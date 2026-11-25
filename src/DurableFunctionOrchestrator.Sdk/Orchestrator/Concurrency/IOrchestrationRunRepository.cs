namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Stores one <see cref="OrchestrationRunRecord"/> per orchestrator, keyed by orchestrator name.
/// <see cref="RunLeaseService"/> uses it to let only one run of an orchestrator hold the lease at a time.
/// </summary>
/// <remarks>
/// <para>
/// Both writes must be <b>atomic and conditional</b>: that is what makes the lease safe when several
/// instances race for it. For example, Azure Cosmos DB <c>CreateItemAsync</c> and
/// <c>ReplaceItemAsync</c> with <c>IfMatchEtag</c>, or SQL <c>INSERT</c> on the primary key and
/// <c>UPDATE ... WHERE ETag = @etag</c>.
/// </para>
/// <para>
/// Across regions, the store must apply those conditions globally: a single write region, or strong
/// consistency. With multi-region writes and last-writer-wins conflict resolution, two regions can
/// each acquire the lease.
/// </para>
/// <para>
/// The repository is called from activities, never from orchestration code, because orchestrations
/// replay and must not do I/O.
/// </para>
/// </remarks>
public interface IOrchestrationRunRepository
{
  /// <summary>Gets the record for <paramref name="orchestratorName"/>, or null if that orchestrator has never run.</summary>
  Task<OrchestrationRunRecord?> GetAsync(string orchestratorName, CancellationToken cancellationToken = default);

  /// <summary>
  /// Inserts <paramref name="record"/> if no record exists for its orchestrator name, assigning a new ETag.
  /// Returns false, without writing, if one already exists.
  /// </summary>
  Task<bool> TryInsertAsync(OrchestrationRunRecord record, CancellationToken cancellationToken = default);

  /// <summary>
  /// Replaces the stored record with <paramref name="record"/>, assigning a new ETag, only if the stored
  /// record's ETag is still <paramref name="expectedETag"/>. Returns false, without writing, if another
  /// write happened in between or the record is missing.
  /// </summary>
  Task<bool> TryReplaceAsync(OrchestrationRunRecord record, string expectedETag, CancellationToken cancellationToken = default);
}
