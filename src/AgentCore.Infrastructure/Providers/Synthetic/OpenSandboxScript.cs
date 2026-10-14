using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Deterministic model fixture, using the normal authorized tool/Run/Artifact path.
// Execution itself is REAL OpenSandbox, never fabricated Python/Node/Office text.
internal static class OpenSandboxScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var last = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
        if (last < 0) return null;
        var headless = request.Messages.Any(m => m.Role == ModelRole.System && m.Text.StartsWith("Bounded background Session task.", StringComparison.Ordinal));
        var text = request.Messages[last].Text;
        // Background task intent is wrapped by PromptContextBuilder's evidence
        // boundary; direct Chat keeps its exact fixture prefix. Both still use
        // only offered capabilities and the real owned execution/complete path.
        var scenario = text.StartsWith("synthetic-sandbox:",StringComparison.Ordinal) ? text["synthetic-sandbox:".Length..].Trim()
            : headless && text.Contains("synthetic-sandbox:demo",StringComparison.Ordinal) ? "demo" : null;
        if (scenario is null) return null;
        var results = request.Messages.Skip(last+1).Where(m => m.Role == ModelRole.Tool).ToArray();
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            using var parsed = JsonDocument.Parse(result.Text);
            if (parsed.RootElement.TryGetProperty("error",out var error) && error.ValueKind == JsonValueKind.String)
                return [new ModelTextDelta("Real sandbox scenario did not complete: " + result.Text),new ModelCompleted(ModelStopReason.Completed)];
            if (scenario == "demo" && index < 5 && (!parsed.RootElement.TryGetProperty("status",out var status) || status.GetString() != "Completed"
                || !parsed.RootElement.TryGetProperty("exitCode",out var exit) || exit.ValueKind != JsonValueKind.Number || (index == 1 ? exit.GetInt32() == 0 : exit.GetInt32() != 0)))
                return [new ModelTextDelta("Real sandbox demo stopped at an unexpected command outcome: " + result.Text),new ModelCompleted(ModelStopReason.Completed)];
        }
        string? name = null; object? args = null;
        if (scenario == "demo")
        {
            (string? Name, object? Args) step = results.Length switch {
                0 => (ToolCatalog.SandboxExec, new { command = "python --version && node --version" }),
                1 => (ToolCatalog.SandboxExec, new { command = "printf 'def add(a,b): return a-b\n' > maths.py; printf 'from maths import add\ndef test_add(): assert add(2,3)==5\n' > test_maths.py; python -m pytest -q" }),
                2 => (ToolCatalog.SandboxExec, new { command = "printf 'def add(a,b): return a+b\n' > maths.py; python -m pytest -q" }),
                3 => (ToolCatalog.SandboxExec, new { command = "mkdir -p node-project; printf '{\"scripts\":{\"test\":\"node --test\"}}' > node-project/package.json; printf 'const test=require(\"node:test\"),assert=require(\"node:assert/strict\");test(\"sum\",()=>assert.equal(2+3,5));' > node-project/sum.test.js; cd node-project && npm test" }),
                4 => (ToolCatalog.SandboxExec, new { argv = new[] { "python","-I","-c","from openpyxl import Workbook,load_workbook; from docx import Document; w=Workbook(); w.active.append(['Total',5]); w.save('report.xlsx'); d=Document(); d.add_heading('Report',0); d.add_paragraph('Total: 5'); d.save('report.docx'); assert load_workbook('report.xlsx').active['B1'].value==5; assert Document('report.docx').paragraphs[1].text=='Total: 5'; print('Office files validated')" } }),
                5 => (ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/working/report.xlsx", displayName = "report.xlsx", contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" }),
                6 => (ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/working/report.docx", displayName = "report.docx", contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document" }),
                _ => (null,null)
            };
            (name,args) = step;
        }
        else if (results.Length == 0)
        {
            name = ToolCatalog.SandboxExec;
            args = scenario switch {
                "long" => new { command = "printf 'started\n'; sleep 31; printf ' beyond-30-seconds'", timeoutSeconds = 60 },
                "cancel" => new { command = "printf 'started\n'; sleep 120; printf should-not-finish", timeoutSeconds = 120 },
                "retain" => new { command = "cat maths.py; test -f report.xlsx && test -f report.docx", timeoutSeconds = 30 },
                "init" => new { command = "cat init-count.txt", timeoutSeconds = 30 },
                "background" => new { command = "printf background-started; sleep 120", background = true },
                _ => null
            };
        }
        if (args is null && name is not null) return [new ModelTextDelta("Unknown Synthetic sandbox scenario."),new ModelCompleted(ModelStopReason.Completed)];
        if (name is not null && request.Tools?.Any(t => t.Name == name) == true)
            return [new ModelToolCallEvent(new("sandbox-proof-"+results.Length,name,JsonSerializer.Serialize(args))),new ModelCompleted(ModelStopReason.ToolCalls)];
        if (name is not null) return [new ModelTextDelta("Sandbox capability unavailable. Register and verify an authorized prebuilt image and start a new Run."),new ModelCompleted(ModelStopReason.Completed)];
        if (scenario == "demo" && results.Length == 7)
        {
            var blocks = new List<ModelResponseBlock>();
            foreach (var result in results.Where(r => r.Name == ToolCatalog.ArtifactsCreateFromWorkspace))
            {
                using var doc = JsonDocument.Parse(result.Text);
                if (doc.RootElement.TryGetProperty("artifactId",out var id) && id.ValueKind == JsonValueKind.String)
                    blocks.Add(new(ModelResponseBlockKind.ArtifactReference,ArtifactId:id.GetString()));
            }
            if (blocks.Count != 2) return [new ModelTextDelta("Office publication was not verified."),new ModelCompleted(ModelStopReason.Completed)];
            if (headless)
                return [new ModelToolCallEvent(new("sandbox-proof-complete",ToolCatalog.WorkComplete,JsonSerializer.Serialize(new { outcome="Response",summary="Real background Workbench completed: Python fixed, Node passed, Excel and Word published.",attentionRequired=false }))),new ModelCompleted(ModelStopReason.ToolCalls)];
            return [new ModelSemanticResponseReady(new("Real sandbox demo completed: Python failure fixed, Node test passed, Excel and Word files validated and published.",new(ModelSpeechMode.None,null),blocks)),new ModelCompleted(ModelStopReason.Completed)];
        }
        return [new ModelTextDelta("Real sandbox scenario completed. Results:\n" + string.Join("\n",results.Select(r => r.Text))),new ModelCompleted(ModelStopReason.Completed)];
    }
}
