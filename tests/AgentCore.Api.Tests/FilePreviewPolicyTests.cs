using System.Net;
using System.Net.Http.Json;
using AgentCore.Api;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentCore.Api.Tests;

public sealed class FilePreviewPolicyTests
{
    private sealed class PolicyFactory(IReadOnlyDictionary<string, string?> settings) : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => settings;
    }
    [Fact]
    public async Task Protected_policy_returns_effective_safety_limits_and_custom_window()
    {
        using var factory = new PolicyFactory(new Dictionary<string, string?> {
            ["FilePreview:Limits:MaxBytes"] = "67108864", ["FilePreview:Limits:XlsxBytes"] = "20971520",
            ["FilePreview:Limits:CsvBytes"] = "123456", ["FilePreview:Spreadsheet:RowsPerWindow"] = "12" });
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v2/file-preview/options")).StatusCode);
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var response = await client.GetAsync("/api/v2/file-preview/options"); response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var policy = (await response.Content.ReadFromJsonAsync<FilePreviewOptions>())!;
        Assert.Equal(OfficeLimits.MaxPackageBytes, policy.Limits.XlsxBytes);
        Assert.Equal(123456, policy.Limits.DelimitedBytes); Assert.Equal(12, policy.Spreadsheet.RowsPerWindow);
    }
    [Theory]
    [InlineData("Limits:MaxBytes", "0")]
    [InlineData("Limits:TextBytes", "67108865")]
    [InlineData("Image:MaxPixels", "40000001")]
    [InlineData("Spreadsheet:RowsPerWindow", "201")]
    [InlineData("Spreadsheet:MaxCellsPerWindow", "1")]
    [InlineData("Text:HighlightMaxLines", "10001")]
    [InlineData("WorkerTimeoutMs", "10001")]
    public void Invalid_policy_fails_startup(string key, string value)
    {
        using var factory = new PolicyFactory(new Dictionary<string, string?> { [$"FilePreview:{key}"] = value });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
    [Fact]
    public void Defaults_bind_and_standard_environment_override_is_supported()
    {
        var defaults = new FilePreviewOptions(); Assert.True(defaults.IsValid()); Assert.Equal(12800, defaults.Spreadsheet.MaxCellsPerWindow);
        const string key = "AGENTCORE_PREVIEW_TEST_FilePreview__Limits__CsvBytes";
        Environment.SetEnvironmentVariable(key, "12345");
        try {
            var config = new ConfigurationBuilder().AddEnvironmentVariables("AGENTCORE_PREVIEW_TEST_").Build();
            var policy = config.GetSection("FilePreview").Get<FilePreviewOptions>()!;
            Assert.Equal(12345, policy.Limits.DelimitedBytes); Assert.True(policy.IsValid());
        } finally { Environment.SetEnvironmentVariable(key, null); }
    }
    [Fact]
    public async Task Oversized_home_original_remains_downloadable()
    {
        using var factory = new PolicyFactory(new Dictionary<string, string?> { ["FilePreview:Limits:XlsxBytes"] = "100", ["FilePreview:Spreadsheet:RowsPerWindow"] = "2" });
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var created = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 26 }); created.EnsureSuccessStatusCode();
        var owner = (await created.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!.InstanceId;
        var session = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = owner, mode = "text" }); session.EnsureSuccessStatusCode();
        var id = (await session.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["sessionId"]!.GetValue<string>();
        var bytes = factory.Services.GetRequiredService<IOfficeProcessor>().CreateWorkbook(new([new("Report", [[new("text", Text: "0001")]])]), default).Bytes;
        using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(OfficeLimits.SpreadsheetMime);
        var saved = await client.PutAsync($"/api/v2/sessions/{id}/workspace/content?path=%2Fhome%2Freport.xlsx", content); saved.EnsureSuccessStatusCode();
        var item = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{owner}/workspace"))!.Items.Single(i => i.LogicalPath.EndsWith("report.xlsx"));
        var preview = await client.GetAsync($"/api/v2/agent-instances/{owner}/workspace/{item.ItemId}/office-preview"); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, preview.StatusCode);
        Assert.Contains("preview_too_large", await preview.Content.ReadAsStringAsync());
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/agent-instances/{owner}/workspace/{item.ItemId}/content"));
    }
    [Fact]
    public void Global_ceiling_clamps_all_formats_and_immutable_office_ceiling_cannot_be_raised()
    {
        var policy = new FilePreviewOptions(); policy.Limits.MaxBytes = 100;
        Assert.True(policy.IsValid());
        var effective = policy.Effective();
        Assert.All(new[] { effective.Limits.TextBytes, effective.Limits.DelimitedBytes, effective.Limits.DocxBytes, effective.Limits.XlsxBytes, effective.Limits.ImageBytes, effective.Limits.AudioBytes, effective.Limits.VideoBytes }, value => Assert.Equal(100, value));
        Assert.Equal(100, effective.Text.HighlightMaxBytes);
        policy.Limits.MaxBytes = 64 * 1024 * 1024; policy.Limits.XlsxBytes = policy.Limits.MaxBytes;
        Assert.Equal(OfficeLimits.MaxPackageBytes, policy.Effective().Limits.XlsxBytes);
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public async Task Office_threshold_and_range_recovery_preserve_original_bytes(int offset)
    {
        var processor = new AgentCore.Infrastructure.Office.OfficeProcessor();
        var bytes = processor.CreateWorkbook(new([new("Window", [[new("text", Text: "00123")], [new("number", Number: 2)], [new("number", Number: 3)]])]), default).Bytes;
        using var factory = new PolicyFactory(new Dictionary<string, string?> { ["FilePreview:Limits:XlsxBytes"] = (bytes.Length + offset).ToString(), ["FilePreview:Spreadsheet:RowsPerWindow"] = "2" });
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var created = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 26 }); created.EnsureSuccessStatusCode();
        var owner = (await created.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!.InstanceId;
        var session = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = owner, mode = "text" }); session.EnsureSuccessStatusCode();
        var id = (await session.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["sessionId"]!.GetValue<string>();
        using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(OfficeLimits.SpreadsheetMime);
        (await client.PutAsync($"/api/v2/sessions/{id}/workspace/content?path=%2Fhome%2Fwindow.xlsx", content)).EnsureSuccessStatusCode();
        var item = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{owner}/workspace"))!.Items.Single(i => i.LogicalPath.EndsWith("window.xlsx"));
        var path = $"/api/v2/agent-instances/{owner}/workspace/{item.ItemId}";
        var response = await client.GetAsync(path + "/office-preview");
        if (offset < 0) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        else {
            response.EnsureSuccessStatusCode(); var preview = (await response.Content.ReadFromJsonAsync<OfficePreviewResponse>())!;
            Assert.Equal("A1:BL2", preview.Range); Assert.Equal(2, preview.Rows!.Count); Assert.Equal("00123", preview.Rows[0][0].Value);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.GetAsync(path + "/office-preview?range=A1:B3")).StatusCode);
            var recovered = await client.GetFromJsonAsync<OfficePreviewResponse>(path + "/office-preview?range=A3:A3"); Assert.Equal("3", recovered!.Rows![0][0].Value);
        }
        Assert.Equal(bytes, await client.GetByteArrayAsync(path + "/content"));
    }

    [Fact]
    public async Task Immutable_excerpt_budget_returns_structural_limit_and_smaller_range_recovers()
    {
        using var factory = new PolicyFactory(new Dictionary<string, string?>());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var created = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 26 });
        created.EnsureSuccessStatusCode();
        var owner = (await created.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!.InstanceId;
        var session = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = owner, mode = "text" });
        session.EnsureSuccessStatusCode();
        var id = (await session.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["sessionId"]!.GetValue<string>();
        var processor = factory.Services.GetRequiredService<IOfficeProcessor>();
        var rows = Enumerable.Range(0, 17).Select(_ => (IReadOnlyList<OfficeCell>)[new("text", Text: new string('x', 4000))]).ToArray();
        var bytes = processor.CreateWorkbook(new([new("Long", rows)]), default).Bytes;
        // Tool inspection retains its existing independent validation semantics.
        var toolError = Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => processor.InspectWorkbook(bytes, "Long", "A1:A17", default));
        Assert.Equal(400, toolError.StatusCode);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new(OfficeLimits.SpreadsheetMime);
        (await client.PutAsync($"/api/v2/sessions/{id}/workspace/content?path=%2Fhome%2Flong.xlsx", content)).EnsureSuccessStatusCode();
        var item = (await client.GetFromJsonAsync<AgentWorkspacePageResponse>($"/api/v2/agent-instances/{owner}/workspace"))!.Items.Single(i => i.LogicalPath.EndsWith("long.xlsx"));
        var path = $"/api/v2/agent-instances/{owner}/workspace/{item.ItemId}";
        var response = await client.GetAsync(path + "/office-preview?range=A1:A17");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("preview_structure_limit", await response.Content.ReadAsStringAsync());
        var recovered = await client.GetFromJsonAsync<OfficePreviewResponse>(path + "/office-preview?range=A1:A1");
        Assert.Equal(new string('x', 4000), recovered!.Rows![0][0].Value);
        Assert.Equal(bytes, await client.GetByteArrayAsync(path + "/content"));
    }

    [Fact]
    public async Task Instance_and_draft_resources_apply_preview_policy_while_downloads_keep_originals()
    {
        var processor = new AgentCore.Infrastructure.Office.OfficeProcessor();
        var bytes = processor.CreateWorkbook(new([new("Source", [[new("text", Text: "original")]])]), default).Bytes;
        using var factory = new PolicyFactory(new Dictionary<string, string?> { ["FilePreview:Limits:XlsxBytes"] = "100" });
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(factory.Services));
        var created = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 26 }); created.EnsureSuccessStatusCode();
        var instance = (await created.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>())!;
        var resources = factory.Services.GetRequiredService<AgentCore.Application.Admin.AgentInstanceResourceService>();
        var catalog = await resources.ListAsync(Guid.Parse(instance.InstanceId));
        var saved = await resources.UpsertAsync(Guid.Parse(instance.InstanceId), catalog.InstanceRevision, null, null, "reference/source.xlsx",
            AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, OfficeLimits.SpreadsheetMime, bytes);
        var item = saved.Resources.Single(r => r.Origin == "Instance");
        var instancePath = $"/api/v2/admin/agent-instances/{instance.InstanceId}/resources/{Uri.EscapeDataString(item.Key)}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.GetAsync(instancePath + "/office-preview")).StatusCode);
        Assert.Equal(bytes, await client.GetByteArrayAsync(instancePath + "/content"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(instancePath + "/office-preview")).StatusCode);

        var fork = await client.PostAsJsonAsync("/api/v2/admin/definition-drafts/fork", new AdminForkDefinitionDraftRequest("general-assistant", 26, "ForkBuiltIn")); fork.EnsureSuccessStatusCode();
        var draft = (await fork.Content.ReadFromJsonAsync<AdminDefinitionDraftResponse>())!;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources/content") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new(OfficeLimits.SpreadsheetMime);
        var uploaded = await client.SendAsync(request); uploaded.EnsureSuccessStatusCode();
        var stored = (await uploaded.Content.ReadFromJsonAsync<AdminDefinitionResourceContentStoredResponse>())!;
        var bound = await client.PutAsJsonAsync($"/api/v2/admin/definition-drafts/{draft.DraftId}/resources", new AdminUpsertDefinitionDraftResourceRequest(draft.Revision, null, "reference/source.xlsx", "Reference", stored.MediaType, stored.ContentSha256, stored.ByteLength)); bound.EnsureSuccessStatusCode();
        var resource = (await bound.Content.ReadFromJsonAsync<AdminDefinitionDraftResourceResponse>())!;
        var draftPath = $"/api/v2/admin/definition-drafts/{draft.DraftId}/resources/{resource.ResourceId}";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.GetAsync(draftPath + "/office-preview")).StatusCode);
        Assert.Equal(bytes, await client.GetByteArrayAsync(draftPath + "/content"));
    }

}
