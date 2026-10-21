using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task<TenantSession?> RequireAutomationConversation(AgentCoreDbContext db, TenantSecurityState state,
        AgentCore.Domain.Triggers.Automation value, Guid? human, PreparedAutomationConversation? witness, CancellationToken ct)
    {
        if (!TenantAutomationConversationRules.Existing(value)) return null;
        var root = await OwnedSessions(db, state.Tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == value.ExecutionTarget.SessionId!.Value.ToString("D"), ct);
        var target = TenantAutomationConversationRules.Require(value, root is null ? null : await SessionView(db, root, ct), witness);
        if (human is { } principal) await TenantAutomationConversationRules.AuthoringAsync(state, principal, value, target, new IamAuthorizer(this), ct);
        return target;
    }
    private async Task RequireAutomationConversationAdmission(AgentCoreDbContext db, TenantSecurityState state, TenantAutomationAdmission admission,
        Guid? human, PreparedAutomationAdmission? consent, bool preparing, CancellationToken ct)
    {
        var target = await RequireAutomationConversation(db, state, admission.Current, human, consent?.Conversation, ct);
        if (target is null) return;
        if (target.Owner != admission.SessionOwner || target.Snapshot.Revision != admission.ExpectedSessionRevision
            || JsonSerializer.Serialize(target.Snapshot) != JsonSerializer.Serialize(admission.Session)) throw new SecurityConflictException("operation_witness_changed");
        if (!preparing && (target.Snapshot.PendingAgentInputIds.Count > 0 || await db.AgentRuns.AnyAsync(r => r.SessionId == target.Owner.SessionId.ToString("D")
            && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Cancelled && r.Status != (int)AgentRunStatus.Failed, ct)))
            throw new SecurityConflictException("parent_busy");
    }
    private async Task StageTenantAutomationGraph(AgentCoreDbContext db, TenantAutomationAdmission admission, CancellationToken ct)
    {
        if (!TenantAutomationConversationRules.Existing(admission.Current))
        { await StageTenantBackgroundGraph(db, admission.SessionOwner, admission.Session, admission.Run, ct); return; }
        db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(admission.Session, admission.Run));
        db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(admission.Run));
    }
}
