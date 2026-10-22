using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantOperationReceiptRecord(Guid OperationId, Guid? RequestId, Guid TenantId, string RunId, string SessionId,
    string RootActivationId, Guid ExecutorPrincipalId, string InstanceId, string ActionHash, int Outcome, string ReasonCode,
    long InstanceRevision, long PersonaRevision, long RecordedAt, string? SourceCallId = null, string? ParametersHash = null, string? BrowserArtifactsJson = null, string? AutomationMutationJson = null, string? ManagementSkillJson = null, string? ManagementResourceJson = null)
{
    internal TenantOperationReceipt Domain()
    {
        if (AutomationMutationJson is not null && (AutomationMutationJson.Length > 1024 || System.Text.Json.JsonSerializer.Deserialize<TenantAutomationMutationReceipt>(AutomationMutationJson) is not { } mutation || !AgentCore.Application.Security.TenantAutomationMutationEvidence.Valid(mutation))) throw new ArgumentException("Invalid Automation mutation receipt.");
        if (ManagementSkillJson is not null) _ = AgentCore.Application.Security.TenantManagementSkillRules.ReadEvidence(ManagementSkillJson);
        if (ManagementResourceJson is not null) _ = AgentCore.Application.Security.TenantManagementResourceRules.ReadEvidence(ManagementResourceJson);
        if (BrowserArtifactsJson is not null) _ = AgentCore.Application.Security.TenantBrowserArtifactRules.Read(BrowserArtifactsJson);
        return new(OperationId, RequestId, TenantId, Guid.Parse(RunId), Guid.Parse(SessionId), Guid.Parse(RootActivationId),
        ExecutorPrincipalId, Guid.Parse(InstanceId), ActionHash, (TenantOperationOutcome)Outcome, ReasonCode, InstanceRevision, PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(RecordedAt), SourceCallId, ParametersHash, BrowserArtifactsJson, AutomationMutationJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<TenantAutomationMutationReceipt>(AutomationMutationJson), ManagementSkillJson is null ? null : AgentCore.Application.Security.TenantManagementSkillRules.ReadEvidence(ManagementSkillJson), ManagementResourceJson is null ? null : AgentCore.Application.Security.TenantManagementResourceRules.ReadEvidence(ManagementResourceJson));
    }
    internal static TenantOperationReceiptRecord From(TenantOperationReceipt r) => new(r.OperationId, r.RequestId, r.TenantId, r.RunId.ToString("D"),
        r.SessionId.ToString("D"), r.RootActivationId.ToString("D"), r.ExecutorPrincipalId, r.InstanceId.ToString("D"), r.ActionHash, (int)r.Outcome,
        r.ReasonCode, r.InstanceRevision, r.PersonaRevision, r.RecordedAt.ToUnixTimeMilliseconds(), r.SourceCallId, r.ParametersHash, r.BrowserArtifactsJson, r.AutomationMutation is null ? null : System.Text.Json.JsonSerializer.Serialize(r.AutomationMutation), r.ManagementSkill is null ? null : System.Text.Json.JsonSerializer.Serialize(r.ManagementSkill), r.ManagementResource is null ? null : System.Text.Json.JsonSerializer.Serialize(r.ManagementResource));
    internal static void Configure(ModelBuilder model)
    {
        var request = model.Entity<TenantApprovalRecord>();
        request.HasAlternateKey(r => new { r.RequestId, r.RootActivationId, r.InstanceId });
        var e = model.Entity<TenantOperationReceiptRecord>();
        e.ToTable("TenantOperationReceipts", t =>
        {
            t.HasCheckConstraint("CK_TenantOperationReceipt_ManagementResource", Persistence.Migrations.TenantManagementResourceAuthoring.ReceiptConstraint);
            t.HasCheckConstraint("CK_TenantOperationReceipt_ManagementSkill", ManagementSkillReceiptPersistence.Shape);
            t.HasCheckConstraint("CK_TenantOperationReceipt_Values", "Outcome IN (0,1,2) AND InstanceRevision >= 1 AND PersonaRevision >= 1 AND length(ActionHash) = 64 AND length(ReasonCode) BETWEEN 1 AND 128");
            t.HasCheckConstraint("CK_TenantOperationReceipt_AutomationMutation", "AutomationMutationJson IS NULL OR COALESCE((Outcome=0 AND ReasonCode='automation_mutation_completed' AND length(CAST(AutomationMutationJson AS BLOB)) BETWEEN 2 AND 1024 AND json_valid(AutomationMutationJson) AND json_type(AutomationMutationJson)='object' AND json_type(AutomationMutationJson,'$.AutomationId')='text' AND length(json_extract(AutomationMutationJson,'$.AutomationId'))=36 AND json_type(AutomationMutationJson,'$.ExpectedRevision')='integer' AND json_type(AutomationMutationJson,'$.Revision')='integer' AND json_extract(AutomationMutationJson,'$.ExpectedRevision')>=0 AND json_extract(AutomationMutationJson,'$.Revision')=json_extract(AutomationMutationJson,'$.ExpectedRevision')+1 AND json_extract(AutomationMutationJson,'$.ToolName') IN ('automation.create','automation.update','automation.enable','automation.disable','automation.delete','automation.run') AND json_type(AutomationMutationJson,'$.AfterHash')='text' AND length(json_extract(AutomationMutationJson,'$.AfterHash'))=64 AND json_extract(AutomationMutationJson,'$.AfterHash') NOT GLOB '*[^0-9a-f]*' AND (json_extract(AutomationMutationJson,'$.ExpectedRevision')=0 AND json_extract(AutomationMutationJson,'$.BeforeHash') IS NULL OR json_extract(AutomationMutationJson,'$.ExpectedRevision')>0 AND length(json_extract(AutomationMutationJson,'$.BeforeHash'))=64) AND (json_extract(AutomationMutationJson,'$.ToolName')='automation.run' AND json_type(AutomationMutationJson,'$.InvocationOccurrenceId')='text' AND length(json_extract(AutomationMutationJson,'$.InvocationOccurrenceId'))=36 AND json_type(AutomationMutationJson,'$.InvocationSessionId')='text' AND length(json_extract(AutomationMutationJson,'$.InvocationSessionId'))=36 AND json_type(AutomationMutationJson,'$.InvocationRunId')='text' AND length(json_extract(AutomationMutationJson,'$.InvocationRunId'))=36 OR json_extract(AutomationMutationJson,'$.ToolName')<>'automation.run' AND json_extract(AutomationMutationJson,'$.InvocationOccurrenceId') IS NULL AND json_extract(AutomationMutationJson,'$.InvocationSessionId') IS NULL AND json_extract(AutomationMutationJson,'$.InvocationRunId') IS NULL)),0)");
            t.HasCheckConstraint("CK_TenantOperationReceipt_Source", "(RequestId IS NOT NULL AND SourceCallId IS NULL AND ParametersHash IS NULL) OR (RequestId IS NULL AND SourceCallId IS NOT NULL AND length(SourceCallId) BETWEEN 1 AND 200 AND ParametersHash IS NOT NULL AND length(ParametersHash) = 64)");
        });
        e.HasKey(r => r.OperationId); e.HasIndex(r => r.RequestId).IsUnique(); e.HasIndex(r => new { r.TenantId, r.RunId, r.RecordedAt });
        e.HasIndex(r => new { r.TenantId, r.RunId, r.SourceCallId }).IsUnique().HasFilter("SourceCallId IS NOT NULL");
        e.HasOne<TenantApprovalRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.RequestId, r.ExecutorPrincipalId, r.OperationId, r.ActionHash })
            .HasPrincipalKey(r => new { r.TenantId, r.RequestId, r.ExecutorPrincipalId, r.OperationId, r.Fingerprint }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantApprovalRecord>().WithMany().HasForeignKey(r => new { r.RequestId, r.RootActivationId, r.InstanceId })
            .HasPrincipalKey(r => new { r.RequestId, r.RootActivationId, r.InstanceId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => new { r.RunId, r.SessionId }).HasPrincipalKey(r => new { r.AgentRunId, r.SessionId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AgentRunRecord>().WithMany().HasForeignKey(r => new { r.RunId, r.RootActivationId }).HasPrincipalKey(r => new { r.AgentRunId, r.RootActivationId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<TenantSessionRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SessionId, r.InstanceId, r.ExecutorPrincipalId })
            .HasPrincipalKey(r => new { r.TenantId, r.SessionId, r.AgentInstanceId, r.ExecutorPrincipalId }).OnDelete(DeleteBehavior.Restrict);
    }
}
