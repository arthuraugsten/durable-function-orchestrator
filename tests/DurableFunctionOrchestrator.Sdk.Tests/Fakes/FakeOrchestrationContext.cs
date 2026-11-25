using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DurableFunctionOrchestrator.Sdk.Tests.Fakes;

internal sealed record TaskCall(StepKind Kind, string Name, object? Input, TaskOptions? Options, DateTime At);

/// <summary>
/// In-memory orchestration context. Activities and sub-orchestrations are resolved from registered
/// handlers; an exception thrown by a handler surfaces as a <see cref="TaskFailedException"/>, just
/// like a failed task in the real runtime. Retries and replay are not simulated.
/// </summary>
/// <remarks>
/// <para>
/// By default, everything completes at once and durable timers move <see cref="CurrentUtcDateTime"/> forward
/// to their fire time.
/// </para>
/// <para>
/// Inside <see cref="Run{TResult}"/>, the context uses <b>virtual time</b> instead, like the real runtime: the
/// orchestration runs on one thread, timers fire in fire-time order, activities registered with
/// <see cref="Takes"/> take that long, and the clock only moves when the orchestration is waiting. Use it for
/// anything that races tasks against timers, such as run-lease renewal.
/// </para>
/// </remarks>
internal sealed class FakeOrchestrationContext : TaskOrchestrationContext
{
  private readonly object? _orchestrationInput;
  private readonly Dictionary<string, Func<object?, object?>> _handlers = new(StringComparer.Ordinal);
  private readonly Dictionary<string, TimeSpan> _durations = new(StringComparer.Ordinal);
  private readonly List<TaskCall> _calls = [];
  private readonly List<object?> _statusHistory = [];
  private readonly List<DateTime> _timers = [];
  private readonly List<Scheduled> _scheduled = [];
  private DateTime _currentUtcDateTime = Start;
  private VirtualTimePump? _pump;
  private long _sequence;

  public FakeOrchestrationContext(object? input = null)
  {
    _orchestrationInput = input;
  }

  public IReadOnlyList<TaskCall> Calls
  {
    get
    {
      lock (_calls)
        return [.. _calls];
    }
  }

  /// <summary>The clock at the start of every test.</summary>
  public static DateTime Start { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  public IReadOnlyList<string> CallNames => Calls.Select(call => call.Name).ToArray();

  public IReadOnlyList<OrchestrationProgress> StatusHistory => _statusHistory.Cast<OrchestrationProgress>().ToArray();

  /// <summary>Fire times of the durable timers created, in seconds after <see cref="Start"/>.</summary>
  public IReadOnlyList<double> TimerSeconds
  {
    get
    {
      lock (_timers)
        return _timers.Select(fireAt => (fireAt - Start).TotalSeconds).ToArray();
    }
  }

  /// <summary>When each call to <paramref name="name"/> started, in seconds after <see cref="Start"/>.</summary>
  public IReadOnlyList<double> CallSeconds(string name) =>
    Calls.Where(call => call.Name == name).Select(call => (call.At - Start).TotalSeconds).ToArray();

  public override TaskName Name => "TestOrchestration";

  public override string InstanceId => "test-instance";

  public override ParentOrchestrationInstance? Parent => null;

  public override DateTime CurrentUtcDateTime
  {
    get
    {
      lock (_timers)
        return _currentUtcDateTime;
    }
  }

  public override bool IsReplaying => false;

  protected override ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

  /// <summary>Registers the behaviour of an activity or sub-orchestrator.</summary>
  public FakeOrchestrationContext Handle(string name, Func<object?, object?> handler)
  {
    _handlers[name] = handler;
    return this;
  }

  /// <summary>Registers an activity or sub-orchestrator that always returns <paramref name="result"/>.</summary>
  public FakeOrchestrationContext Returns(string name, object? result) => Handle(name, _ => result);

  /// <summary>Registers an activity or sub-orchestrator that always fails.</summary>
  public FakeOrchestrationContext Fails(string name, string message = "boom") =>
    Handle(name, _ => throw new InvalidOperationException(message));

  /// <summary>
  /// In virtual time, makes each call to <paramref name="name"/> take <paramref name="duration"/>. Its handler
  /// runs when the call completes, so it sees the clock at that moment.
  /// </summary>
  public FakeOrchestrationContext Takes(string name, TimeSpan duration)
  {
    _durations[name] = duration;
    return this;
  }

  /// <summary>
  /// Runs <paramref name="orchestration"/> in virtual time until it completes, and returns its result (or
  /// throws its exception). Throws if the orchestration waits on nothing that can complete.
  /// </summary>
  public TResult Run<TResult>(Func<Task<TResult>> orchestration)
  {
    var pump = new VirtualTimePump();
    SynchronizationContext? previous = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(pump);
    _pump = pump;

    try
    {
      Task<TResult> task = orchestration();
      while (true)
      {
        pump.RunPending();
        if (task.IsCompleted)
          return task.GetAwaiter().GetResult();

        if (!CompleteNextScheduled())
          throw new InvalidOperationException("The orchestration is waiting, but no timer or activity is pending.");
      }
    }
    finally
    {
      _pump = null;
      SynchronizationContext.SetSynchronizationContext(previous);
    }
  }

  public override T GetInput<T>() where T : default => (T)_orchestrationInput!;

  public override Task<TResult> CallActivityAsync<TResult>(TaskName name, object? input = null, TaskOptions? options = null) =>
    InvokeAsync<TResult>(StepKind.Activity, name.Name, input, options);

  public override Task<TResult> CallSubOrchestratorAsync<TResult>(TaskName orchestratorName, object? input = null, TaskOptions? options = null) =>
    InvokeAsync<TResult>(StepKind.SubOrchestration, orchestratorName.Name, input, options);

  public override void SetCustomStatus(object? customStatus) => _statusHistory.Add(customStatus);

  public override async Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken)
  {
    if (_pump is not null)
    {
      lock (_timers)
        _timers.Add(fireAt);

      var fired = new TaskCompletionSource();
      Scheduled timer = Schedule(fireAt, () => fired.TrySetResult());
      using (cancellationToken.Register(() =>
      {
        Unschedule(timer);
        fired.TrySetCanceled(cancellationToken);
      }))
      {
        await fired.Task;
      }

      return;
    }

    await Task.Yield();

    lock (_timers)
    {
      _timers.Add(fireAt);
      if (fireAt > _currentUtcDateTime)
        _currentUtcDateTime = fireAt;
    }
  }

  public override Task<T> WaitForExternalEvent<T>(string eventName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

  public override void SendEvent(string instanceId, string eventName, object payload) => throw new NotSupportedException();

  public override void ContinueAsNew(object? newInput = null, bool preserveUnprocessedEvents = true) => throw new NotSupportedException();

  public override Guid NewGuid() => Guid.NewGuid();

  private async Task<TResult> InvokeAsync<TResult>(StepKind kind, string name, object? input, TaskOptions? options)
  {
    // Yield so steps in a parallel stage genuinely interleave, as they would in the runtime.
    await Task.Yield();

    int taskId;
    lock (_calls)
    {
      _calls.Add(new TaskCall(kind, name, input, options, CurrentUtcDateTime));
      taskId = _calls.Count;
    }

    if (!_handlers.TryGetValue(name, out Func<object?, object?>? handler))
      throw new InvalidOperationException($"Test setup error: no handler registered for '{name}'.");

    if (_pump is not null && _durations.TryGetValue(name, out TimeSpan duration) && duration > TimeSpan.Zero)
    {
      var completed = new TaskCompletionSource<object?>();
      Schedule(CurrentUtcDateTime + duration, () =>
      {
        try
        {
          completed.SetResult(handler(input));
        }
        catch (Exception ex)
        {
          completed.SetException(TaskFailure(name, taskId, ex));
        }
      });

      return (TResult)(await completed.Task)!;
    }

    try
    {
      return (TResult)handler(input)!;
    }
    catch (Exception ex)
    {
      throw TaskFailure(name, taskId, ex);
    }
  }

  private static TaskFailedException TaskFailure(string name, int taskId, Exception ex) =>
    new(name, taskId, new TaskFailureDetails(ex.GetType().FullName!, ex.Message, null, null, null));

  private Scheduled Schedule(DateTime dueAt, Action complete)
  {
    lock (_scheduled)
    {
      var item = new Scheduled(dueAt, ++_sequence, complete);
      _scheduled.Add(item);
      return item;
    }
  }

  private void Unschedule(Scheduled item)
  {
    lock (_scheduled)
      _scheduled.Remove(item);
  }

  // Completes the earliest pending timer or activity (first scheduled wins a tie), moving the clock to it.
  private bool CompleteNextScheduled()
  {
    Scheduled? next;
    lock (_scheduled)
    {
      next = _scheduled.OrderBy(item => item.DueAt).ThenBy(item => item.Sequence).FirstOrDefault();
      if (next is null)
        return false;

      _scheduled.Remove(next);
    }

    lock (_timers)
    {
      if (next.DueAt > _currentUtcDateTime)
        _currentUtcDateTime = next.DueAt;
    }

    next.Complete();
    return true;
  }

  private sealed record Scheduled(DateTime DueAt, long Sequence, Action Complete);

  /// <summary>Runs every continuation on the thread that pumps it, one at a time, like the Durable Task runtime.</summary>
  private sealed class VirtualTimePump : SynchronizationContext
  {
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _work = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
      lock (_work)
        _work.Enqueue((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public void RunPending()
    {
      while (true)
      {
        (SendOrPostCallback Callback, object? State) item;
        lock (_work)
        {
          if (_work.Count == 0)
            return;

          item = _work.Dequeue();
        }

        item.Callback(item.State);
      }
    }
  }
}
