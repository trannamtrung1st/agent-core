using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private CanonicalSandboxMountResolver MountResolver => new(options);
    private CanonicalSandboxMountResolver.Mounts ResolveMounts(SandboxLease lease)
    {
        var authority = lease.MountAuthority ?? throw CanonicalSandboxMountResolver.Incompatible("Mount authority is absent; reset and reverify this environment.");
        // Admission validates aliases; the usage sampler checks them again in
        // its single tree walk. Identity checks themselves need only root stat.
        var mounted = MountResolver.Resolve(lease.AgentInstanceId, lease.SessionId, authority.HomeAccess, validateHardLinks:false);
        if (mounted.Authority != authority with { TranslatedOwnership = false }) throw CanonicalSandboxMountResolver.Incompatible("Canonical root identity or execution identity changed. Retire the old generation before dispatch.");
        return mounted;
    }
    private async ValueTask<HomeAccess> EffectiveHomeAsync(SandboxAccess access, CancellationToken ct) =>
        (await StateIsolationAccess.ResolveAsync(access.Definition, access.AgentInstanceId, instances, definitions, ct)).HomeAccess;

    private async ValueTask CheckMountedAuthorityAsync(SandboxLease lease, CancellationToken ct)
    {
        var session = await sessions.LoadMetadataAsync(lease.SessionId, ct);
        if (session?.AgentInstanceId != lease.AgentInstanceId || session.DurablyDeletedAt is not null || session.ArchivedAt is not null
            || session.Status is SessionStatus.Ended or SessionStatus.Ending)
            throw AgentCoreErrors.Forbidden("Sandbox owner is no longer active.");
        var run = session.ProfileId is { } profile && lease.AdmissionRunId is { } id ? await runs.GetAsync(new(lease.AgentInstanceId, profile), id, ct) : null;
        if (lease.ActiveExecutionId is { } executionId)
        {
            if (session.LifecycleStatus != SessionLifecycleStatus.Active) throw AgentCoreErrors.Forbidden("Active process Session was deactivated.");
            var execution = await store.GetExecutionAsync(lease.SessionId, executionId, ct);
            if (run is null || run.CancellationRequested || run.Status is AgentRunStatus.Cancelled or AgentRunStatus.Failed
                || execution is null || !execution.Background && (run.Status != AgentRunStatus.Running
                    || run.Claim?.Generation != execution.ClaimGeneration || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow()))
                throw AgentCoreErrors.Forbidden("The active process no longer has its admitted Run authority.");
        }
        var admitted = run?.Admission.Configuration?.Definition ?? session.Definition;
        var policy = await StateIsolationAccess.ResolveAsync(admitted, lease.AgentInstanceId, instances, definitions, ct);
        if (lease.MountAuthority?.HomeAccess != policy.HomeAccess)
            throw AgentCoreErrors.Forbidden("Home policy changed; the previous mounted generation must be terminated.");
        var instance = instances is null ? null : await instances.FindAsync(lease.AgentInstanceId, ct);
        if (instance?.Lifecycle != AgentInstanceLifecycle.Active)
            throw AgentCoreErrors.Forbidden("Sandbox Instance is archived or deleted.");
        if (instance.SettingsOverrides?.SandboxAllowedProfileIds is { } narrowed && !narrowed.Value.Contains(lease.Profile.Id, StringComparer.Ordinal))
            throw AgentCoreErrors.Forbidden("Sandbox Profile was revoked from the Instance subset.");
        if (!options.Enabled || (await store.GetProfileAsync(lease.Profile.Id, ct: ct))?.Enabled != true)
            throw AgentCoreErrors.Forbidden("Sandbox Profile is revoked.");
        if (run is not null) await ValidateLiveProfileAuthorityAsync(run, lease.Profile, ct);
        var source = await Images.InspectAsync(lease.Profile, ct, requireVerification: false);
        if (source.Status != "SourceAuthorized" || source.CredentialRevision != lease.Profile.RegistryCredentialRevision
            || source.SecretRevision != lease.Profile.RegistrySecretRevision)
            throw AgentCoreErrors.Forbidden("Sandbox source authority changed.");
        _ = ResolveMounts(lease);
    }

    private RunCommandOptions CommandOptions(SandboxLease lease, string cwd, int timeout, bool background = false)
    {
        var authority = lease.MountAuthority ?? throw CanonicalSandboxMountResolver.Incompatible("Missing execution identity.");
        var result = CommandOptions(authority, cwd, timeout, background);
        result.Envs = GitEnvironment(authority, GitRepositories(lease));
        return result;
    }
    private static RunCommandOptions CommandOptions(SandboxMountAuthority authority, string cwd, int timeout, bool background = false)
    {
        return new() { WorkingDirectory = cwd, TimeoutSeconds = timeout, Background = background,
            Uid = authority.Uid, Gid = authority.Gid,
            Envs = GitEnvironment(authority) };
    }

    private void ValidateCwd(SandboxLease lease, string cwd)
    {
        try
        {
            var mounted = ResolveMounts(lease);
            if (cwd == "/working" || cwd.StartsWith("/working/", StringComparison.Ordinal)) mounted.Working.RequireDirectory(cwd);
            else if (mounted.Home is not null && (cwd == "/home" || cwd.StartsWith("/home/", StringComparison.Ordinal))) mounted.Home.RequireDirectory(cwd);
            else throw AgentCoreErrors.Forbidden("Cwd is outside mounted authorized roots.");
        }
        catch (Exception error) when (error is AgentCoreException or IOException or UnauthorizedAccessException)
        { throw new AgentCoreException("cwd_unavailable", "The requested cwd is missing, inaccessible or outside mounted policy. Set workspace.cwd to an accessible directory.", 409); }
    }
    private static async Task<string> InternalCommandAsync(SdkSandbox sdk, IReadOnlyList<string> argv, CancellationToken ct, int timeoutSeconds = 30)
    {
        var output = new BoundedText(1024 * 1024);
        var execution = await sdk.Commands.RunAsync(argv, new RunCommandOptions { WorkingDirectory = "/", TimeoutSeconds = timeoutSeconds },
            new ExecutionHandlers { SkipAccumulation = true, OnStdout = m => { output.Append(m.Text); return Task.CompletedTask; } }, ct);
        if (execution.ExitCode != 0 || output.Truncated) throw AgentCoreErrors.Validation("Sandbox filesystem/process validation failed. Reset before another command.");
        return output.Text;
    }
    // POSIX shell builtins only: no Python, ps, cat, jq or image package installation.
    private const string ProcessScript = """
        ignored=' '
        p=$$
        while [ "$p" -gt 0 ]; do
            ignored="$ignored$p "
            IFS= read -r line < "/proc/$p/stat" || break
            rest=${line##*) }
            set -- $rest
            [ "$#" -ge 2 ] || exit 1
            p=$2
        done
        for file in /proc/[0-9]*/stat; do
            pid=${file#/proc/}; pid=${pid%/stat}
            case "$ignored" in *" $pid "*) continue ;; esac
            IFS= read -r line < "$file" 2>/dev/null || continue
            rest=${line##*) }
            set -- $rest
            [ "$#" -ge 20 ] || exit 1
            [ "$1" != Z ] || continue
            shift 19
            printf '%s:%s\n' "$pid" "$1"
        done
        """;
    private static async Task<string[]> ProcessIdentitiesAsync(SdkSandbox sdk, CancellationToken ct)
    {
        var output = await InternalCommandAsync(sdk, ["/bin/sh", "-c", ProcessScript], ct);
        var identities = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (identities.Any(value => !System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9]+:[0-9]+$")))
            throw AgentCoreErrors.Validation("Sandbox process identities could not be confirmed.");
        return identities.Order(StringComparer.Ordinal).ToArray();
    }
}
