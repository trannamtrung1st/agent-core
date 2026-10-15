using System.Text.RegularExpressions;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    // Probe only the observed PID/start-time pair. No process is exempted from the
    // final identity scan. Optional utilities come from fixed image paths, not PATH.
    private const string BuildServerProbe = """
        IFS= read -r line < "/proc/$1/stat" 2>/dev/null || exit 0
        rest=${line##*) }; set -- "$1" "$2" $rest
        pid=$1; expected=$2; shift 2
        [ "$#" -ge 20 ] || exit 0
        shift 19
        [ "$1" = "$expected" ] || exit 0
        reader=/usr/bin/readlink; [ -x "$reader" ] || reader=/bin/readlink
        converter=/usr/bin/tr; [ -x "$converter" ] || converter=/bin/tr
        [ -x "$reader" ] && [ -x "$converter" ] || exit 0
        "$reader" "/proc/$pid/exe" || exit 0
        "$converter" '\000' '\n' < "/proc/$pid/cmdline" 2>/dev/null
        """;

    internal static (string Host, string Cli)? RecognizeBuildServer(string observation)
    {
        var lines = observation.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3) return null;
        var executable = lines[0];
        var managed = executable is "/usr/share/dotnet/dotnet" or "/usr/lib/dotnet/dotnet" or "/usr/lib64/dotnet/dotnet";
        var server = managed ? lines[2] : executable;
        var match = Regex.Match(server, "^(?<root>/usr/(?:share|lib|lib64)/dotnet)/sdk/(?<version>[0-9][A-Za-z0-9.+-]*)/(?<server>Roslyn/bincore/VBCSCompiler(?:\\.dll)?|MSBuild\\.dll)$");
        if (!match.Success) return null;
        var host = match.Groups["root"].Value + "/dotnet";
        if (managed && executable != host) return null;
        var kind = match.Groups["server"].Value;
        if (kind == "MSBuild.dll" ? !managed || !lines.Skip(3).Any(a => a is "/nodemode:1" or "/nodemode:2" or "/nodemode:8" or "-nodemode:1" or "-nodemode:2" or "-nodemode:8")
            : !lines.Skip(managed ? 3 : 2).Any(a => a.StartsWith("-pipename:", StringComparison.Ordinal))) return null;
        if (!managed && kind != "Roslyn/bincore/VBCSCompiler") return null;
        return (host, match.Groups["root"].Value + "/sdk/" + match.Groups["version"].Value + "/dotnet.dll");
    }

    private async Task ConfirmQuiescentAsync(SdkSandbox sdk, SandboxLease lease, CancellationToken ct)
    {
        static AgentCoreException Blocked() => new("SandboxProcessesActive", "Untracked child processes remain or build-server shutdown was not confirmed. No scratch was committed; inspect the retained execution and termination evidence.", 409);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            async Task<string[]> Remaining() => (await ProcessIdentitiesAsync(sdk, budget.Token)).Except(lease.BaselineProcesses ?? [], StringComparer.Ordinal).ToArray();
            var remaining = await Remaining();
            if (remaining.Length == 0) return;
            // Allow short-lived helpers to exit, without waiting out persistent servers.
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, budget.Token);
            remaining = await Remaining();
            if (remaining.Length == 0) return;
            if (remaining.Length > 16) throw Blocked();
            var servers = new HashSet<(string Host, string Cli)>();
            foreach (var identity in remaining)
            {
                var parts = identity.Split(':');
                var observation = await InternalCommandAsync(sdk, ["/bin/sh", "-c", BuildServerProbe, "core-build-server-probe", parts[0], parts[1]], budget.Token, 3);
                var server = RecognizeBuildServer(observation);
                if (server is null)
                {
                    if (!(await Remaining()).Contains(identity, StringComparer.Ordinal)) continue;
                    throw Blocked(); // Never interrupt an arbitrary app/child.
                }
                servers.Add(server.Value);
            }
            foreach (var server in servers)
                // Pin the observed SDK CLI, independent of PATH/global.json. This
                // requests its supported IPC shutdown; it never reruns user commands.
                await InternalCommandAsync(sdk, [server.Host, server.Cli, "build-server", "shutdown", "--msbuild", "--vbcscompiler"], budget.Token, 5);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if ((await Remaining()).Length == 0) return;
                await Task.Delay(TimeSpan.FromMilliseconds(250), time, budget.Token);
            }
            throw Blocked();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Blocked(); }
        catch (AgentCoreException error) when (error.Code == "ValidationError") { throw Blocked(); }
    }
}
