using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantConnectedAccountService(IIamStore security, ITenantCredentialBindingStore bindings,
    ITenantCredentialOAuthStore oauth, ITenantConnectedAccountReadStore reads, ITenantConnectedAccountReader provider,
    IdentityService identity, IamAuthorizer iam, IIdGenerator ids, TimeProvider time)
{
    private sealed record Source(TenantSecurityState State, TenantCredentialBindingSnapshot Snapshot, TenantCredentialBinding Bound);
    public async ValueTask<TenantConnectedAccountPage> ListAsync(Guid tenant, Guid instance, HumanSessionReference human,
        int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        if (page is < 0 or > 10000 || pageSize is < 1 or > 100) throw new ArgumentException("Use bounded account pagination.");
        var before = await SnapshotAsync(tenant, instance, human, ct).ConfigureAwait(false);
        var allowed = new List<TenantConnectedAccountView>();
        foreach (var bound in before.Snapshot.Bindings.OrderBy(b => b.Binding.Reference, StringComparer.Ordinal))
        {
            if (!TenantConnectedAccountRules.Account(bound.Credential.Value)) continue;
            if (!await TenantConnectedAccountRules.AllowedAsync(before.State, bound.Credential.Value, human.PrincipalId, iam, false, ct).ConfigureAwait(false)) continue;
            var value = bound.Credential.Value; var available = provider.Availability(value);
            var canUse = await TenantConnectedAccountRules.AllowedAsync(before.State, value, human.PrincipalId, iam, true, ct).ConfigureAwait(false);
            allowed.Add(new(bound.Binding.Reference, value.DisplayName, value.Purpose.ToString(), value.EmailAccount?.EmailAddress ?? value.DriveAccount!.EmailAddress,
                available.Status, available.Available && canUse && before.Snapshot.Agent.Instance.Lifecycle == Domain.Definitions.AgentInstanceLifecycle.Active,
                value.Revision, bound.Binding.Revision));
        }
        var after = await SnapshotAsync(tenant, instance, human, ct).ConfigureAwait(false);
        if (before.State.Tenant.Revision != after.State.Tenant.Revision || before.Snapshot.Agent.Instance.Revision != after.Snapshot.Agent.Instance.Revision
            || !before.Snapshot.Bindings.Select(TenantConnectedAccountRules.Fingerprint).SequenceEqual(after.Snapshot.Bindings.Select(TenantConnectedAccountRules.Fingerprint)))
            throw new SecurityConflictException("account_source_changed");
        return new(allowed.Skip(page * pageSize).Take(pageSize).ToArray(), allowed.Count, page, pageSize);
    }

    public async ValueTask<ConnectedAccountReadResult> ReadAsync(Guid tenant, Guid instance, string accountRef,
        HumanSessionReference human, ConnectedAccountRead request, CancellationToken ct = default)
    {
        TenantConnectedAccountRules.Validate(request); accountRef = CredentialRules.Reference(accountRef);
        var before = await SourceAsync(tenant, instance, accountRef, human, ct).ConfigureAwait(false);
        TenantConnectedAccountRules.Purpose(before.Bound.Credential.Value, request.Kind);
        var available = provider.Availability(before.Bound.Credential.Value);
        if (!available.Available) throw new AccessDeniedException("account_unavailable");
        var expected = before.Bound.Credential.Value;
        async ValueTask<Source> Current(CancellationToken token)
        {
            var fresh = await SourceAsync(tenant, instance, accountRef, human, token).ConfigureAwait(false);
            if (fresh.Snapshot.Agent.Instance.Revision != before.Snapshot.Agent.Instance.Revision || fresh.Bound.Binding != before.Bound.Binding
                || !TenantConnectedAccountRules.Same(expected, fresh.Bound.Credential.Value)) throw new SecurityConflictException("account_source_changed");
            if (!provider.Availability(fresh.Bound.Credential.Value).Available) throw new AccessDeniedException("account_unavailable");
            return fresh;
        }
        async ValueTask<Credential> Recheck(CancellationToken token) => (await Current(token).ConfigureAwait(false)).Bound.Credential.Value;
        async ValueTask Maintain(CredentialOAuthCheck result, CancellationToken token)
        {
            if (result.RotatedProtectedPayload is null && !result.ReconnectRequired) return;
            var fresh = await Current(token).ConfigureAwait(false); var value = fresh.Bound.Credential.Value;
            var replacement = TenantCredentialOAuthRules.Checked(value, result, time.GetUtcNow());
            var next = fresh.State with { Tenant = fresh.State.Tenant with { Revision = checked(fresh.State.Tenant.Revision + 1) } };
            var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, "credentials.oauth.read_maintenance", value.CredentialId.ToString("D"),
                "allowed", result.ReconnectRequired ? "oauth_reconnect_required" : "oauth_grant_rotated", time.GetUtcNow());
            if (!await oauth.TryCheckCredentialOAuthAsync(next, fresh.State.Tenant.Revision, value.CredentialId,
                new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), value.Revision, value.SecretRevision, result, audit, token).ConfigureAwait(false))
                throw new SecurityConflictException("account_source_changed");
            expected = replacement;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ConnectedAccountReadResult result;
        try { result = await provider.ReadAsync(new(tenant, instance, before.Bound.Binding, expected), request, Recheck, Maintain, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AgentCoreException("account_read_timeout", "The connected account read timed out. Retry the read.", 504); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // An adapter may normalize its callback failure as transport unavailability. Current human/source authority wins.
            await Current(ct).ConfigureAwait(false); throw;
        }
        ct.ThrowIfCancellationRequested(); linked.Token.ThrowIfCancellationRequested();
        TenantConnectedAccountRules.ValidateResult(request, result);
        var final = await Current(ct).ConfigureAwait(false);
        var proof = new TenantConnectedAccountReadProof(tenant, instance, final.Snapshot.Agent.Instance.Revision,
            final.Bound.Binding.BindingId, final.Bound.Binding.Revision, expected.CredentialId, expected.Revision, expected.SecretRevision, expected.Purpose,
            human, final.State.Tenant.Revision, new(ids.NewId(), tenant, human.PrincipalId, TenantConnectedAccountRules.Permission(expected),
                expected.CredentialId.ToString("D"), "allowed", "connected_account_read", time.GetUtcNow()));
        if (!await reads.TryRecordConnectedAccountReadAsync(proof, ct).ConfigureAwait(false)) throw new SecurityConflictException("account_source_changed");
        // The commit records one exact check, not future read authority. Original authentication still fences release.
        await Current(ct).ConfigureAwait(false); return result;
    }
    private async ValueTask<(TenantSecurityState State, TenantCredentialBindingSnapshot Snapshot)> SnapshotAsync(Guid tenant, Guid instance,
        HumanSessionReference human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (!(await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("agents.read"),
            AccessScope.ForResource(tenant, "agent", instance.ToString("D")), ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("permission_denied");
        var snapshot = await bindings.ReadTenantCredentialBindingsAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        return (state, snapshot);
    }
    private async ValueTask<Source> SourceAsync(Guid tenant, Guid instance, string accountRef, HumanSessionReference human, CancellationToken ct)
    {
        var (state, snapshot) = await SnapshotAsync(tenant, instance, human, ct).ConfigureAwait(false);
        var bound = snapshot.Bindings.SingleOrDefault(b => b.Binding.Reference == accountRef) ?? throw new AccessDeniedException("resource_unavailable");
        TenantConnectedAccountRules.Owned(tenant, instance, bound);
        if (!await TenantConnectedAccountRules.AllowedAsync(state, bound.Credential.Value, human.PrincipalId, iam, true, ct).ConfigureAwait(false))
            throw new AccessDeniedException("permission_denied");
        TenantSessionRules.RequireExecutor(state, snapshot.Agent);
        if (snapshot.Agent.Instance.Lifecycle != Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("executor_unavailable");
        return new(state, snapshot, bound);
    }
}

public static class TenantConnectedAccountRules
{
    public static async ValueTask<bool> AgentReadAsync(TenantSecurityState state, Guid instance, Guid human, IamAuthorizer iam, CancellationToken ct) =>
        (await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("agents.read"),
            AccessScope.ForResource(state.Tenant.TenantId, "agent", instance.ToString("D")), ct).ConfigureAwait(false)).Allowed;
    public static bool Account(Credential c) => c.Purpose is CredentialPurpose.EmailAccount or CredentialPurpose.GoogleDriveAccount;
    public static string Permission(Credential c) => c.Purpose == CredentialPurpose.EmailAccount ? "email.read" : c.Purpose == CredentialPurpose.GoogleDriveAccount ? "drive.read" : throw new AccessDeniedException("account_unavailable");
    public static void Owned(Guid tenant, Guid instance, TenantCredentialBinding bound)
    {
        if (bound.Credential.Owner.TenantId != tenant || bound.Credential.Owner.DeletedAt is not null || bound.Binding.AgentInstanceId != instance
            || bound.Binding.CredentialId != bound.Credential.Value.CredentialId || !Account(bound.Credential.Value)) throw new AccessDeniedException("resource_unavailable");
    }
    public static async ValueTask<bool> AllowedAsync(TenantSecurityState state, Credential account, Guid human, IamAuthorizer iam, bool use, CancellationToken ct)
    {
        var scope = TenantCredentialRules.Scope(state.Tenant.TenantId, account.CredentialId);
        foreach (var key in use ? new[] { "credentials.read", "credentials.use", Permission(account) } : new[] { "credentials.read", Permission(account) })
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) return false;
        return true;
    }
    public static bool Same(Credential a, Credential b) => a.CredentialId == b.CredentialId && a.Revision == b.Revision && a.SecretRevision == b.SecretRevision
        && a.Purpose == b.Purpose && a.Status == b.Status && a.ProtectedPayload == b.ProtectedPayload;
    public static string Fingerprint(TenantCredentialBinding b) => $"{b.Binding.BindingId:D}:{b.Binding.Revision}:{b.Credential.Value.CredentialId:D}:{b.Credential.Value.Revision}:{b.Credential.Value.SecretRevision}";
    public static void Purpose(Credential c, ConnectedAccountReadKind kind)
    {
        if ((kind is ConnectedAccountReadKind.EmailSearch or ConnectedAccountReadKind.EmailMessage) != (c.Purpose == CredentialPurpose.EmailAccount))
            throw new AccessDeniedException("account_purpose_mismatch");
    }
    public static void Validate(ConnectedAccountRead r)
    {
        if (!Enum.IsDefined(r.Kind) || r.Limit is < 1 or > 100 || r.MaxBytes is < 1 or > DriveService.MaxTransferBytes
            || r.Query?.Length > (r.Kind == ConnectedAccountReadKind.EmailSearch ? 2000 : 200) || r.Cursor?.Length > 2048)
            throw new ArgumentException("Use bounded connected account read arguments.");
        if (r.Kind is ConnectedAccountReadKind.EmailMessage && (r.Id is not { Length: > 0 and <= 512 } || r.Id.Any(char.IsControl))) throw new ArgumentException("Use an exact message reference.");
        if (r.Kind is ConnectedAccountReadKind.DriveFile or ConnectedAccountReadKind.DriveContent) _ = DriveService.Id(r.Id ?? "");
        if (r.Folder is not null) _ = DriveService.Id(r.Folder);
        if (r.Kind == ConnectedAccountReadKind.DriveContent && (r.ExpectedVersion is not { Length: > 0 and <= 160 } || r.ExpectedVersion.Any(char.IsControl)))
            throw new ArgumentException("An exact observed Drive version is required.");
    }
    public static void ValidateResult(ConnectedAccountRead request, ConnectedAccountReadResult result)
    {
        var slots = (result.Mailbox is null ? 0 : 1) + (result.Message is null ? 0 : 1) + (result.Files is null ? 0 : 1) + (result.File is null ? 0 : 1);
        if (result.Operation is not null || slots != 1 || System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(result with { Bytes = null })) > 1024 * 1024)
            throw new AccessDeniedException("account_result_invalid");
        if (request.Kind == ConnectedAccountReadKind.EmailSearch ? result.Mailbox is null || result.Mailbox.Results.Count > Math.Min(request.Limit, 25)
            : request.Kind == ConnectedAccountReadKind.EmailMessage ? result.Message is null || result.Message.MessageId != request.Id
            : request.Kind == ConnectedAccountReadKind.DriveList ? result.Files is null || result.Files.Items.Count > request.Limit
            : result.File is null || result.File.Id != request.Id || result.File.Trashed) throw new AccessDeniedException("account_result_invalid");
        if (result.Bytes is { } bytes && (request.Kind != ConnectedAccountReadKind.DriveContent || bytes.Length > request.MaxBytes
            || result.File!.Version != request.ExpectedVersion)) throw new AccessDeniedException("account_result_invalid");
        if (request.Kind == ConnectedAccountReadKind.DriveContent && result.Bytes is null) throw new AccessDeniedException("account_result_invalid");
    }
    public static void Proof(TenantConnectedAccountReadProof proof)
    {
        if (proof.Purpose is not (CredentialPurpose.EmailAccount or CredentialPurpose.GoogleDriveAccount)
            || proof.Audit.TenantId != proof.TenantId || proof.Audit.ActorPrincipalId != proof.Human.PrincipalId || proof.Audit.Target != proof.CredentialId.ToString("D")
            || proof.Audit.Action != (proof.Purpose == CredentialPurpose.EmailAccount ? "email.read" : "drive.read")
            || proof.Audit.Decision != "allowed" || proof.Audit.ReasonCode != "connected_account_read") throw new ArgumentException("Invalid account read proof.");
    }
}
