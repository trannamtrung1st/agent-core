using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
namespace AgentCore.Infrastructure.Drive;

public sealed class GoogleDriveProviderFactory(GoogleDriveOptions options, GoogleDriveOAuthService oauth, IHttpClientFactory clients,
    IDriveOperationStore operations, ISyntheticDriveStore syntheticFiles, ICredentialProtector protector, IIdGenerator ids, TimeProvider time, bool synthetic) : IGoogleDriveProviderFactory
{
    public bool Enabled => options.Enabled;
    public bool Configured => oauth.IsConfigured;
    public bool Synthetic => synthetic;
    public IGoogleDriveProvider Create(Guid credential, long revision) => !Enabled ? throw new AgentCoreException("drive_host_disabled", "Google Drive is disabled on this host.", 403)
        : synthetic ? new SyntheticGoogleDriveProvider(credential, syntheticFiles, ids) : new GoogleDriveProvider(credential, revision, oauth, clients, operations, protector, time);
}
