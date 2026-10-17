using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    private static HttpClient ResourceClient(AgentCoreApiFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    private static async Task<(Guid Tenant, string Agent, string File)> CreatePreviewResource(HttpClient owner, byte[] bytes, string path, string mediaType)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var created = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        var agent = $"/api/v2/tenants/{tenant}/agents/{created.GetProperty("instance").GetProperty("instanceId").GetGuid()}";
        var saved = await Json(await owner.PostAsJsonAsync(agent + "/resources", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
            input = new { logicalPath = path, kind = 2, mediaType, content = bytes } }));
        return (tenant, agent, agent + "/resources/" + Uri.EscapeDataString(saved.GetProperty("resource").GetProperty("key").GetString()!));
    }
    private static byte[] PreviewBytes(IOfficeProcessor office, string format) => format == "docx"
        ? office.CreateDocument(new("Regional review", [new(null, ["Protected document evidence."], null)]), default).Bytes
        : office.CreateWorkbook(new([new("Regional review", [[new("text", Text: "00123")], [new("number", Number: 2)], [new("number", Number: 3)]])]), default).Bytes;

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    public async Task Tenant_resource_office_previews_use_authorized_bytes_and_shared_bounded_projection(string format)
    {
        await using var factory = new AgentReadFenceFactory(new Dictionary<string, string?> { ["FilePreview:Spreadsheet:RowsPerWindow"] = "2" });
        using var owner = ResourceClient(factory);
        var bytes = PreviewBytes(factory.Services.GetRequiredService<IOfficeProcessor>(), format);
        var file = await CreatePreviewResource(owner, bytes, "regional/source." + format, format == "docx" ? OfficeLimits.DocumentMime : OfficeLimits.SpreadsheetMime);
        var before = await Revision(owner, file.Tenant);
        var response = await owner.GetAsync(file.File + "/office-preview"); var preview = await Json(response);
        Assert.True(response.Headers.CacheControl!.NoStore); Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(format, preview.GetProperty("format").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), preview.GetProperty("sha256").GetString());
        Assert.False(preview.TryGetProperty("sourceBase64", out var source) && source.ValueKind == JsonValueKind.String);
        if (format == "xlsx")
        {
            Assert.Equal("A1:BL2", preview.GetProperty("range").GetString());
            Assert.Equal("00123", preview.GetProperty("rows")[0][0].GetProperty("value").GetString());
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await owner.GetAsync(file.File + "/office-preview?range=A1:A3")).StatusCode);
            var smaller = await Json(await owner.GetAsync(file.File + "/office-preview?sheet=Regional%20review&range=A3:A3"));
            Assert.Equal("3", smaller.GetProperty("rows")[0][0].GetProperty("value").GetString());
        }
        else Assert.False(preview.GetProperty("emptyBody").GetBoolean());
        Assert.Equal(bytes, await owner.GetByteArrayAsync(file.File + "/content"));
        Assert.Equal(before, await Revision(owner, file.Tenant));
        var options = await Json(await owner.GetAsync($"/api/v2/tenants/{file.Tenant}/file-preview/options"));
        Assert.Equal(2, options.GetProperty("spreadsheet").GetProperty("rowsPerWindow").GetInt32());
        using var anonymous = ResourceClient(factory);
        TestOwnerCapability.Apply(anonymous, factory.Services); // A legacy owner header cannot authenticate canonical routes.
        foreach (var suffix in new[] { "/editor", "/content", "/office-preview" }) Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(file.File + suffix)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v2/tenants/{file.Tenant}/file-preview/options")).StatusCode);
        var foreign = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Foreign preview organization", slug = "foreign-preview" }))).GetProperty("tenantId").GetGuid();
        foreach (var suffix in new[] { "/editor", "/content", "/office-preview" }) Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(file.File.Replace(file.Tenant.ToString(), foreign.ToString()) + suffix)).StatusCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Tenant_resource_preview_size_is_checked_before_blob_IO_and_download_retains_original(int offset)
    {
        var bytes = PreviewBytes(new AgentCore.Infrastructure.Office.OfficeProcessor(), "xlsx");
        await using var factory = new AgentReadFenceFactory(new Dictionary<string, string?> { ["FilePreview:Limits:XlsxBytes"] = (bytes.Length + offset).ToString() });
        using var owner = ResourceClient(factory); var file = await CreatePreviewResource(owner, bytes, "source.xlsx", OfficeLimits.SpreadsheetMime);
        factory.ContentReads = 0;
        var response = await owner.GetAsync(file.File + "/office-preview");
        Assert.Equal(offset < 0 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(offset < 0 ? 0 : 1, factory.ContentReads);
        Assert.Equal(bytes, await owner.GetByteArrayAsync(file.File + "/content"));
    }

    public static IEnumerable<object[]> ResourceProjectionRaces =>
        from format in new[] { "docx", "xlsx" }
        from capture in new[] { "bytes", "office" }
        from change in new[] { "logout", "relogin", "security", "instance" }
        select new object[] { format, capture, change };

    [Theory]
    [MemberData(nameof(ResourceProjectionRaces))]
    public async Task Tenant_resource_preview_fences_original_session_and_revisions_after_bytes_and_actual_Office_processing(string format, string capture, string change)
    {
        await using var factory = new AgentReadFenceFactory(); using var owner = ResourceClient(factory);
        var bytes = PreviewBytes(factory.Services.GetRequiredService<IOfficeProcessor>(), format);
        var file = await CreatePreviewResource(owner, bytes, "protected." + format, format == "docx" ? OfficeLimits.DocumentMime : OfficeLimits.SpreadsheetMime);
        factory.CaptureAt = capture; factory.AfterCapture = async () =>
        {
            if (change == "security") Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/v2/tenants/{file.Tenant}/roles", new { name = "Concurrent preview role", permissions = new[] { "agents.read" }, expectedRevision = await Revision(owner, file.Tenant) })).StatusCode);
            else if (change == "instance")
            {
                var current = await Json(await owner.GetAsync(file.Agent));
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(file.Agent + "/lifecycle", new { lifecycle = 1, expectedRevision = await Revision(owner, file.Tenant), expectedInstanceRevision = current.GetProperty("instance").GetProperty("revision").GetInt64() })).StatusCode);
            }
            else
            {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                if (change == "relogin") await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            }
        };
        var response = await owner.GetAsync(file.File + "/office-preview"); var error = await Json(response);
        Assert.Equal(change is "security" or "instance" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, response.StatusCode);
        var reason = change is "logout" or "relogin" ? "initiator_unavailable" : change == "instance" && capture == "bytes" ? "resource_changed" : "stale_revision";
        Assert.Equal(reason, error.GetProperty("reasonCode").GetString());
        Assert.False(error.TryGetProperty("sha256", out _)); Assert.False(error.TryGetProperty("rows", out _)); Assert.Null(factory.AfterCapture);
        if (change != "logout") Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(file.File + "/office-preview")).StatusCode);
    }

    [Theory]
    [InlineData("source.txt", "text/plain", "preview_unsupported")]
    [InlineData("source.xlsx", OfficeLimits.SpreadsheetMime, "invalid_office_package")]
    public async Task Tenant_resource_preview_rejects_unsupported_or_malformed_source_with_safe_error(string name, string mime, string title)
    {
        await using var factory = new AgentReadFenceFactory(); using var owner = ResourceClient(factory);
        var bytes = "Private malformed package sentinel."u8.ToArray(); var file = await CreatePreviewResource(owner, bytes, name, mime);
        var response = await owner.GetAsync(file.File + "/office-preview");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(); Assert.Contains(title, body); Assert.DoesNotContain("sentinel", body);
        Assert.Equal(bytes, await owner.GetByteArrayAsync(file.File + "/content"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_resource_exact_reader_can_preview_local_or_inherited_bytes_without_IAM_inspection_and_revocation_retires_projection(bool inherited)
    {
        await using var factory = new AgentReadFenceFactory(); using var owner = ResourceClient(factory);
        var bytes = PreviewBytes(factory.Services.GetRequiredService<IOfficeProcessor>(), "xlsx");
        var file = await CreatePreviewResource(owner, bytes, "regional/visible.xlsx", OfficeLimits.SpreadsheetMime);
        var root = $"/api/v2/tenants/{file.Tenant}";
        if (inherited)
        {
            var draft = await Json(await owner.PostAsJsonAsync(root + "/agent-definitions", new { name = "Inherited preview", expectedRevision = await Revision(owner, file.Tenant) }));
            var draftPath = root + $"/definition-drafts/{draft.GetProperty("draftId").GetGuid()}";
            await Json(await owner.PostAsJsonAsync(draftPath + "/resources/batch", new { expectedRevision = await Revision(owner, file.Tenant), expectedDraftRevision = 1,
                items = new[] { new { logicalPath = "regional/visible.xlsx", kind = 2, mediaType = OfficeLimits.SpreadsheetMime, content = bytes } } }));
            await Json(await owner.PostAsJsonAsync(draftPath + "/publish", new { expectedRevision = await Revision(owner, file.Tenant), expectedDraftRevision = 2 }));
            var agent = await Json(await owner.PostAsJsonAsync(root + "/agents/from-publication", new { definitionId = draft.GetProperty("definitionId").GetString(), version = 1, expectedRevision = await Revision(owner, file.Tenant) }));
            var path = root + $"/agents/{agent.GetProperty("instance").GetProperty("instanceId").GetGuid()}";
            var source = (await Json(await owner.GetAsync(path + "/resources/editor"))).GetProperty("items")[0].GetProperty("resource");
            file = (file.Tenant, path, path + "/resources/" + Uri.EscapeDataString(source.GetProperty("key").GetString()!));
        }
        var detail = await Json(await owner.GetAsync(file.File + "/editor"));
        var key = detail.GetProperty("item").GetProperty("resource").GetProperty("key").GetString()!;
        var agentId = detail.GetProperty("agent").GetProperty("instance").GetProperty("instanceId").GetGuid();
        var hidden = (await Json(await owner.PostAsJsonAsync(file.Agent + "/resources", new { expectedRevision = await Revision(owner, file.Tenant), expectedInstanceRevision = detail.GetProperty("agent").GetProperty("instance").GetProperty("revision").GetInt64(),
            input = new { logicalPath = "private-sentinel.xlsx", kind = 2, mediaType = OfficeLimits.SpreadsheetMime, content = bytes } }))).GetProperty("resource").GetProperty("key").GetString()!;
        async Task<Guid> Role(string name, params string[] permissions) => (await Json(await owner.PostAsJsonAsync(root + "/roles", new { name, permissions, expectedRevision = await Revision(owner, file.Tenant) }))).GetProperty("roleId").GetGuid();
        var membership = await Role("Preview minimal membership", "sessions.read");
        var parentRole = await Role("Preview exact parent", "agents.read"); var childRole = await Role("Preview exact child", "resources.read");
        var invitation = await Json(await owner.PostAsJsonAsync(root + "/members/invite", new { login = "preview-reader@example.test", roleId = membership, expectedRevision = await Revision(owner, file.Tenant) }));
        using var reader = ResourceClient(factory); await Csrf(reader);
        Assert.Equal(HttpStatusCode.Created, (await reader.PostAsJsonAsync("/api/v2/auth/register", new { login = "preview-reader@example.test", displayName = "Preview reader", password = "strong-preview-reader-password",
            invitation = new { tenantId = file.Tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(reader, "preview-reader@example.test", "strong-preview-reader-password");
        var principal = (await Json(await owner.GetAsync(root + "/members"))).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == "Preview reader").GetProperty("principalId").GetGuid();
        async Task<JsonElement> Grant(Guid roleId, string resourceType, string resourceId) => await Json(await owner.PostAsJsonAsync(root + "/role-assignments", new { principalId = principal, roleId, resourceType, resourceId, expectedRevision = await Revision(owner, file.Tenant) }));
        await Grant(parentRole, "agent", agentId.ToString("D"));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(file.File + "/office-preview")).StatusCode);
        var grant = await Grant(childRole, "resource", agentId.ToString("D") + ":" + key);
        var readable = await Json(await reader.GetAsync(file.File + "/editor"));
        Assert.False(readable.GetProperty("item").GetProperty("canManage").GetBoolean()); Assert.False(readable.GetProperty("item").GetProperty("canDelete").GetBoolean()); Assert.False(readable.GetProperty("item").GetProperty("canCopy").GetBoolean());
        var preview = await Json(await reader.GetAsync(file.File + "/office-preview")); Assert.Equal("00123", preview.GetProperty("rows")[0][0].GetProperty("value").GetString());
        Assert.Equal(bytes, await reader.GetByteArrayAsync(file.File + "/content"));
        foreach (var suffix in new[] { "/editor", "/content", "/office-preview" }) Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(file.Agent + "/resources/" + Uri.EscapeDataString(hidden) + suffix)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(root + "/roles")).StatusCode);
        factory.CaptureAt = "office"; factory.AfterCapture = async () => Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(root + $"/role-assignments/{grant.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, file.Tenant)}")).StatusCode);
        var retired = await reader.GetAsync(file.File + "/office-preview"); Assert.Equal(HttpStatusCode.Conflict, retired.StatusCode); Assert.Null(factory.AfterCapture);
        Assert.DoesNotContain("00123", await retired.Content.ReadAsStringAsync());
        foreach (var suffix in new[] { "/editor", "/content", "/office-preview" }) Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(file.File + suffix)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(file.Agent)).StatusCode);
    }
}
