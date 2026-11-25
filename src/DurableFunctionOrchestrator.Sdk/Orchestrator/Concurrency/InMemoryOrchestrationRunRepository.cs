namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// A fake, in-memory <see cref="IOrchestrationRunRepository"/> for tests and local development.
/// </summary>
/// <remarks>
/// It is safe for concurrent use within one process, but its records live only in that process: it
/// can't prevent concurrent runs across several hosts or regions. Use a shared database in production.
/// </remarks>
public sealed class InMemoryOrchestrationRunRepository : IOrchestrationRunRepository
{
  private readonly Lock _lock = new();
  private readonly Dictionary<string, OrchestrationRunRecord> _records = new(StringComparer.Ordinal);

  /// <inheritdoc />
  public Task<OrchestrationRunRecord?> GetAsync(string orchestratorName, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(orchestratorName);

    lock (_lock)
      return Task.FromResult(_records.GetValueOrDefault(orchestratorName));
  }

  /// <inheritdoc />
  public Task<bool> TryInsertAsync(OrchestrationRunRecord record, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(record);

    lock (_lock)
      return Task.FromResult(_records.TryAdd(record.OrchestratorName, record with { ETag = NewETag() }));
  }

  /// <inheritdoc />
  public Task<bool> TryReplaceAsync(OrchestrationRunRecord record, string expectedETag, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(record);
    ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);

    lock (_lock)
    {
      if (!_records.TryGetValue(record.OrchestratorName, out OrchestrationRunRecord? current) || current.ETag != expectedETag)
        return Task.FromResult(false);

      _records[record.OrchestratorName] = record with { ETag = NewETag() };
      return Task.FromResult(true);
    }
  }

  private static string NewETag() => Guid.NewGuid().ToString("N");
}
