using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private SandboxMountFiles Mounts => new(options);
    private static async Task<string> InternalCommandAsync(SdkSandbox sdk, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var output = new BoundedText(1024 * 1024);
        var execution = await sdk.Commands.RunAsync(argv, new RunCommandOptions { WorkingDirectory = "/", TimeoutSeconds = 30 },
            new ExecutionHandlers { SkipAccumulation = true, OnStdout = m => { output.Append(m.Text); return Task.CompletedTask; } }, ct);
        if (execution.ExitCode != 0 || output.Truncated) throw AgentCoreErrors.Validation("Sandbox filesystem/process validation failed. Reset before another command.");
        return output.Text;
    }
    private Task HydrateAsync(SandboxLease lease, WorkspaceTransfer transfer, CancellationToken ct) => Mounts.HydrateAsync(lease.Generation, transfer, ct);
    private Task<WorkspaceTransfer> CollectAsync(SandboxLease lease, CancellationToken ct) => Mounts.CollectAsync(lease.Generation, lease.Profile, ct);

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
    private static async Task ConfirmQuiescentAsync(SdkSandbox sdk, SandboxLease lease, CancellationToken ct)
    {
        var current = await ProcessIdentitiesAsync(sdk, ct);
        if (current.Except(lease.BaselineProcesses ?? [], StringComparer.Ordinal).Any())
            throw new AgentCoreException("SandboxProcessesActive", "Untracked child processes remain. No scratch was committed; inspect the retained execution and termination evidence.", 409);
    }
}
