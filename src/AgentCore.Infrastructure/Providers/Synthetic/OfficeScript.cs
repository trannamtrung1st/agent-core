using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Deterministic Office acceptance through the normal authorized model/tool loop.
internal static class OfficeScript
{
    public const string Marker = "synthetic-office: q3-sales";
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var last = request.Messages.ToList().FindLastIndex(message => message.Role == ModelRole.User);
        if (last < 0) return null;
        var fixtures = request.Messages[last].Text.Trim() == "synthetic-office: q3-sales-drive-trash-fixtures";
        var drive = fixtures || request.Messages[last].Text.Trim() == "synthetic-office: q3-sales-drive";
        if (!drive && request.Messages[last].Text.Trim() != Marker) return null;
        var results = request.Messages.Skip(last + 1).Where(message => message.Role == ModelRole.Tool).ToArray();
        if (results.Any(result => { using var json = JsonDocument.Parse(result.Text); return json.RootElement.TryGetProperty("error", out _); }))
            return Done("Office fixture stopped after a tool failure. Inspect the tool result; no success is claimed.");
        if (results.Length == 0) return Call(request, ToolCatalog.CapabilitiesLoad, new { query = "office", limit = 8 });
        if (results.Length == 1) return Call(request, ToolCatalog.OfficeSpreadsheetCreate, new
        {
            path = "/home/q3-sales.xlsx",
            sheets = new[] {
            new {name="Sales",rows=new object[][] { [new {type="text",text="Region"},new {type="text",text="Revenue"},new {type="text",text="Growth"}], [new {type="text",text="North"},new {type="number",number=1250,format="currency"},new {type="number",number=0.125,format="percentage"}], [new {type="text",text="South"},new {type="number",number=3250,format="currency"},new {type="number",number=0.2,format="percentage"}], [new {type="text",text="Total"},new {type="blank",formula="SUM(B2:B3)",format="currency"},new {type="blank"}] }},
            new {name="Assumptions",rows=new object[][] { [new {type="text",text="Report date"},new {type="date",text="2026-10-13",format="date"}], [new {type="text",text="Account"},new {type="text",text="0001"}] }} }
        });
        if (results.Length == 2) return Call(request, ToolCatalog.OfficeSpreadsheetInspect, new { path = "/home/q3-sales.xlsx", sheet = "Sales", range = "A1:C4" });
        if (results.Length == 3) return Call(request, ToolCatalog.OfficeDocumentCreate, new
        {
            path = "/home/q3-report.docx",
            title = "Q3 sales report",
            sections = new[] {
            new {heading="Summary",paragraphs=new[]{"Synthetic demonstration dataset: North revenue $1,250; South revenue $3,250.","The workbook stores its total formula. Inspection does not recalculate Excel formulas."},table=new[]{new[]{"Region","Revenue","Growth"},new[]{"North","$1,250","12.5%"},new[]{"South","$3,250","20%"}},pageBreakBefore=false},
            new {heading="Recommendations",paragraphs=new[]{"Review regional performance using the attached workbook.","Keep original Office bytes available for normal Office clients."},table=Array.Empty<string[]>(),pageBreakBefore=true},
            new {heading="Method and limitations",paragraphs=new[]{"This report uses deterministic Synthetic data.","Document preview is best effort; formula values are labeled as cached or unavailable."},table=Array.Empty<string[]>(),pageBreakBefore=true} }
        });
        if (results.Length == 4) return Call(request, ToolCatalog.OfficeDocumentInspect, new { path = "/home/q3-report.docx", limit = 20 });
        if (results.Length == 5) return Call(request, ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/home/q3-sales.xlsx", displayName = "q3-sales.xlsx" });
        if (results.Length == 6) return Call(request, ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/home/q3-report.docx", displayName = "q3-report.docx" });
        using var spreadsheet = JsonDocument.Parse(results[5].Text); using var document = JsonDocument.Parse(results[6].Text);
        if (!spreadsheet.RootElement.TryGetProperty("artifactId", out var spreadsheetId) || !document.RootElement.TryGetProperty("artifactId", out var documentId)) return Done("Office publication did not return verified Artifact identities.");
        if (drive)
        {
            if (results.Length == 7) return Call(request, ToolCatalog.CapabilitiesLoad, new { query = "drive", limit = 8 });
            if (results.Length == 8) return Call(request, "drive.accounts", new { });
            using var accounts = JsonDocument.Parse(results[8].Text); var ready = accounts.RootElement.GetProperty("accounts").EnumerateArray().Where(a => a.GetProperty("available").GetBoolean()).ToArray();
            if (ready.Length != 1) return Done("Choose exactly one connected bound Drive account for this deterministic fixture. Local Office files were created; no Drive success is claimed.");
            if (fixtures && !ready[0].GetProperty("synthetic").GetBoolean()) return Done("Disposable Drive fixtures require a Synthetic account. Local Office files were created; no real Drive mutation was requested.");
            var account = ready[0].GetProperty("accountRef").GetString(); var epoch = ready[0].GetProperty("authEpoch").GetInt64();
            if (results.Length == 9) return Call(request, "drive.create_folder", new { accountRef = account, authEpoch = epoch, name = "Q3 Sales" });
            if (results.Length == 10) return Call(request, ToolCatalog.CapabilitiesLoad, new { query = "drive.upload", limit = 1 });
            using var folder = JsonDocument.Parse(results[9].Text); var folderId = folder.RootElement.GetProperty("receipt").GetProperty("file").GetProperty("id").GetString();
            if (results.Length == 11) { using var source = JsonDocument.Parse(results[3].Text); return Call(request, "drive.upload", new { accountRef = account, authEpoch = epoch, path = "/home/q3-report.docx", folderId }); }
            if (results.Length == 12) { using var source = JsonDocument.Parse(results[1].Text); return Call(request, "drive.upload", new { accountRef = account, authEpoch = epoch, path = "/home/q3-sales.xlsx", folderId }); }
            using var reportReceipt = JsonDocument.Parse(results[11].Text); using var workbookReceipt = JsonDocument.Parse(results[12].Text);
            if (reportReceipt.RootElement.GetProperty("receipt").GetProperty("status").GetString() != "Succeeded" || workbookReceipt.RootElement.GetProperty("receipt").GetProperty("status").GetString() != "Succeeded") return Done("Drive completion is uncertain; inspect the known operations. No delivery is claimed.");
            if (fixtures && ready[0].GetProperty("synthetic").GetBoolean() && results.Length < 21)
            {
                using var source = JsonDocument.Parse(results[3].Text);
                return Call(request, "drive.upload", new { accountRef = account, authEpoch = epoch, path = "/home/q3-report.docx", folderId,
                    name = "AgentCoreUat-" + (results.Length - 13) + "-" + string.Concat(Enumerable.Repeat("長い四半期報告書", 20)) + ".docx" });
            }
            return Done($"Synthetic Q3 Office files created, inspected and saved as new files in app-authorized Drive folder {folderId}. Verified report ID: {reportReceipt.RootElement.GetProperty("receipt").GetProperty("file").GetProperty("id").GetString()}; workbook ID: {workbookReceipt.RootElement.GetProperty("receipt").GetProperty("file").GetProperty("id").GetString()}. Formulas are stored; cached totals may be unavailable. [[artifact:{spreadsheetId.GetString()}]] [[artifact:{documentId.GetString()}]]");
        }
        return Done($"Synthetic Q3 workbook and report created and inspected. Formulas are stored; cached totals may be unavailable. [[artifact:{spreadsheetId.GetString()}]] [[artifact:{documentId.GetString()}]]");
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(ModelRequest request, string name, object args) => request.Tools?.Any(tool => tool.Name == name) == true
        ? [new ModelToolCallEvent(new("office-proof-" + resultsOrdinal(request) + "-" + name, name, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)]
        : Done("Office fixture requires explicitly authorized and available " + name + ". No output is claimed.");
    private static int resultsOrdinal(ModelRequest request) => request.Messages.Count(message => message.Role == ModelRole.Tool);
    private static IReadOnlyList<ModelGenerationEvent> Done(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
}
