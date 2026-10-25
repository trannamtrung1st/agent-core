using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantBackgroundReportContractTests
{
    private static PreparedBackgroundReport Report()
    {
        var tenant = Guid.NewGuid();
        return new(tenant, Guid.NewGuid(), Guid.NewGuid(), 3, Guid.NewGuid(), 2, 0, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            JsonSerializer.Serialize(new AgentRunAdmissionFactory.SignalInput(TriggerKind.BackgroundCompleted, """{"summary":"Exact completion","status":"Completed"}""", null, 0)),
            new string('a', 64), new("synthetic", "primary-llm", "scripted-alpha", null), new(tenant, Guid.NewGuid(), null, 0, 1, DateTimeOffset.UnixEpoch));
    }
    [Fact]
    public void Report_semantic_dedupe_retains_source_and_destination_while_exact_grants_bind_all_witnesses()
    {
        var p = Report(); var instance = Guid.NewGuid(); var first = TenantApprovalRules.PrepareBackgroundReport(Guid.NewGuid(), instance, p, 1, 1);
        var later = TenantApprovalRules.PrepareBackgroundReport(Guid.NewGuid(), instance, p with { RunId = Guid.NewGuid(), ActivationId = Guid.NewGuid(), ResponseId = Guid.NewGuid(), ParentRevision = 3 }, 2, 1);
        Assert.Equal(first.OperationFingerprint, later.OperationFingerprint); Assert.NotEqual(first.ActionHash, later.ActionHash);
        var elsewhere = TenantApprovalRules.PrepareBackgroundReport(Guid.NewGuid(), instance, p with {
            DestinationContext = p.DestinationContext with { SpaceId = Guid.NewGuid() } }, 1, 1);
        Assert.NotEqual(first.OperationFingerprint, elsewhere.OperationFingerprint);
        Assert.Equal("agent-runs.create", first.Permission); Assert.Equal(AccessScopeKind.Tenant, TenantApprovalRules.Scope(p.TenantId, first).Kind);
        Assert.Equal(first, TenantApprovalRules.Rebuild(first));
    }
    [Theory] [InlineData("source")] [InlineData("destination")] [InlineData("revision")] [InlineData("boundary")] [InlineData("evidence")] [InlineData("hash")]
    public void Report_preparation_refuses_missing_or_ambiguous_source_destination_and_evidence(string boundary)
    {
        var p = Report();
        p = boundary switch { "source" => p with { ChildRunId = Guid.Empty }, "destination" => p with { ParentSessionId = p.SessionId },
            "revision" => p with { SourceRevision = 0 }, "boundary" => p with { InputBoundarySequence = 4 }, "evidence" => p with { EvidenceJson = "{}" }, _ => p with { ConfigurationHash = "changed" } };
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareBackgroundReport(Guid.NewGuid(), Guid.NewGuid(), p, 1, 1));
    }
    [Theory] [InlineData("deliver_background_completion")] [InlineData("background.report")]
    public void Model_cannot_initiate_arbitrary_completion_report_or_supply_trusted_report_authority(string operation)
    {
        var json = JsonSerializer.Serialize(new { operation, report = new { tenantId = Guid.NewGuid(), childRunId = Guid.NewGuid(), summary = "Forged completion" } });
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("forged-report", ToolCatalog.RequestAuthorization, json)));
    }
}
