using System.Collections.Concurrent;
using System.Security.Cryptography;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Drive;
using AgentCore.Infrastructure.Email;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Credentials;

public sealed partial class TenantConnectedAccountReader(TenantGoogleOAuthProvider google, EmailTransportOptions emailHost, GoogleDriveOptions driveHost,
    IHttpClientFactory clients, ICredentialProtector protector, IDriveOperationStore driveOperations, ISyntheticDriveStore driveFiles,
    IIdGenerator ids, TimeProvider time, bool synthetic, ILoggerFactory logs, IDiagnosticIdSource diagnostics) : ITenantConnectedAccountReader, ITenantConnectedAccountTransportFactory, ITenantEmailChangeFeed
{
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Credential), SyntheticEmailProvider> mailboxes = new();
    private readonly SemaphoreSlim mailConnections = new(4);
    public ConnectedAccountAvailability Availability(Credential c) => Availability(c, false);
    public ConnectedAccountAvailability Availability(Credential c, bool send)
    {
        if (send && (c.Purpose != CredentialPurpose.EmailAccount || !emailHost.Enabled || (synthetic ? emailHost.SendEnabled == false : !emailHost.EffectiveSendEnabled)
            || c.EmailAccount?.CanSend != true || !synthetic && c.EmailAccount.AuthMethod == EmailAuthMethod.Password && !emailHost.SmtpEnabled))
            return new(false, "SendUnavailable");
        if (c.Status != CredentialStatus.Active || c.ProtectedPayload.Length == 0) return new(false, "Unavailable");
        if (c.Purpose == CredentialPurpose.EmailAccount && c.EmailAccount is { } email)
        {
            if (!emailHost.Enabled) return new(false, "HostDisabled");
            if (email.AuthMethod == EmailAuthMethod.Password) return new(email.IsUsable, email.IsUsable ? "Configured" : "Unavailable");
            var descriptor = google.Describe(c.Purpose);
            return new(email.IsUsable && email.Synthetic == synthetic && descriptor.Configured && descriptor.HostEnabled,
                !descriptor.Configured ? "NotConfigured" : email.Synthetic != synthetic ? "ProfileMismatch" : email.ConnectionStatus.ToString());
        }
        if (c.Purpose == CredentialPurpose.GoogleDriveAccount && c.DriveAccount is { } drive)
        {
            var descriptor = google.Describe(c.Purpose);
            return new(driveHost.Enabled && drive.IsUsable && drive.Synthetic == synthetic && descriptor.Configured,
                !driveHost.Enabled ? "HostDisabled" : !descriptor.Configured ? "NotConfigured" : drive.Synthetic != synthetic ? "ProfileMismatch" : drive.ConnectionStatus.ToString());
        }
        return new(false, "Unavailable");
    }
    public async ValueTask<ConnectedAccountReadResult> ReadAsync(TenantConnectedAccount account, ConnectedAccountRead request,
        Func<CancellationToken, ValueTask<Credential>> recheck, Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintenance, CancellationToken ct = default)
    {
        TenantConnectedAccountRules.Validate(request); TenantConnectedAccountRules.Purpose(account.Credential, request.Kind);
        async ValueTask<Credential> Current(CancellationToken token)
        {
            var value = await recheck(token).ConfigureAwait(false);
            if (value.CredentialId != account.Credential.CredentialId || value.Purpose != account.Credential.Purpose || !Availability(value).Available)
                throw new AccessDeniedException("account_unavailable");
            return value;
        }
        var transport = await OpenAsync(account, Current, maintenance, ct: ct).ConfigureAwait(false);
        ConnectedAccountReadResult result;
        if (account.Credential.Purpose == CredentialPurpose.EmailAccount)
        {
            var mailbox = transport.Email!;
            result = request.Kind == ConnectedAccountReadKind.EmailSearch
                ? new(Mailbox: await mailbox.SearchAsync(new(request.Query ?? "", Math.Min(25, request.Limit)), ct).ConfigureAwait(false))
                : new(Message: await mailbox.ReadAsync(new(request.Id!), ct).ConfigureAwait(false));
        }
        else
        {
            var drive = transport.Drive!;
            if (request.Kind == ConnectedAccountReadKind.DriveList)
                result = new(Files: await drive.ListAsync(request.Folder, request.Query, request.Cursor, request.Limit, ct).ConfigureAwait(false));
            else
            {
                var file = await drive.GetAsync(request.Id!, ct).ConfigureAwait(false); await Current(ct).ConfigureAwait(false);
                if (file.Id != request.Id || file.Trashed) throw new AccessDeniedException("account_result_invalid");
                if (request.Kind == ConnectedAccountReadKind.DriveFile) result = new(File: file);
                else
                {
                    if (file.Folder || file.NativeDocument) throw new AgentCoreException("drive_native_unsupported", "Download supports original binary files only.", 400);
                    if (file.Version != request.ExpectedVersion) throw new SecurityConflictException("drive_remote_conflict");
                    if (file.ByteSize > request.MaxBytes) throw new AgentCoreException("drive_transfer_too_large", "The file exceeds this read's byte limit.", 413);
                    var bytes = await drive.DownloadAsync(file, request.MaxBytes, ct).ConfigureAwait(false); await Current(ct).ConfigureAwait(false);
                    if (bytes.LongLength != file.ByteSize || bytes.Length > request.MaxBytes || file.Md5Checksum is not null
                        && !string.Equals(Convert.ToHexStringLower(MD5.HashData(bytes)), file.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                        throw new SecurityConflictException("drive_remote_conflict");
                    var after = await drive.GetAsync(file.Id, ct).ConfigureAwait(false); await Current(ct).ConfigureAwait(false);
                    if (after.Id != file.Id || after.Version != file.Version || after.Trashed || after.ByteSize != file.ByteSize
                        || after.Md5Checksum != file.Md5Checksum) throw new SecurityConflictException("drive_remote_conflict");
                    result = new(File: file, Bytes: bytes);
                }
            }
        }
        await Current(ct).ConfigureAwait(false); TenantConnectedAccountRules.ValidateResult(request, result); return result;
    }
    public async ValueTask<TenantConnectedAccountTransport> OpenAsync(TenantConnectedAccount account,
        Func<CancellationToken, ValueTask<Credential>> recheck, Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintenance,
        bool send = false, CancellationToken ct = default)
    {
        if (account.TenantId == Guid.Empty || account.InstanceId == Guid.Empty || account.Binding.AgentInstanceId != account.InstanceId
            || account.Binding.CredentialId != account.Credential.CredentialId) throw new AccessDeniedException("account_source_changed");
        async ValueTask<Credential> Current(CancellationToken token)
        {
            var value = await recheck(token).ConfigureAwait(false);
            if (value.CredentialId != account.Credential.CredentialId || value.Purpose != account.Credential.Purpose || !Availability(value, send).Available)
                throw new AccessDeniedException("account_unavailable");
            return value;
        }
        string? access = null;
        async ValueTask<string> Token(CancellationToken token, bool fresh = false)
        {
            await Current(token).ConfigureAwait(false);
            if (access is null || fresh) access = await google.RuntimeTokenAsync(Current, maintenance, token).ConfigureAwait(false);
            await Current(token).ConfigureAwait(false); return access;
        }
        async ValueTask Reject(CancellationToken token)
        { await Current(token).ConfigureAwait(false); await maintenance(new(ReconnectRequired: true), token).ConfigureAwait(false); }
        var c = await Current(ct).ConfigureAwait(false);
        if (c.Purpose == CredentialPurpose.EmailAccount)
        {
            var context = new EmailAccountContext(account.InstanceId, c.CredentialId, account.Binding.Reference, c.DisplayName, c.Revision,
                account.Binding.BindingId, account.Binding.Revision, c.EmailAccount!);
            IEmailProvider mailbox = synthetic
                ? new ScopedSyntheticEmailProvider(context, mailboxes.GetOrAdd((account.TenantId, c.CredentialId), _ => new()),
                    async (_, token) => { await Current(token).ConfigureAwait(false); })
                : c.EmailAccount!.AuthMethod == EmailAuthMethod.GoogleOAuth2
                ? new GmailEmailProvider(context, clients, token => Token(token), async token => { await Current(token).ConfigureAwait(false); },
                    () => send && Availability(c, true).Available, time, logs.CreateLogger<GmailEmailProvider>(), diagnostics, token => Token(token, true), Reject)
                : new ImapSmtpEmailProvider(context, emailHost, new MailTransportPolicy(emailHost), mailConnections,
                    async (_, token) => { var value = await Current(token).ConfigureAwait(false); return protector.Unprotect(value.CredentialId, value.ProtectionVersion, value.ProtectedPayload); },
                    logs.CreateLogger<ImapSmtpEmailProvider>(), diagnostics);
            return new(new ScopedMail(mailbox, Current, () => send && Availability(c, true).Available), null, synthetic);
        }
        IGoogleDriveProvider drive = synthetic ? new SyntheticGoogleDriveProvider(c.CredentialId, driveFiles, ids)
            : new GoogleDriveProvider(c.CredentialId, Token, Reject, clients, driveOperations, protector, time);
        return new(null, new ScopedDrive(drive, Current), synthetic);
    }

}
