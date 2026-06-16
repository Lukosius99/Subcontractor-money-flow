namespace PADS.MoneyFlow.Api.Models;

public sealed class ImportWarning
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImportBatchId { get; set; }
    public int? SourceRow { get; set; }
    public required string Message { get; set; }
}
