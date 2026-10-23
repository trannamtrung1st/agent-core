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
    private async ValueTask CheckMountedAuthorityAsync(SandboxLease lease, CancellationToken ct)
    {
        if (lease.TenantId is not null)
        {
            RequireTenantProviderScope(lease);
            var ownedExecutionId = lease.ActiveExecutionId ?? lease.LastExecutionId ?? throw new AgentCore.Application.Security.AccessDeniedException("sandbox_approval_context_required");
            var facts = await TenantRuntime.ReadTenantSandboxExecutionFactsAsync(lease.TenantId.Value, lease.SessionId, ownedExecutionId, ct)
                ?? throw new AgentCore.Application.Security.AccessDeniedException("resource_unavailable");
            var ownedRun = await TenantRuntimeRun(lease.TenantId.Value, facts.Execution.AgentRunId, ct)
                ?? throw new AgentCore.Application.Security.AccessDeniedException("run_unavailable");
            if (lease.State == SandboxLeaseState.Ready || facts.Execution.Background && ownedRun.SideEffect.Disposition == AgentRunSideEffectDisposition.Succeeded)
                await TenantGeneration.RequireAsync(lease, facts.Execution, ct);
            else
            {
                var authority = await TenantExecution.RequireAsync(new(ownedRun.Owner, lease.SessionId, ownedRun.AgentRunId,
                    ownedRun.Claim?.Generation ?? Guid.Empty), ownedExecutionId, ct);
                if (authority.Lease.Generation != lease.Generation || authority.Lease.MountAuthority != lease.MountAuthority)
                    throw new AgentCore.Application.Security.SecurityConflictException("sandbox_operation_witness_changed");
            }
            _ = ResolveMounts(lease); return;
        }
        throw new AgentCore.Application.Security.AccessDeniedException("tenant_scope_required");
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
