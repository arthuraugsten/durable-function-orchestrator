using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DurableFunctionOrchestrator.Sdk.Tests.Fakes;

internal sealed record TaskCall(StepKind Kind, string Name, object? Input, TaskOptions? Options);

/// <summary>
/// In-memory orchestration context. Activities and sub-orchestrations are resolved from registered
/// handlers; an exception thrown by a handler surfaces as a <see cref="TaskFailedException"/>, just
/// like a failed task in the real runtime. Retries and replay are not simulated.
/// </summary>
internal sealed class FakeOrchestrationContext : TaskOrchestrationContext
{
  private readonly object? _orchestrationInput;
  private readonly Dictionary<string, Func<object?, object?>> _handlers = new(StringComparer.Ordinal);
  private readonly List<TaskCall> _calls = [];
  private readonly List<object?> _statusHistory = [];

  public FakeOrchestrationContext(object? input = null)
  {
    _orchestrationInput = input;
  }

  public IReadOnlyList<TaskCall> Calls
  {
    get
    {
      lock (_calls)
        return _calls.ToArray();
    }
  }

  public IReadOnlyList<string> CallNames => Calls.Select(call => call.Name).ToArray();

  public IReadOnlyList<OrchestrationProgress> StatusHistory => _statusHistory.Cast<OrchestrationProgress>().ToArray();

  public override TaskName Name => "TestOrchestration";

  public override string InstanceId => "test-instance";

  public override ParentOrchestrationInstance? Parent => null;

  public override DateTime CurrentUtcDateTime => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

  public override T GetInput<T>() where T : default => (T)_orchestrationInput!;

  public override Task<TResult> CallActivityAsync<TResult>(TaskName name, object? input = null, TaskOptions? options = null) =>
    InvokeAsync<TResult>(StepKind.Activity, name.Name, input, options);

  public override Task<TResult> CallSubOrchestratorAsync<TResult>(TaskName orchestratorName, object? input = null, TaskOptions? options = null) =>
    InvokeAsync<TResult>(StepKind.SubOrchestration, orchestratorName.Name, input, options);

  public override void SetCustomStatus(object? customStatus) => _statusHistory.Add(customStatus);

  public override Task CreateTimer(DateTime fireAt, CancellationToken cancellationToken) => throw new NotSupportedException();

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
      _calls.Add(new TaskCall(kind, name, input, options));
      taskId = _calls.Count;
    }

    if (!_handlers.TryGetValue(name, out Func<object?, object?>? handler))
      throw new InvalidOperationException($"Test setup error: no handler registered for '{name}'.");

    object? result;
    try
    {
      result = handler(input);
    }
    catch (Exception ex)
    {
      throw new TaskFailedException(name, taskId, new TaskFailureDetails(ex.GetType().FullName!, ex.Message, null, null, null));
    }

    return (TResult)result!;
  }
}
