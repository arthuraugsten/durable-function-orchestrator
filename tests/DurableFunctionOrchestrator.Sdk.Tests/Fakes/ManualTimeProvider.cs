namespace DurableFunctionOrchestrator.Sdk.Tests.Fakes;

/// <summary>A clock that only moves when the test advances it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
  private DateTimeOffset _now;

  public ManualTimeProvider(DateTimeOffset start)
  {
    _now = start;
  }

  public override DateTimeOffset GetUtcNow() => _now;

  public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A clock that reads the fake orchestration's virtual time, so activity code (such as the lease service)
/// and the orchestration agree on "now".
/// </summary>
internal sealed class OrchestrationTimeProvider : TimeProvider
{
  private readonly FakeOrchestrationContext _context;

  public OrchestrationTimeProvider(FakeOrchestrationContext context)
  {
    _context = context;
  }

  public override DateTimeOffset GetUtcNow() => new(_context.CurrentUtcDateTime);
}
