using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using OpenSandbox.Models;

namespace AgentCore.Infrastructure.Sandbox;

public sealed class SandboxImageAuthority(OpenSandboxOptions options, ISandboxStore profiles, TimeProvider time,
    ICredentialStore? credentials = null, ICredentialProtector? protector = null) : ISandboxImageAuthority
{
    public async ValueTask<SandboxProfile> ResolveAsync(SandboxProfile profile, CancellationToken ct = default)
    {
        OciImageSource source;
        try { source = OciImageSource.Parse(profile.ImageReference); }
        catch (ArgumentException) { throw AgentCoreErrors.Validation("Enter a valid image, such as mcr.microsoft.com/dotnet/sdk:10.0, ubuntu:24.04 or registry/repository@sha256:<64 lowercase hex characters>."); }
        var candidate = profile with { ImageReference = source.Image.Value };
        candidate.Validate();
        // Check host and credential scope before making any registry request or revealing secrets.
        try { options.ValidateProfile(candidate); }
        catch (ArgumentException) { throw AgentCoreErrors.Validation("Image registry or repository is not allowed by host policy. Use an approved source shown in Sandboxes or update the host image allowlist."); }
        if (source.Tag is not null)
        {
            var auth = await ResolveAuthAsync(candidate, ct, requireVerification: false);
            candidate = candidate with { ImageReference = await RegistryAuthorizationProbe.ResolveTagAsync(options, source.Image, source.Tag, auth, ct) };
        }
        options.ValidateProfile(candidate);
        return candidate;
    }

    public async ValueTask<SandboxImageAvailability> InspectAsync(SandboxProfile profile, CancellationToken ct = default, bool requireVerification = true)
    {
        try { options.ValidateProfile(profile); }
        catch (ArgumentException) { return new("Forbidden", "Image is not permitted by current host policy. Repair the saved source in Sandboxes.", false); }
        long? revision = null, secretRevision = null;
        if (profile.RegistryCredentialId is { } id)
        {
            var credential = credentials is null ? null : await credentials.GetAsync(id, ct);
            if (credential is not { Purpose: CredentialPurpose.ContainerRegistry, Status: CredentialStatus.Active, ContainerRegistry: { } scope }
                || scope.ExpiresAtUtc <= time.GetUtcNow() || !scope.Allows(OciImageReference.Parse(profile.ImageReference)))
                return new("SourceUnavailable", "Registry credential is missing, disabled, expired or outside its repository scope. Manage Credentials, then verify again.", false);
            revision = credential.Revision; secretRevision = credential.SecretRevision;
        }
        if (!requireVerification) return new("SourceAuthorized", "Source authority is valid; image compatibility still requires a disposable check.", false, revision, secretRevision);
        if (!profile.Enabled) return new("Disabled", "Profile is disabled. Enabling appends a revision that requires verification.", false, revision, secretRevision);
        var observation = await profiles.GetVerificationAsync(profile.Id, profile.Revision, ct);
        if (observation is not { ContractVersion: 1 } || observation.RegistryCredentialId != profile.RegistryCredentialId
            || observation.RegistryCredentialRevision != revision || observation.RegistrySecretRevision != secretRevision)
            return new("NeedsVerification", "Verify this exact saved revision under current source authority before using commands.", false, revision, secretRevision);
        return observation.Passed ? new("Eligible", "Verified for the current image and credential revision.", true, revision, secretRevision)
            : new("VerificationFailed", observation.Message, false, revision, secretRevision);
    }

    internal async ValueTask<ImageAuth?> ResolveAuthAsync(SandboxProfile profile, CancellationToken ct, bool requireVerification)
    {
        var available = await InspectAsync(profile, ct, requireVerification);
        if (requireVerification ? !available.Eligible : available.Status != "SourceAuthorized") throw AgentCoreErrors.Forbidden(available.Message);
        if (profile.RegistryCredentialId is not { } id) return null;
        var credential = await credentials!.GetAsync(id, ct);
        // Recheck the epoch after lookup, including a Run's admission pins, before revealing a value to the SDK.
        if (credential is null || credential.Revision != available.CredentialRevision || credential.SecretRevision != available.SecretRevision
            || credential.Status != CredentialStatus.Active || credential.ContainerRegistry is not { } scope || scope.ExpiresAtUtc <= time.GetUtcNow()
            || !scope.Allows(OciImageReference.Parse(profile.ImageReference))
            || requireVerification && (profile.RegistryCredentialRevision != credential.Revision || profile.RegistrySecretRevision != credential.SecretRevision))
            throw AgentCoreErrors.Forbidden("Registry authorization changed. Verify the Profile and start a new Run.");
        return new ImageAuth { Username = scope.Username, Password = protector!.Unprotect(id, credential.ProtectionVersion, credential.ProtectedPayload) };
    }
}
