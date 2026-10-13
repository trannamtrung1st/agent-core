namespace AgentCore.Contracts.Http;

public sealed record OfficeCellResponse(string Address, string Type, string Value, string? Formula, string ValueStatus, string NumberFormat);
public sealed record OfficePreviewResponse(string Format, string Sha256, IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? Sheets = null, string? Sheet = null,
    string? Range = null, IReadOnlyList<IReadOnlyList<OfficeCellResponse>>? Rows = null, bool EmptyBody = false);
