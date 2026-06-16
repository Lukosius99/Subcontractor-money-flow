namespace PADS.MoneyFlow.Api.Models;

public sealed class Subcontractor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string CanonicalName { get; set; }
    public string? LegalForm { get; set; }
    public required string BaseName { get; set; }
    public required string NormalizedKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
