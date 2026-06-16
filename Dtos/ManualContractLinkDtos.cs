namespace PADS.MoneyFlow.Api.Dtos;

public sealed class ManualContractLinkRequest
{
    public string? SourceSubcontractorName { get; set; }
    public string? SourceObjectNumber { get; set; }
    public string? TargetContractRowKey { get; set; }
}

public sealed class ObjectAssignmentRequest
{
    public string? SubcontractorName { get; set; }
    public string? SourceObjectNumber { get; set; }
    public string? TargetObjectNumber { get; set; }
}
