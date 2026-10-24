using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Theory]
    [InlineData(false, "dialog_pending", true)] [InlineData(true, "dialog_pending", true)]
    [InlineData(false, "dialog_pending", false)] [InlineData(true, "dialog_pending", false)]
    [InlineData(false, "timeout", true)] [InlineData(true, "timeout", true)]
    [InlineData(false, null, true)] [InlineData(true, null, true)]
    public async Task Tenant_Browser_observed_pending_modal_settles_one_nonreplayable_dispatch_without_claiming_SDK_or_application_success(bool sqlite, string? error, bool attempted)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f);
        var probe = new EffectProbe(Probe(s)) { Result = new(error, EffectAttempted: attempted) };
        (s, var request) = await ApprovedEffect(f, s, probe);
        var service = Effects(f, probe); var result = await ExecuteEffect(service, s, request);
        var modal = error == "dialog_pending" && attempted;
        Assert.Equal(modal ? TenantOperationOutcome.Completed : attempted ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Denied, result.Receipt.Outcome);
        Assert.Equal(1, probe.Invocations); Assert.Equal(1, probe.NativeEffects);
        Assert.Equal(request.RequestId, result.Receipt.RequestId); Assert.Equal(request.Operation.ActionHash, result.Receipt.ActionHash);
        if (modal)
        {
            Assert.NotNull(result.Result); Assert.Equal("dialog_pending", result.Result.ErrorCode);
            Assert.False(result.Result.EffectConfirmedBySdk); Assert.False(result.Result.ApplicationOutcomeVerified);
            using var json = JsonDocument.Parse(TenantBrowserContinuationProjection.Observation(result.Result));
            Assert.Equal("dialog_pending", json.RootElement.GetProperty("error").GetString());
            Assert.False(json.RootElement.GetProperty("effectConfirmedBySdk").GetBoolean());
            Assert.False(json.RootElement.GetProperty("applicationOutcomeVerified").GetBoolean());
        }
        else Assert.Null(result.Result);
        Assert.Equal(result.Receipt, (await ExecuteEffect(service, s, request)).Receipt); Assert.Equal(1, probe.Invocations);
    }
}
