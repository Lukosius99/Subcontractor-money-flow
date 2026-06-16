namespace PADS.MoneyFlow.Api.Models;

public sealed class SubcontractorAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SubcontractorId { get; set; }
    public required string RawName { get; set; }
    public required string NormalizedKey { get; set; }
    public required string CanonicalName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
