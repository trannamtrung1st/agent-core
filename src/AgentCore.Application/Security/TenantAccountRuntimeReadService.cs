using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAccountPreparedRead(AgentRun Run, TenantSecurityState State, TenantAgent Agent, PreparedAccountRead Read, TenantApprovalSnapshot Snapshot);

public sealed partial class TenantAccountRuntimeReadService(IIamStore security, ITenantRunStore runs, ITenantSessionStore sessions,
    ITenantCredentialBindingStore bindings, TenantRunConfigurationResolver configuration, TenantOperationAuthorizer operations,
    IamAuthorizer iam, ITenantApprovalStore approvals, ITenantApprovedOperationStore receipts, ITenantConnectedAccountReadStore reads,
    ITenantConnectedAccountReader provider, IIdGenerator ids, TimeProvider time,
    IDriveOperationStore? driveOperations = null, AgentCore.Application.Drive.DriveService? drive = null, ITenantConnectedAccountTransportFactory? transports = null)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private sealed record Source(AgentRun Run, TenantSecurityState State, TenantAgent Agent, TenantSession Session,
        AgentDefinition Baseline, TenantCredentialBinding Bound);
    private static TenantAccountReadWitness Fingerprint(TenantCredentialBinding bound) => TenantAccountReadReferences.Witness(bound);

    public async ValueTask<TenantAccountPreparedRead> PrepareAsync(TenantRuntimeFrame frame, TenantAccountReadIntent intent, CancellationToken ct = default)
    {
        var args = TenantAccountReadContract.Parse(intent.ToolName, intent.ArgumentsJson);
        var source = await SourceAsync(frame, args.Reference, ct).ConfigureAwait(false);
        var read = new PreparedAccountRead(frame.SessionId, intent.ToolName, args.Json, TenantAccountReadRules.Witness(source.Bound), args.Purpose,
            args.OperationId is { } operation ? await PrepareOperationAsync(source, operation, ct).ConfigureAwait(false) : null);
        await RequireOriginalAsync(source, read, ct).ConfigureAwait(false);
        RequireSource(source, read); await TenantAccountReadRules.PermissionsAsync(source.State, read, source.Agent.PrincipalId, iam, false, ct).ConfigureAwait(false);
        var final = await SourceAsync(frame, args.Reference, ct).ConfigureAwait(false);
        if (source.Run.Revision != final.Run.Revision || source.State.Tenant.Revision != final.State.Tenant.Revision
            || source.Agent.Instance.Revision != final.Agent.Instance.Revision || Fingerprint(source.Bound) != Fingerprint(final.Bound))
            throw new SecurityConflictException("account_source_changed");
        return new(source.Run, source.State, source.Agent, read, TenantAccountReadRules.Snapshot(source.Agent, source.Bound));
    }
    private void RequireSource(Source source, PreparedAccountRead read, bool exactSecret = true)
    {
        TenantAccountReadRules.Require(read, source.Run, source.Agent, source.Session, source.Baseline, source.Bound, exactSecret);
        if (!provider.Availability(source.Bound.Credential.Value).Available) throw new AccessDeniedException("account_unavailable");
    }
    private async ValueTask<Source> SourceAsync(TenantRuntimeFrame frame, string reference, CancellationToken ct, bool live = true)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (run.SessionId != frame.SessionId || run.Owner != frame.Owner) throw new AccessDeniedException("tenant_context_mismatch");
        var (agent, baseline) = await configuration.BaselineAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        var snapshot = await configuration.CurrentCredentialBindingsAsync(run, ct, bindings).ConfigureAwait(false) ?? throw new AccessDeniedException("account_unavailable");
        var bound = snapshot.Bindings.SingleOrDefault(b => b.Binding.Reference == reference) ?? throw new AccessDeniedException("account_unavailable");
        if (snapshot.Agent.Instance.Revision != agent.Instance.Revision || snapshot.Agent.PrincipalId != run.Owner.ExecutorPrincipalId)
            throw new SecurityConflictException("account_source_changed");
        var final = await runs.FindTenantRunAsync(tenant, frame.RunId, ct).ConfigureAwait(false);
        if (final?.Revision != run.Revision || (await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("account_source_changed");
        return new(run, state, agent, session, baseline, bound);
    }
    public async ValueTask RequireInspectionAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default)
    {
        var read = request.Operation.AccountRead ?? throw new AccessDeniedException("account_read_unavailable");
        var source = await SourceAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty), read.Credential.Reference, ct, live: false).ConfigureAwait(false);
        if (source.Run.Revision != run.Revision || source.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || source.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new SecurityConflictException("operation_witness_changed");
        RequireSource(source, read); await RequireOriginalAsync(source, read, ct).ConfigureAwait(false);
        await TenantAccountReadRules.PermissionsAsync(source.State, read, source.Agent.PrincipalId, iam, request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
    }
    private async ValueTask RequireReceiptAsync(Source source, TenantApprovalRequest request, TenantOperationReceipt receipt, CancellationToken ct, bool stored = false)
    {
        await RequireOriginalAsync(source, request.Operation.AccountRead!, ct).ConfigureAwait(false);
        var decision = await receipts.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var grant = await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false);
        TenantAccountReadRules.Receipt(request, source.Run, receipt, decision, grant);
        if (!stored && (request.ExpiresAt <= time.GetUtcNow() || grant?.ExpiresAt <= time.GetUtcNow())) throw new AccessDeniedException("account_read_expired");
        if (await approvals.FindApprovalAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) != request
            || await receipts.FindOperationReceiptAsync(request.TenantId, receipt.OperationId, ct).ConfigureAwait(false) != receipt)
            throw new AccessDeniedException("account_read_unavailable");
        await TenantAccountReadRules.PermissionsAsync(source.State, request.Operation.AccountRead!, source.Agent.PrincipalId, iam, request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
        await TenantAccountReadRules.ReviewerAsync(source.State, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
    }
    public async ValueTask<string> InspectApprovedForRunAsync(TenantRuntimeFrame frame, TenantApprovalRequest request, TenantOperationReceipt receipt,
        int remainingBytes, CancellationToken ct = default)
    {
        var read = request.Operation.AccountRead ?? throw new AccessDeniedException("account_read_unavailable");
        var input = TenantAccountReadContract.Parse(read.ToolName, read.ArgumentsJson);
        var initial = await SourceAsync(frame, read.Credential.Reference, ct).ConfigureAwait(false);
        RequireSource(initial, read); await RequireReceiptAsync(initial, request, receipt, ct).ConfigureAwait(false);
        var expected = initial.Bound.Credential.Value;
        async ValueTask<Source> Current(CancellationToken token)
        {
            var source = await SourceAsync(frame, read.Credential.Reference, token).ConfigureAwait(false);
            RequireSource(source, read, exactSecret: false);
            if (source.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || source.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision
                || !TenantConnectedAccountRules.Same(source.Bound.Credential.Value, expected)) throw new SecurityConflictException("account_source_changed");
            await RequireReceiptAsync(source, request, receipt, token).ConfigureAwait(false); return source;
        }
        async ValueTask<Credential> Recheck(CancellationToken token) => (await Current(token).ConfigureAwait(false)).Bound.Credential.Value;
        async ValueTask Maintain(CredentialOAuthCheck result, CancellationToken token)
        {
            if (result.RotatedProtectedPayload is null && !result.ReconnectRequired) return;
            var source = await Current(token).ConfigureAwait(false);
            await CommitAsync(source, request, result, token).ConfigureAwait(false);
            expected = TenantCredentialOAuthRules.Checked(expected, result, time.GetUtcNow());
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ConnectedAccountReadResult result;
        try { result = read.Source is not null ? await InspectOperationAsync(initial, read, Recheck, Maintain, linked.Token).ConfigureAwait(false)
                : await provider.ReadAsync(new(request.TenantId, request.Operation.InstanceId, initial.Bound.Binding, expected), input.Request, Recheck, Maintain, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AgentCoreException("account_read_timeout", "The account read timed out.", 504); }
        catch (Exception) when (!ct.IsCancellationRequested) { await Current(ct).ConfigureAwait(false); throw; }
        ct.ThrowIfCancellationRequested(); linked.Token.ThrowIfCancellationRequested(); if (read.Source is null) TenantConnectedAccountRules.ValidateResult(input.Request, result);
        var final = await Current(ct).ConfigureAwait(false);
        var text = JsonSerializer.Serialize(new { tenantAccountRead = true, accountReadRequestId = request.RequestId, accountReadOperationId = receipt.OperationId,
            accountReadTool = read.ToolName, accountReadWitness = Fingerprint(final.Bound), untrusted = true, result }, WebJson);
        if (Encoding.UTF8.GetByteCount(text) > remainingBytes) throw new AccessDeniedException("account_result_too_large");
        await CommitAsync(final, request, null, ct).ConfigureAwait(false); await Current(ct).ConfigureAwait(false); return text;
    }
    private async ValueTask CommitAsync(Source source, TenantApprovalRequest request, CredentialOAuthCheck? maintenance, CancellationToken ct)
    {
        var audit = new AuthorizationAudit(ids.NewId(), request.TenantId, request.ExecutorPrincipalId,
            maintenance is null ? "operations.accounts.read" : "operations.accounts.oauth.maintain", request.ResourceId, "allowed",
            maintenance is null ? "connected_account_read" : maintenance.ReconnectRequired ? "oauth_reconnect_required" : "oauth_grant_rotated", time.GetUtcNow());
        var proof = new TenantRuntimeAccountReadProof(new(source.Run.Owner, source.Run.SessionId, source.Run.AgentRunId, source.Run.Claim!.Generation), source.Run.Revision,
            request.RequestId, request.Operation.OperationId, source.Bound.Credential.Value.Revision, source.Bound.Credential.Value.SecretRevision);
        if (!await reads.TryCommitRuntimeAccountReadAsync(source.State with { Tenant = source.State.Tenant with { Revision = checked(source.State.Tenant.Revision + 1) } },
            source.State.Tenant.Revision, proof, maintenance, audit, ct).ConfigureAwait(false)) throw new SecurityConflictException("account_source_changed");
    }
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default, bool live = true)
    {
        foreach (var reference in TenantAccountReadReferences.Read(run))
        {
            var request = await approvals.FindApprovalAsync(run.Owner.TenantId!.Value, reference.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("account_read_unavailable");
            var read = request.Operation.AccountRead ?? throw new AccessDeniedException("account_read_unavailable");
            var receipt = await receipts.FindOperationReceiptAsync(request.TenantId, reference.OperationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("account_read_unavailable");
            var current = await SourceAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty), read.Credential.Reference, ct, live).ConfigureAwait(false);
            RequireSource(current, read, exactSecret: false); await RequireReceiptAsync(current, request, receipt, ct, stored: true).ConfigureAwait(false);
            if (current.Run.Revision != run.Revision) throw new AccessDeniedException("account_source_changed");
            TenantAccountReadReferences.Require(reference, request, current.Bound, current.Agent);
        }
    }
}
