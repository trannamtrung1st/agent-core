using System.Text;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private async Task CheckMountedReadinessAsync(SdkSandbox sdk, CanonicalSandboxMountResolver.Mounts mounted, CancellationToken ct)
    {
        var suffix = ".core-probe-" + Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var working = "/working/" + suffix;
        var home = "/home/" + suffix;
        var authority = mounted.Authority;
        try
        {
            mounted.Working.Mkdir(working);
            await mounted.Working.WriteAsync(working + "/core", Encoding.UTF8.GetBytes(nonce), 256, long.MaxValue, ct);
            if (mounted.Home is not null)
            {
                mounted.Home.Mkdir(home);
                await mounted.Home.WriteAsync(home + "/core", Encoding.UTF8.GetBytes(nonce), 256, long.MaxValue, ct);
            }
            var script = $$"""
                set -eu
                stage=process_isolation
                trap 'printf "core_mount_probe=%s\n" "$stage"' EXIT
                test -d /proc/self
                test ! -e /var/run/docker.sock
                stage=execution_identity
                test "$(id -u)" = '{{authority.Uid}}'
                test "$(id -g)" = '{{authority.Gid}}'
                stage=home_environment
                test "$HOME" = '{{(authority.HomeAccess == HomeAccess.Disabled ? "/working" : "/home")}}'
                cd ~
                test "$PWD" = "$HOME"
                stage=working_bytes
                value=$(cat '{{working}}/core')
                test "$value" = '{{nonce}}'
                printf '%s' '{{nonce}}' > '{{working}}/from-container'
                stage=links_and_permissions
                ln '{{working}}/core' '{{working}}/hard'
                ln -s '{{working}}/core' '{{working}}/symbolic'
                chmod 600 '{{working}}/from-container'
                mv '{{working}}/from-container' '{{working}}/renamed'
                mkdir '{{working}}/.git'
                printf lock > '{{working}}/.git/index.lock'
                rm '{{working}}/.git/index.lock'
                """;
            if (mounted.Home is not null)
            {
                script += $"\nstage=home_bytes\ntest \"$(cat '{home}/core')\" = '{nonce}'\n";
                // Separate mount boundaries must reject cross-root link aliases,
                // including RO home. Never accept a profile where this succeeds.
                script += $"stage=cross_root_links\nif ln '{home}/core' '{working}/cross-root' 2>/dev/null; then exit 71; fi\n";
                script += "stage=home_access\n";
                script += authority.HomeAccess == HomeAccess.ReadOnly
                    ? $"if printf denied > '{home}/denied' 2>/dev/null; then exit 72; fi\n"
                    : $"printf '%s' '{nonce}' > '{home}/from-container'\n";
            }
            var result = await sdk.Commands.RunAsync(["/bin/sh", "-c", script], CommandOptions(authority, "/working", 30), null, ct);
            if (result.ExitCode != 0)
            {
                string[] stages = ["process_isolation", "execution_identity", "home_environment", "working_bytes", "links_and_permissions", "home_bytes", "cross_root_links", "home_access"];
                var stage = stages.FirstOrDefault(value => result.Logs.Stdout.Any(line => line.Text.Contains("core_mount_probe=" + value, StringComparison.Ordinal))) ?? "command_start";
                throw CanonicalSandboxMountResolver.Incompatible($"Canonical mount probe failed at {stage} (exit {result.ExitCode?.ToString() ?? "unconfirmed"}). Verify the daemon backing path and image default UID/GID matches Core's workspace owner. Capability-restricted providers cannot switch a mismatched image identity. Check HOME, links and permissions.");
            }
            async ValueTask Confirm(CanonicalWorkspaceFilesystem files, string path)
            {
                if (Encoding.UTF8.GetString(await files.ReadAsync(path, 256, ct)) != nonce)
                    throw CanonicalSandboxMountResolver.Incompatible("Core/daemon paths do not share canonical backing bytes.");
            }
            await Confirm(mounted.Working, working + "/renamed");
            await Confirm(mounted.Working, working + "/symbolic");
            await Confirm(mounted.Working, working + "/hard");
            if (mounted.Home is not null && authority.HomeAccess == HomeAccess.ReadWrite)
                await Confirm(mounted.Home, home + "/from-container");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw CanonicalSandboxMountResolver.Incompatible("Core cannot read/write the canonical probe with the declared Unix identity. Check scoped ownership and permissions."); }
        finally
        {
            try { mounted.Working.Delete(working, true, CancellationToken.None); } catch { /* Owned temporary probe can be inspected; never delete a broader path. */ }
            if (mounted.Home is not null) try { mounted.Home.Delete(home, true, CancellationToken.None); } catch { }
        }
    }
}
