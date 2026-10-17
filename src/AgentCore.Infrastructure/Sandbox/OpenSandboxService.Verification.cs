using System.Globalization;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;
using OpenSandbox;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private sealed class VerificationInitializationFailure(string stage, int? exitCode) : Exception
    {
        internal string Stage { get; } = stage;
        internal int? ExitCode { get; } = exitCode;
    }
    public ValueTask<SandboxProfileVerification> VerifyProfileAsync(SandboxProfile profile, CancellationToken ct, bool imageOnly = false)
    {
        if (profile.TenantId is not null) throw new AccessDeniedException("tenant_scope_required");
        return VerifyProfileCoreAsync(profile, ct, imageOnly, Guid.CreateVersion7(), null);
    }
    private async ValueTask<SandboxProfileVerification> VerifyProfileCoreAsync(SandboxProfile profile, CancellationToken ct, bool imageOnly,
        Guid id, Func<CancellationToken, ValueTask>? require)
    {
        var hostScope = profile.TenantId is null ? null : HostScope;
        var started = time.GetUtcNow(); var stdout = new BoundedText(32768); var stderr = new BoundedText(32768);
        if (require is not null) await require(ct);
        var source = await Images.InspectAsync(profile, ct, requireVerification: false);
        SandboxProfileVerification Observation(bool passed, string message, string? code = null, int? exit = null) =>
            new(profile.Id, profile.Revision, time.GetUtcNow(), passed, message, code, exit, stdout.Text, stderr.Text, stdout.Truncated || stderr.Truncated,
                Math.Max(0, (long)(time.GetUtcNow() - started).TotalMilliseconds), passed ? null : Guid.CreateVersion7().ToString("D"),
                SandboxProfileVerification.CurrentContractVersion, profile.RegistryCredentialId, profile.TenantId is null ? source.CredentialRevision : profile.RegistryCredentialRevision,
                profile.TenantId is null ? source.SecretRevision : profile.RegistrySecretRevision, profile.TenantId, !imageOnly, hostScope);
        if (source.Status != "SourceAuthorized") return Observation(false, source.Message, "sourceAuthority");
        if (profile.TenantId is not null && (source.CredentialRevision != profile.RegistryCredentialRevision || source.SecretRevision != profile.RegistrySecretRevision))
            return Observation(false, "Source authority changed before verification.", "authority_changed");
        if (!options.Enabled) return Observation(false, "OpenSandbox is disabled in host configuration.", "disabled");
        var budget = 3 * 45 + profile.MaxCommandSeconds + ProcessCleanupGraceSeconds;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(budget));
        var owner = VerificationOwner(profile.TenantId, id); var phase = "mountPreparation";
        CanonicalSandboxMountResolver.Mounts? mounted = null;
        SdkSandbox? sdk = null;
        var result = Observation(false, "Direct mount verification has not completed.", "mountCompatibility");
        var safeToPurge = true;
        OpenSandbox.Config.ConnectionConfig? connection = null;
        try
        {
            connection = Connection();
            foreach (var access in new[] { HomeAccess.ReadWrite, HomeAccess.ReadOnly, HomeAccess.Disabled })
            {
                if (require is not null) await require(deadline.Token);
                mounted = MountResolver.Verification(id, access, deadline.Token);
                var auth = await Images.ResolveAuthAsync(profile, deadline.Token, requireVerification: false, require);
                if (!OciImageReference.Parse(profile.ImageReference).IsLocal)
                    await RegistryAuthorizationProbe.CheckAsync(options, OciImageReference.Parse(profile.ImageReference), auth, deadline.Token);
                if (require is not null) await require(deadline.Token);
                phase = "provisioning"; safeToPurge = false;
                sdk = await SdkSandbox.CreateAsync(new SandboxCreateOptions {
                    ConnectionConfig = connection, Image = profile.ImageReference, ImageAuth = auth,
                    TimeoutSeconds = Math.Min(profile.MaxLifetimeSeconds, 45 + profile.MaxCommandSeconds + ProcessCleanupGraceSeconds), ReadyTimeoutSeconds = 45,
                    Resource = new Dictionary<string,string> { ["cpu"] = profile.Cpu.ToString(CultureInfo.InvariantCulture), ["memory"] = profile.MemoryMiB + "Mi" },
                    Metadata = new Dictionary<string,string> { ["agent-core-image-verification"] = owner, ["agent-core-filesystem-contract"] = SandboxProfileVerification.CurrentContractVersion.ToString(CultureInfo.InvariantCulture) },
                    Volumes = mounted.Volumes, NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] } }, deadline.Token);
                auth = null;
                phase = "mountValidation";
                var translated = await AttestMountsAsync(sdk.Id, mounted, profile.ImageReference, "agent-core-image-verification", owner, deadline.Token);
                var authority = await CheckMountedReadinessAsync(sdk, mounted, translated, deadline.Token);
                var lease = new SandboxLease(Guid.Empty, Guid.Empty, id, 1, profile, SandboxLeaseState.Provisioning, sdk.Id,
                    time.GetUtcNow(), time.GetUtcNow().AddSeconds(profile.MaxLifetimeSeconds),
                    BaselineProcesses: await ProcessIdentitiesAsync(sdk, deadline.Token), MountAuthority: authority);
                if (!imageOnly && access == HomeAccess.ReadWrite)
                {
                    var setupStarted = time.GetUtcNow();
                    foreach (var (stage, script) in new[] { ("Profile", profile.InitializationScript), ("Instance", profile.InstanceInitializationScript) })
                    {
                        if (string.IsNullOrWhiteSpace(script)) continue;
                        if (require is not null) await require(deadline.Token);
                        phase = "initialization";
                        var remaining = profile.MaxCommandSeconds - (time.GetUtcNow() - setupStarted).TotalSeconds;
                        if (remaining <= 0) throw new TimeoutException();
                        using var scriptDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        scriptDeadline.CancelAfter(TimeSpan.FromSeconds(remaining));
                        var initialized = await GuardCommandStorageAsync(lease, token => sdk.Commands.RunAsync(SandboxCommandPermissions.Shell(script),
                            CommandOptions(authority, "/working", Math.Max(1, (int)Math.Ceiling(remaining))),
                            new ExecutionHandlers { SkipAccumulation = true, OnStdout = m => { stdout.Append(m.Text); return Task.CompletedTask; },
                                OnStderr = m => { stderr.Append(m.Text); return Task.CompletedTask; } }, token), scriptDeadline.Token, mounted);
                        if (initialized.ExitCode != 0) throw new VerificationInitializationFailure(stage, initialized.ExitCode);
                    }
                    await ConfirmQuiescentAsync(sdk, lease, deadline.Token);
                }
                CanonicalSandboxMountResolver.CheckUsage(mounted, profile.StorageBytes, deadline.Token);
                phase = "cleanup";
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await sdk.KillAsync(cleanup.Token); safeToPurge = true;
                await sdk.DisposeAsync(); sdk = null;
            }
            if (require is not null) await require(deadline.Token);
            var final = await Images.InspectAsync(profile, deadline.Token, requireVerification: false);
            if (final.Status != "SourceAuthorized" || final.CredentialRevision != source.CredentialRevision || final.SecretRevision != source.SecretRevision)
                throw AgentCoreErrors.Forbidden("Image authority changed during verification; verify the current saved source again.");
            result = Observation(true, "Disposable direct mounts passed RW/RO/disabled home, trusted daemon provenance, non-root identity, Core/container bytes, HOME, links, modes, Git and cleanup. Storage enforcement is sampled."
                + (imageOnly ? " Initialization was not executed." : " Profile and Instance initialization passed."), exit: 0);
        }
        catch (OpenSandbox.Core.SandboxApiException error) when (error.Error?.Code == "MountIncompatible")
        {
            result = Observation(false, "Automatic preparation could not establish safe filesystem access during mount preparation. Check the trusted deployment mapping and reverify; an identical reset will not help.", "MountIncompatible");
        }
        catch (VerificationInitializationFailure error)
        {
            result = Observation(false, $"{error.Stage} initialization failed; inspect bounded output and correct the script.", "initialization", error.ExitCode);
        }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { result = Observation(false, "Verification authority changed. Read current configuration before retrying.", "authority_changed"); }
        catch (OperationCanceledException) { result = Observation(false, "Verification canceled or timed out during " + phase + ".", "canceledOrTimeout"); }
        catch (AgentCoreException error) { result = Observation(false, error.Message, error.Code); }
        catch { result = Observation(false, "Verification failed during " + phase + ". Check private server readiness, daemon backing-path mapping and compatible Unix identity. Provider details are withheld.", phase); }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                if (sdk is not null) { await sdk.KillAsync(cleanup.Token); safeToPurge = true; }
                else if (!safeToPurge)
                {
                    await using var manager = SandboxManager.Create(new SandboxManagerOptions { ConnectionConfig = connection });
                    var candidates = await manager.ListSandboxInfosAsync(new SandboxFilter { Metadata = new Dictionary<string,string> { ["agent-core-image-verification"] = owner, ["agent-core-filesystem-contract"] = SandboxProfileVerification.CurrentContractVersion.ToString(CultureInfo.InvariantCulture) }, PageSize = 100 }, cleanup.Token);
                    foreach (var candidate in candidates.Items)
                    {
                        if (profile.TenantId is not null && !VerificationMetadata(candidate.Metadata, owner)) throw new AccessDeniedException("verification_unavailable");
                        await manager.KillSandboxAsync(candidate.Id, cleanup.Token);
                    }
                    safeToPurge = candidates.Items.Count < 100;
                }
                if (safeToPurge && mounted?.VerificationRoot is { } path) CanonicalWorkspaceFilesystem.PurgeOwner(path, cleanup.Token);
                if (!safeToPurge) result = Observation(false, "Disposable verification cleanup remains pending. Inspect exact verification ownership before retrying.", "cleanup");
            }
            catch { result = Observation(false, "Verification process cleanup was not confirmed. Its private disposable files are retained for safe inspection.", "cleanup"); }
            finally { if (sdk is not null) await sdk.DisposeAsync(); }
        }
        return result;
    }
}
