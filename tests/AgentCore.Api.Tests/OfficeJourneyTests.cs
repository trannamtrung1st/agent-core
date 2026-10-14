using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.DependencyInjection;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AgentCore.Api.Tests;

public sealed class OfficeJourneyTests(AgentCoreApiFactory factory) : IClassFixture<AgentCoreApiFactory>
{
    [Fact]
    public async Task Owned_DOCX_with_inert_hyperlink_can_be_inspected_previewed_and_safely_edited()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var created = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 26 });
        created.EnsureSuccessStatusCode();
        var instance = (await created.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!;
        var response = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = instance.InstanceId, mode = "text" });
        response.EnsureSuccessStatusCode();
        using var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessionId = session.RootElement.GetProperty("sessionId").GetGuid();
        var definition = (await factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Definition;
        var office = factory.Services.GetRequiredService<IOfficeProcessor>();
        using var source = new MemoryStream();
        source.Write(office.CreateDocument(new(null, [new(null, ["Editable business text."], null)]), default).Bytes);
        source.Position = 0;
        using (var document = WordprocessingDocument.Open(source, true))
        {
            var main = document.MainDocumentPart!;
            main.AddHyperlinkRelationship(new Uri("https://example.test/business-report"), true, "business-link");
            main.Document!.Body!.InsertBefore(new Paragraph(new Hyperlink(new Run(new Text("Read the report"))) { Id = "business-link" }), main.Document!.Body.GetFirstChild<SectionProperties>());
            main.Document!.Save();
        }
        var path = "/home/business-links.docx";
        (await client.PutAsync($"/api/v2/sessions/{sessionId}/workspace/content?path={path}", new ByteArrayContent(source.ToArray()))).EnsureSuccessStatusCode();
        var tools = factory.Services.GetRequiredService<SessionToolExecutor>();
        async Task<JsonElement> Execute(string name, object args)
        {
            var result = await tools.ExecuteAsync(definition, sessionId, new ModelToolCall(Guid.NewGuid().ToString(), name, JsonSerializer.Serialize(args)), 8192,
                admission: new(false, TriggerKind.UserTurn, AgentInstanceId: Guid.Parse(instance.InstanceId), OwnedSessionId: sessionId));
            using var json = JsonDocument.Parse(result.Text); return json.RootElement.Clone();
        }
        var inspection = (await Execute(ToolCatalog.OfficeDocumentInspect, new { path })).GetProperty("inspection");
        Assert.Contains("Read the report", inspection.ToString());
        var elements = inspection.GetProperty("elements").EnumerateArray().ToArray();
        var hash = inspection.GetProperty("sha256").GetString();
        var linked = elements.Single(e => e.GetProperty("text").GetString() == "Read the report").GetProperty("selector").GetString();
        var unsafeEdit = await Execute(ToolCatalog.OfficeDocumentEdit, new { path, expectedSha256 = hash, operations = new[] { new { operation = "replace_paragraph", selector = linked, text = "Must not replace hyperlink" } } });
        Assert.Equal("office_operation_unsupported", unsafeEdit.GetProperty("error").GetString());
        var selector = elements.Single(e => e.GetProperty("text").GetString() == "Editable business text.").GetProperty("selector").GetString();
        var edited = await Execute(ToolCatalog.OfficeDocumentEdit, new { path, expectedSha256 = hash, operations = new[] { new { operation = "replace_paragraph", selector, text = "Updated business text." } } });
        Assert.False(edited.TryGetProperty("error", out _), edited.ToString());
        var page = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{instance.InstanceId}/workspace"))!;
        var item = page.Items.Single(i => !i.Directory && i.LogicalPath.EndsWith("business-links.docx", StringComparison.Ordinal));
        var preview = await client.GetAsync($"/api/v2/agent-instances/{instance.InstanceId}/workspace/{item.ItemId}/office-preview");
        preview.EnsureSuccessStatusCode(); Assert.True(preview.Headers.CacheControl!.NoStore);
        var bytes = await client.GetByteArrayAsync($"/api/v2/agent-instances/{instance.InstanceId}/workspace/{item.ItemId}/content");
        using var reopened = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        Assert.Equal("https://example.test/business-report", reopened.MainDocumentPart!.HyperlinkRelationships.Single().Uri.ToString());
        Assert.Contains("Updated business text.", reopened.MainDocumentPart.Document!.InnerText);
        Assert.Equal("Read the report", reopened.MainDocumentPart.Document!.Descendants<Hyperlink>().Single().InnerText);
    }

    [Fact]
    public async Task Published_office_capability_reads_sparse_owned_workbook_and_rechecks_revision_on_continuation()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var created = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 26 });
        created.EnsureSuccessStatusCode();
        var instance = (await created.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!;
        var response = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = instance.InstanceId, mode = "text" });
        response.EnsureSuccessStatusCode();
        using var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessionId = session.RootElement.GetProperty("sessionId").GetGuid();
        var definition = (await factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Definition;
        var tools = factory.Services.GetRequiredService<SessionToolExecutor>();
        async Task<JsonElement> Execute(string name, object args)
        {
            var result = await tools.ExecuteAsync(definition, sessionId, new ModelToolCall(Guid.NewGuid().ToString(), name, JsonSerializer.Serialize(args)), 4096,
                admission: new(false, TriggerKind.UserTurn, AgentInstanceId: Guid.Parse(instance.InstanceId), OwnedSessionId: sessionId));
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result.Text), 1, 4096);
            using var json = JsonDocument.Parse(result.Text); return json.RootElement.Clone();
        }
        var path = "/home/sparse-continuation.xlsx";
        var file = await Execute(ToolCatalog.OfficeSpreadsheetCreate, new { path, sheets = new[] {
            new { name = "First", rows = Enumerable.Range(1, 100).Select(i => new[] { new { type = "number", number = i } }).ToArray() },
            new { name = "Second", rows = new[] { new[] { new { type = "number", number = 101 } } } } } });
        Assert.False(file.TryGetProperty("error", out _), file.ToString());
        var seen = new List<string>(); string? cursor = null; string? initialCursor = null;
        for (var call = 0; call < 30; call++)
        {
            var result = await Execute(ToolCatalog.OfficeSpreadsheetInspect, new { path, cursor });
            Assert.False(result.TryGetProperty("error", out _), result.ToString());
            var page = result.GetProperty("inspection");
            Assert.Equal("sparse", page.GetProperty("mode").GetString());
            foreach (var cell in page.GetProperty("cells").EnumerateArray())
                seen.Add(page.GetProperty("sheet").GetString() + "!" + cell.GetProperty("address").GetString());
            cursor = page.GetProperty("nextCursor").GetString(); initialCursor ??= cursor;
            if (cursor is null) { Assert.Equal("partial", page.GetProperty("coverage").GetString()); Assert.True(page.GetProperty("traversalComplete").GetBoolean()); break; }
        }
        Assert.Null(cursor);
        Assert.NotNull(initialCursor);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => "First!A" + i).Append("Second!A1"), seen);
        Assert.Equal("invalid", (await Execute(ToolCatalog.OfficeSpreadsheetInspect, new { path, cursor = initialCursor, range = "A1" })).GetProperty("error").GetString());
        var changed = await Execute(ToolCatalog.OfficeSpreadsheetEdit, new { path, expectedSha256 = file.GetProperty("sha256").GetString(), operations = new[] {
            new { operation = "set_cells", sheet = "First", range = "A1", cells = new[] { new[] { new { type = "number", number = 999 } } } } } });
        Assert.False(changed.TryGetProperty("error", out _), changed.ToString());
        Assert.Equal("office_revision_conflict", (await Execute(ToolCatalog.OfficeSpreadsheetInspect, new { path, cursor = initialCursor })).GetProperty("error").GetString());
        var recovered = (await Execute(ToolCatalog.OfficeSpreadsheetInspect, new { path })).GetProperty("inspection");
        Assert.Equal("999", recovered.GetProperty("cells")[0].GetProperty("value").GetString());
        Assert.Equal(changed.GetProperty("sha256").GetString(), recovered.GetProperty("sha256").GetString());
    }

    [Theory]
    [InlineData("Z1:Z10")]
    [InlineData("A20:B30")]
    [InlineData("Z20:Z30")]
    public async Task Workspace_spreadsheet_inspection_outside_used_range_returns_metadata_without_continuation(string range)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var created = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 26 });
        created.EnsureSuccessStatusCode();
        var instance = (await created.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!;
        var response = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = instance.InstanceId, mode = "text" });
        response.EnsureSuccessStatusCode();
        using var session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessionId = session.RootElement.GetProperty("sessionId").GetGuid();
        var definition = (await factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Definition;
        var tools = factory.Services.GetRequiredService<SessionToolExecutor>();
        async Task<JsonElement> Execute(string tool, object args)
        {
            var result = await tools.ExecuteAsync(definition, sessionId, new ModelToolCall(Guid.NewGuid().ToString(), tool, JsonSerializer.Serialize(args)), 4096,
                admission: new(false, TriggerKind.UserTurn, AgentInstanceId: Guid.Parse(instance.InstanceId), OwnedSessionId: sessionId));
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result.Text), 1, 4096);
            using var json = JsonDocument.Parse(result.Text);
            Assert.False(json.RootElement.TryGetProperty("error", out _), result.Text);
            return json.RootElement.Clone();
        }
        var createdFile = await Execute(ToolCatalog.OfficeSpreadsheetCreate, new { path = "/home/empty-range.xlsx", sheets = new[]
            { new { name = "Items", rows = Enumerable.Range(1, 10).Select(i => new object[] { new { type = "text", text = $"Item {i}" }, new { type = "number", number = i } }).ToArray() } } });
        var inspection = (await Execute(ToolCatalog.OfficeSpreadsheetInspect, new { path = "/home/empty-range.xlsx", sheet = "Items", range })).GetProperty("inspection");
        Assert.Equal(range, inspection.GetProperty("range").GetString());
        Assert.Equal("A1:B10", inspection.GetProperty("usedRange").GetString());
        Assert.Equal(createdFile.GetProperty("sha256").GetString(), inspection.GetProperty("sha256").GetString());
        Assert.Empty(inspection.GetProperty("rows").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, inspection.GetProperty("returnedRange").ValueKind);
        Assert.Equal(JsonValueKind.Null, inspection.GetProperty("nextRange").ValueKind);
        Assert.True(inspection.GetProperty("truncated").GetBoolean());
        var recovered = (await Execute(ToolCatalog.OfficeSpreadsheetInspect, new { path = "/home/empty-range.xlsx", sheet = "Items", range = "A1:B1" })).GetProperty("inspection");
        Assert.Contains("Item 1", recovered.ToString());
        Assert.Equal(createdFile.GetProperty("sha256").GetString(), recovered.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Office_create_inspect_targeted_edit_and_stale_hash_preserve_owned_real_files()
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var created = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 26 }); created.EnsureSuccessStatusCode();
        var instance = await created.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        var sessionResponse = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = instance!.InstanceId, mode = "text" }); sessionResponse.EnsureSuccessStatusCode();
        using var sessionJson = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync()); var sessionId = sessionJson.RootElement.GetProperty("sessionId").GetGuid();
        var definition = (await factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Definition;
        var tools = factory.Services.GetRequiredService<SessionToolExecutor>();
        async Task<JsonElement> Execute(string name, object args)
        {
            var result = await tools.ExecuteAsync(definition, sessionId, new ModelToolCall(Guid.NewGuid().ToString(), name, JsonSerializer.Serialize(args)), 256 * 1024,
                admission: new(false, TriggerKind.UserTurn, AgentInstanceId: Guid.Parse(instance.InstanceId), OwnedSessionId: sessionId));
            using var json = JsonDocument.Parse(result.Text); return json.RootElement.Clone();
        }
        var word = await Execute(ToolCatalog.OfficeDocumentCreate, new
        {
            path = "/home/report.docx",
            title = "Q3 sales report",
            sections = new[] {
            new { heading = "Summary", paragraphs = new[] { "Revenue increased.", "Specific paragraph to edit." }, table = new[] {new[] {"Region","Revenue"},new[] {"North","1250"}}, pageBreakBefore = false },
            new { heading = "Recommendations", paragraphs = new[] { "Review the North region." }, table = Array.Empty<string[]>(), pageBreakBefore = true } }
        });
        Assert.False(word.TryGetProperty("error", out _), word.ToString());
        var inspection = (await Execute(ToolCatalog.OfficeDocumentInspect, new { path = "/home/report.docx" })).GetProperty("inspection");
        var selector = inspection.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("text").GetString() == "Specific paragraph to edit.").GetProperty("selector").GetString();
        var edited = await Execute(ToolCatalog.OfficeDocumentEdit, new { path = "/home/report.docx", expectedSha256 = word.GetProperty("sha256").GetString(), operations = new[] { new { operation = "replace_paragraph", selector, text = "Reviewed recommendation." } } });
        Assert.False(edited.TryGetProperty("error", out _), edited.ToString()); Assert.NotEqual(word.GetProperty("sha256").GetString(), edited.GetProperty("sha256").GetString());
        var stale = await Execute(ToolCatalog.OfficeDocumentEdit, new { path = "/home/report.docx", expectedSha256 = word.GetProperty("sha256").GetString(), operations = new[] { new { operation = "replace_paragraph", selector, text = "Must not commit." } } });
        Assert.Contains("office_revision_conflict", stale.ToString());
        var after = (await Execute(ToolCatalog.OfficeDocumentInspect, new { path = "/home/report.docx" })).GetProperty("inspection");
        Assert.Contains("Reviewed recommendation.", after.ToString()); Assert.DoesNotContain("Must not commit.", after.ToString());
        var excel = await Execute(ToolCatalog.OfficeSpreadsheetCreate, new
        {
            path = "/home/sales.xlsx",
            sheets = new[] {
            new {name = "Sales", rows = new object[][] { [new {type="text",text="Region"},new {type="text",text="Revenue"}], [new {type="text",text="North"},new {type="number",number=1250,format="currency"}], [new {type="date",text="2026-10-13",format="date"},new {type="blank",formula="SUM(B2:B2)",format="currency"}] }},
            new {name = "Growth", rows = new object[][] { [new {type="number",number=0.125,format="percentage"},new {type="text",text="0001"}] }} }
        });
        Assert.False(excel.TryGetProperty("error", out _), excel.ToString());
        var sheet = (await Execute(ToolCatalog.OfficeSpreadsheetInspect, new { path = "/home/sales.xlsx", sheet = "Sales", range = "A1:B3" })).GetProperty("inspection");
        Assert.Equal(2, sheet.GetProperty("sheets").GetArrayLength()); Assert.Contains("SUM(B2:B2)", sheet.ToString()); Assert.Contains("cached_value", sheet.ToString());
        var changed = await Execute(ToolCatalog.OfficeSpreadsheetEdit, new { path = "/home/sales.xlsx", expectedSha256 = excel.GetProperty("sha256").GetString(), operations = new[] { new { operation = "set_cells", sheet = "Sales", range = "B2", cells = new[] { new[] { new { type = "number", number = 1700, format = "currency" } } } } } });
        Assert.False(changed.TryGetProperty("error", out _), changed.ToString());
        var value = (await Execute(ToolCatalog.OfficeSpreadsheetInspect, new { path = "/home/sales.xlsx", sheet = "Sales", range = "B2" })).GetProperty("inspection").GetProperty("rows")[0][0].GetProperty("value").GetString();
        Assert.Equal("$1,700.00", value);
        var page = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{instance.InstanceId}/workspace"))!;
        foreach (var item in page.Items.Where(item => !item.Directory))
        {
            var download = await client.GetAsync($"/api/v2/agent-instances/{instance.InstanceId}/workspace/{item.ItemId}/content"); download.EnsureSuccessStatusCode();
            var bytes = await download.Content.ReadAsByteArrayAsync(); Assert.Equal(new byte[] { 80, 75, 3, 4 }, bytes[..4]);
            Assert.Equal(item.Sha256Hex, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
            var preview = await client.GetAsync($"/api/v2/agent-instances/{instance.InstanceId}/workspace/{item.ItemId}/office-preview"); preview.EnsureSuccessStatusCode(); Assert.True(preview.Headers.CacheControl!.NoStore);
            var projected = await preview.Content.ReadFromJsonAsync<OfficePreviewResponse>(); Assert.Equal(item.Sha256Hex, projected!.Sha256);
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/v2/agent-instances/{instance.InstanceId}/workspace/{item.ItemId}/office-preview")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/agent-instances/{Guid.NewGuid()}/workspace/{item.ItemId}/office-preview")).StatusCode);

        }
        var scratch = await Execute(ToolCatalog.OfficeSpreadsheetCreate, new { path = "/working/scratch.xlsx", sheets = new[] { new { name = "Scratch", rows = new object[][] { [new { type = "text", text = "exact scratch bytes" }] } } } });
        Assert.False(scratch.TryGetProperty("error", out _), scratch.ToString());
        var publication = await Execute(ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/working/scratch.xlsx", displayName = "scratch.xlsx" }); Assert.Equal(OfficeLimits.SpreadsheetMime, publication.GetProperty("contentType").GetString());
        var artifactId = publication.GetProperty("artifactId").GetString(); var projection = await client.GetFromJsonAsync<OfficePreviewResponse>($"/api/v2/sessions/{sessionId}/artifacts/{artifactId}/office-preview"); Assert.Equal("exact scratch bytes", projection!.Rows![0][0].Value);
        var empty = await Execute(ToolCatalog.OfficeDocumentCreate, new { path = "/working/empty.docx", sections = new[] { new { heading = (string?)null, paragraphs = Array.Empty<string>() } } }); Assert.False(empty.TryGetProperty("error", out _), empty.ToString());
        var emptyArtifact = await Execute(ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/working/empty.docx", displayName = "empty.docx" }); var emptyPreview = await client.GetFromJsonAsync<OfficePreviewResponse>($"/api/v2/sessions/{sessionId}/artifacts/{emptyArtifact.GetProperty("artifactId").GetString()}/office-preview"); Assert.True(emptyPreview!.EmptyBody);
        var next = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = instance.InstanceId, mode = "text" }); next.EnsureSuccessStatusCode();
        using var freshJson = JsonDocument.Parse(await next.Content.ReadAsStringAsync()); var freshId = freshJson.RootElement.GetProperty("sessionId").GetGuid(); Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{freshId}/workspace/content?path=/working/scratch.xlsx")).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{instance.InstanceId}/workspace"))!.Items.Count(item => !item.Directory));
        var oldDefinition = (await factory.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 25))!;
        Assert.Equal(ToolPolicyDecision.Deny, tools.EvaluateExecutionPolicy(oldDefinition, ToolCatalog.OfficeDocumentCreate, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: Guid.Parse(instance.InstanceId), OwnedSessionId: sessionId)));
    }
}
