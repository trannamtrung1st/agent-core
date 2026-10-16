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
    // Native Linux metadata remains mandatory. Desktop translation is accepted
    // only after the authenticated server proves the exact generation/image/binds.
    internal static string MountOwnershipProbe(HomeAccess homeAccess, bool trustedDesktop = false)
    {
        string Check(string path) => trustedDesktop
            ? $"if ! (test -O {path} && test -G {path}); then test \"$(stat -c '%u:%g' {path})\" = '0:0'; printf 'core_mount_translation=1\\n'; fi\nmode=$(stat -c '%a' {path}); test $((0$mode & 0022)) -eq 0"
            : $"test -O {path}\ntest -G {path}";
        return Check("/working") + (homeAccess == HomeAccess.Disabled ? "" : "\n" + Check("/home"));
    }

    private async Task<SandboxMountAuthority> CheckMountedReadinessAsync(SdkSandbox sdk, CanonicalSandboxMountResolver.Mounts mounted, bool trustedDesktop, CancellationToken ct)
    {
        var suffix = ".core-probe-" + Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var working = "/working/" + suffix;
        var home = "/home/" + suffix;
        var authority = mounted.Authority with { TranslatedOwnership = trustedDesktop };
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
                stage=mount_ownership
                {{MountOwnershipProbe(authority.HomeAccess, trustedDesktop)}}
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
                stage=working_delete
                printf delete > '{{working}}/delete'
                rm '{{working}}/delete'
                test ! -e '{{working}}/delete'
                stage=git
                export GIT_CONFIG_NOSYSTEM=1 GIT_CONFIG_GLOBAL=/dev/null
                git -C '{{working}}' init -q
                git -C '{{working}}' add core
                git -C '{{working}}' -c user.name=Core -c user.email=probe@example.invalid -c core.hooksPath=/dev/null commit -qm probe
                git -C '{{working}}' checkout -qb compatibility
                git -C '{{working}}' status --porcelain
                git clone -q '{{working}}' '{{working}}/clone'
                test "$(cat '{{working}}/clone/core')" = '{{nonce}}'
                """;
            if (mounted.Home is not null)
            {
                script += $"\nstage=home_bytes\ntest \"$(cat '{home}/core')\" = '{nonce}'\n";
                // Separate mount boundaries must reject cross-root link aliases,
                // including RO home. Never accept a profile where this succeeds.
                script += $"stage=cross_root_links\nif ln '{home}/core' '{working}/cross-root' 2>/dev/null; then exit 71; fi\n";
                script += "stage=home_access\n";
                script += authority.HomeAccess == HomeAccess.ReadOnly
                    ? $"if printf denied > '{home}/denied' 2>/dev/null; then exit 72; fi\nif mv '{home}/core' '{home}/moved' 2>/dev/null; then exit 73; fi\nif rm '{home}/core' 2>/dev/null; then exit 74; fi\nif chmod 644 '{home}/core' 2>/dev/null; then exit 75; fi\n"
                    : $"printf '%s' '{nonce}' > '{home}/from-container'\nchmod 600 '{home}/from-container'\nln '{home}/core' '{home}/hard'\nln -s '{home}/core' '{home}/symbolic'\nmv '{home}/from-container' '{home}/renamed'\nprintf delete > '{home}/delete'\nrm '{home}/delete'\ngit -C '{home}' init -q\ngit -C '{home}' add core\ngit -C '{home}' -c user.name=Core -c user.email=probe@example.invalid -c core.hooksPath=/dev/null commit -qm probe\ngit -C '{home}' status --porcelain\n";
            }
            var probeOptions = CommandOptions(authority, "/working", 30);
            probeOptions.Envs = GitEnvironment(authority, authority.HomeAccess == HomeAccess.ReadWrite ? [working, home] : [working]);
            var result = await sdk.Commands.RunAsync(["/bin/sh", "-c", script], probeOptions, null, ct);
            if (result.ExitCode != 0)
            {
                string[] stages = ["process_isolation", "execution_identity", "mount_ownership", "home_environment", "working_bytes", "links_and_permissions", "working_delete", "git", "home_bytes", "cross_root_links", "home_access"];
                var stage = stages.FirstOrDefault(value => result.Logs.Stdout.Any(line => line.Text.Contains("core_mount_probe=" + value, StringComparison.Ordinal))) ?? "command_start";
                throw CanonicalSandboxMountResolver.Incompatible($"Sandbox filesystem is incompatible with this Docker environment. Automatic preparation could not establish safe access (stage: {stage}; exit: {result.ExitCode?.ToString() ?? "unconfirmed"}). Reverify the profile after correcting the deployment; an identical reset cannot repair this failure.");
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
            {
                await Confirm(mounted.Home, home + "/renamed");
                await Confirm(mounted.Home, home + "/hard");
                await Confirm(mounted.Home, home + "/symbolic");
            }
            // Detect path replacement while probing without authorizing a new root.
            static string Identity(CanonicalWorkspaceFilesystem files, string path)
            { var node = files.Stat(path); return $"{node.Device:x}:{node.Inode:x}"; }
            if (Identity(mounted.Working, "/working") != authority.WorkingIdentity
                || mounted.Home is not null && Identity(mounted.Home, "/home") != authority.HomeIdentity)
                throw CanonicalSandboxMountResolver.Incompatible("Canonical workspace root changed during compatibility validation.");
            if (mounted.Working.Stat(working + "/renamed").Mode != 0x180
                || mounted.Home is not null && authority.HomeAccess == HomeAccess.ReadWrite && mounted.Home.Stat(home + "/renamed").Mode != 0x180)
                throw CanonicalSandboxMountResolver.Incompatible("Mounted file permissions do not preserve private creation.");
            return authority with { TranslatedOwnership = trustedDesktop && result.Logs.Stdout.Any(line => line.Text.Contains("core_mount_translation=1", StringComparison.Ordinal)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw CanonicalSandboxMountResolver.Incompatible("Automatic preparation could not establish safe canonical filesystem access; files were retained."); }
        finally
        {
            try { mounted.Working.Delete(working, true, CancellationToken.None); } catch { /* Owned temporary probe can be inspected; never delete a broader path. */ }
            if (mounted.Home is not null) try { mounted.Home.Delete(home, true, CancellationToken.None); } catch { }
        }
    }
}
