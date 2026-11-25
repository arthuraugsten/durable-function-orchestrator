using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Prevents concurrent runs of an orchestrator: return it from
/// <c>ActivityOrchestrator.RunLease</c>. Before the first stage the orchestrator acquires a lease keyed
/// by its name, renews it while the pipeline runs, and releases it when the run ends. While one run holds
/// the lease, every other run of the same orchestrator, in any region, is rejected without running its pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The lease is managed by three activities, because the repository does I/O and orchestration code must
/// not. Register them once per app, each forwarding to <see cref="RunLeaseService"/>, under
/// <see cref="AcquireActivityName"/>, <see cref="RenewActivityName"/> and <see cref="ReleaseActivityName"/>.
/// </para>
/// <para>
/// <b>Renewal.</b> While any stage runs (including a polling step that waits for hours, a long activity, or a
/// sub-orchestration) a durable timer renews the lease every <see cref="RenewalSeconds"/>, so a run keeps its
/// lease however long it takes. If a renewal finds that another run took the lease, or the run can no longer
/// renew before its lease expires, the run fails with <see cref="RunLeaseLostException"/> instead of running
/// without the lease.
/// </para>
/// <para>
/// <b>Choosing <see cref="LeaseSeconds"/>.</b> A run that stops renewing (it was terminated, or its host
/// was lost) blocks the orchestrator until its lease expires, so a shorter lease recovers sooner. Each
/// renewal adds a timer and an activity call to the orchestration history, so a longer lease costs less on
/// long runs. For runs of hours, 30 to 60 minutes is a reasonable balance.
/// </para>
/// </remarks>
public sealed record RunLeaseOptions
{
  /// <summary>The default name of the acquire activity.</summary>
  public const string DefaultAcquireActivityName = "AcquireOrchestrationRunLease";

  /// <summary>The default name of the renew activity.</summary>
  public const string DefaultRenewActivityName = "RenewOrchestrationRunLease";

  /// <summary>The default name of the release activity.</summary>
  public const string DefaultReleaseActivityName = "ReleaseOrchestrationRunLease";

  /// <summary>The shortest allowed lease, in seconds.</summary>
  public const int MinLeaseSeconds = 60;

  private readonly int? _renewalSeconds;

  /// <summary>Creates options with a lease of <paramref name="leaseSeconds"/> (at least <see cref="MinLeaseSeconds"/>).</summary>
  public RunLeaseOptions(int leaseSeconds)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, MinLeaseSeconds);
    LeaseSeconds = leaseSeconds;
  }

  /// <summary>How long the lease lasts after it is acquired or renewed, if it isn't renewed again.</summary>
  public int LeaseSeconds { get; }

  /// <summary>
  /// How often a running pipeline renews the lease, in seconds: from 1 to less than <see cref="LeaseSeconds"/>.
  /// The default, a third of the lease, leaves room for one failed renewal before the lease could expire.
  /// </summary>
  public int RenewalSeconds
  {
    get => _renewalSeconds ?? LeaseSeconds / 3;
    init
    {
      ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
      ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, LeaseSeconds);
      _renewalSeconds = value;
    }
  }

  /// <summary>The registered name of the activity that calls <see cref="RunLeaseService.TryAcquireAsync"/>.</summary>
  public string AcquireActivityName { get; init; } = DefaultAcquireActivityName;

  /// <summary>The registered name of the activity that calls <see cref="RunLeaseService.RenewAsync"/>.</summary>
  public string RenewActivityName { get; init; } = DefaultRenewActivityName;

  /// <summary>The registered name of the activity that calls <see cref="RunLeaseService.ReleaseAsync"/>.</summary>
  public string ReleaseActivityName { get; init; } = DefaultReleaseActivityName;

  /// <summary>
  /// Task options (for example a retry policy for transient database errors) for the lease activities.
  /// Null uses the orchestrator's <c>DefaultTaskOptions</c>.
  /// </summary>
  public TaskOptions? TaskOptions { get; init; }
}
