using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    // Ported from WebPublicToolTests: native input/result parity uses an actual
    // admitted owner and claim, rather than an unowned SessionToolExecutor.
    [Fact]
    public async Task Web_search_enforces_query_and_limit_bounds_in_owned_execution()
    {
        await using var f = await Fixture.Create(false, builtInAuthority: true);
        var v = await WorkspaceEffectCase(f); var native = new PublicReadPort(); var service = PublicReads(f, native);
        foreach (var arguments in new[] { "{}", JsonSerializer.Serialize(new { query = new string('q', WebToolLimits.MaxSearchQueryLength + 1) }), """{"query":"agent core","limit":11}""" })
            await Assert.ThrowsAsync<ArgumentException>(() => service.InspectAsync(v.Frame, new("invalid", ToolCatalog.WebSearch, arguments), ToolLimits.MaxOutputBytes).AsTask());
        Assert.Equal(0, native.Calls);
        var call = new ModelToolCall("search", ToolCatalog.WebSearch, """{"query":"agent core","limit":10}""");
        await WorkspaceEffectCheckpoint(f, v.Frame, call);
        using var result = JsonDocument.Parse(await service.InspectAsync(v.Frame, call, ToolLimits.MaxOutputBytes));
        Assert.True(result.RootElement.GetProperty("tenantPublicWebRead").GetBoolean());
        Assert.True(result.RootElement.GetProperty("untrusted").GetBoolean());
        Assert.Equal("agent core", native.LastSearch!.Query); Assert.Equal(10, native.LastSearch.Limit);
        Assert.Equal(1, native.Calls);
    }

    [Fact]
    public async Task Web_fetch_returns_untrusted_framing_and_respects_owned_policy_denial()
    {
        await using var f = await Fixture.Create(false, builtInAuthority: true);
        var v = await WorkspaceEffectCase(f); var native = new PublicReadPort(); var service = PublicReads(f, native);
        var call = new ModelToolCall("fetch", ToolCatalog.WebFetch, """{"url":"https://example.test/page"}""");
        await WorkspaceEffectCheckpoint(f, v.Frame, call);
        var text = await service.InspectAsync(v.Frame, call, ToolLimits.MaxOutputBytes);
        using var result = JsonDocument.Parse(text);
        Assert.True(result.RootElement.GetProperty("tenantPublicWebRead").GetBoolean());
        Assert.True(result.RootElement.GetProperty("untrusted").GetBoolean());
        Assert.Contains("PUBLIC-NATIVE-WITNESS", text); Assert.Equal(new Uri("https://example.test/page"), native.LastFetch!.Url);
        await f.Access.RevokeAssignmentAsync(v.Setup.Owner.PrincipalId, v.Setup.Tenant, v.Grant.AssignmentId,
            (await f.Iam.ReadTenantAsync(v.Setup.Tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.InspectAsync(v.Frame, call, ToolLimits.MaxOutputBytes).AsTask());
        Assert.Equal(1, native.Calls);
    }
}
