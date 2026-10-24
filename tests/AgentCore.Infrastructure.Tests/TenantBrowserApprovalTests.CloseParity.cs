using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Theory]
    [InlineData(false, "closed")] [InlineData(true, "closed")]
    [InlineData(false, "already_closed")] [InlineData(true, "already_closed")]
    public async Task Tenant_Browser_close_routes_exact_Session_command_and_preserves_native_status_without_replay(bool sqlite, string status)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f);
        var probe = new EffectProbe(Probe(s)) { Result = new(null, Status: status, EffectAttempted: true, EffectConfirmedBySdk: true) };
        (s, var request) = await ApprovedEffect(f, s, probe, tool: ToolCatalog.BrowserClose, args: "{}");
        var service = Effects(f, probe); var result = await ExecuteEffect(service, s, request);
        Assert.Equal(TenantOperationOutcome.Completed, result.Receipt.Outcome);
        Assert.Equal(1, probe.NativeEffects); Assert.Equal(s.Run.SessionId, probe.LastRequest!.SessionId);
        Assert.IsType<BrowserClose>(probe.LastRequest.Command);
        Assert.Equal(status, result.Result!.Status);
        Assert.Contains("\"status\":\"" + status + "\"", TenantBrowserContinuationProjection.Observation(result.Result));
        Assert.Equal(result.Receipt, (await ExecuteEffect(service, s, request)).Receipt); Assert.Equal(1, probe.NativeEffects);
    }
}
