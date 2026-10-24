using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_capture_cancellation_before_settlement_retains_uncertainty_without_uncommitted_files_or_replay(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Prepare(f);
        await GrantBrowserPublication(f, s); using var stopping = new CancellationTokenSource();
        var probe = new EffectProbe(Probe(s)) { Result = new(null, Bytes: CapturePng, ContentType: "image/png"),
            AfterEffect = () => { stopping.Cancel(); return Task.CompletedTask; } };
        (s, var request) = await ApprovedEffect(f, s, probe, tool: ToolCatalog.BrowserScreenshot, args: "{}");
        var service = Effects(f, probe);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteEffect(service, s, request, stopping.Token).AsTask());
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
        var replay = await ExecuteEffect(service, s, request); Assert.Equal(receipt, replay.Receipt); Assert.Null(replay.Result); Assert.Equal(1, probe.Invocations);
        Assert.DoesNotContain(Convert.ToBase64String(CapturePng), System.Text.Json.JsonSerializer.Serialize(receipt));
    }

    private sealed class LimitedArtifactProbe(TargetProbe target, BrowserOperationalLimits limits) : IBrowser, ITenantBrowserCommandTarget
    {
        public EffectProbe Inner { get; } = new(target);
        public BrowserProviderDescriptor Provider => Inner.Provider;
        public BrowserHostPolicy HostPolicy => Inner.HostPolicy with { Limits = limits };
        public bool IsAvailable => true;
        public ValueTask<TenantBrowserCommandPreparation> PrepareTenantCommandAsync(TenantBrowserOwner owner, BrowserCommand command, CancellationToken ct = default) => Inner.PrepareTenantCommandAsync(owner, command, ct);
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => Inner.ExecuteAsync(request, ct);
    }

    [Theory] [InlineData(false, 1)] [InlineData(true, 1)] [InlineData(false, 4)] [InlineData(true, 4)]
    public async Task Tenant_Browser_capture_effective_allowance_survives_exact_consent_hops_and_rejects_before_another_native_call(bool sqlite, int allowance)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Prepare(f);
        await GrantBrowserPublication(f, s);
        var probe = new LimitedArtifactProbe(Probe(s), new(CapturesPerScope: allowance));
        probe.Inner.Result = new(null, Bytes: CapturePng, ContentType: "image/png");
        for (var index = 0; index < allowance; index++)
        {
            (s, var request) = await ApprovedEffect(f, s, probe, tool: ToolCatalog.BrowserScreenshot,
                args: "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"View " + index + "\"}}");
            var captured = await ExecuteEffect(Effects(f, probe), s, request);
            Assert.Equal(TenantOperationOutcome.Completed, captured.Receipt.Outcome);
            Assert.Equal("published", TenantBrowserArtifactRules.Read(captured.Receipt.BrowserArtifactsJson!).Status);
            Assert.Null(captured.Result!.Bytes);
            s = s with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))! };
        }
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, probe), s, ToolCatalog.BrowserScreenshot, "{}").AsTask());
        Assert.Equal("capture_limit", denied.ReasonCode); Assert.Equal(allowance, probe.Inner.Invocations);
        Assert.Equal(allowance, (await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items.Count);
        Assert.Equal(allowance, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new())).Items.Count);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_oversized_download_never_publishes_or_spends_the_later_valid_file_allowance(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true); var s = await Prepare(f);
        await GrantBrowserPublication(f, s); var probe = new LimitedArtifactProbe(Probe(s), new(DownloadBytes: 1024, DownloadsPerScope: 1));
        probe.Inner.Result = new(null, Downloads: [new(null, "oversized.txt", "text/plain", new byte[2048])], EffectAttempted: true, EffectConfirmedBySdk: true);
        (s, var oversized) = await ApprovedEffect(f, s, probe, downloads: true);
        var rejected = await ExecuteEffect(Effects(f, probe), s, oversized);
        Assert.Equal(TenantOperationOutcome.Completed, rejected.Receipt.Outcome);
        Assert.Equal("unavailable", TenantBrowserArtifactRules.Read(rejected.Receipt.BrowserArtifactsJson!).Status);
        Assert.Empty((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
        s = s with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))! };
        var bytes = "id,value\n1,approved\n"u8.ToArray();
        probe.Inner.Result = new(null, Downloads: [new(null, "valid.csv", "text/csv", bytes)], EffectAttempted: true, EffectConfirmedBySdk: true);
        (s, var valid) = await ApprovedEffect(f, s, probe, args: """{"url":"http://127.0.0.1:5091/next"}""", downloads: true);
        var accepted = await ExecuteEffect(Effects(f, probe), s, valid);
        var file = Assert.Single(TenantBrowserArtifactRules.Read(accepted.Receipt.BrowserArtifactsJson!).Files);
        Assert.Equal(bytes, await ((ITenantArtifactStore)f.Artifacts).ReadTenantContentAsync(s.Tenant, s.Run.SessionId, file.ArtifactId));
        Assert.Equal(2, probe.Inner.Invocations); Assert.Single((await ((ITenantArtifactStore)f.Artifacts).ListTenantAsync(s.Tenant, s.Run.SessionId, null, null, 100)).Items);
        s = s with { Run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))! };
        var exhausted = await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, probe), s, args: """{"url":"http://127.0.0.1:5091/third"}""", downloads: true).AsTask());
        Assert.Equal("download_limit", exhausted.ReasonCode); Assert.Equal(2, probe.Inner.Invocations);
    }
}
