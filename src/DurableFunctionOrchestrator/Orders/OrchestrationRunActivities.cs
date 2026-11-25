using DurableFunctionOrchestrator.Sdk;
using Microsoft.Azure.Functions.Worker;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>
/// The three run-lease activities (acquire, renew, release) the SDK calls for orchestrators that set <c>RunLease</c>. Registered once
/// for the whole app: the orchestrator name in each request selects the record. They only forward to
/// <see cref="RunLeaseService"/>, which holds the logic.
/// </summary>
public sealed class OrchestrationRunActivities
{
  private readonly RunLeaseService _leases;

  public OrchestrationRunActivities(RunLeaseService leases)
  {
    _leases = leases;
  }

  [Function(RunLeaseOptions.DefaultAcquireActivityName)]
  public Task<RunLeaseResult> AcquireOrchestrationRunLease([ActivityTrigger] RunLeaseRequest request, CancellationToken cancellationToken) =>
    _leases.TryAcquireAsync(request, cancellationToken);

  [Function(RunLeaseOptions.DefaultRenewActivityName)]
  public Task<RunLeaseResult> RenewOrchestrationRunLease([ActivityTrigger] RunLeaseRequest request, CancellationToken cancellationToken) =>
    _leases.RenewAsync(request, cancellationToken);

  [Function(RunLeaseOptions.DefaultReleaseActivityName)]
  public Task<bool> ReleaseOrchestrationRunLease([ActivityTrigger] RunLeaseRelease release, CancellationToken cancellationToken) =>
    _leases.ReleaseAsync(release, cancellationToken);
}
