using System.Text;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    internal static string MountOwnershipProbe(HomeAccess homeAccess, bool trustedDesktop = false, HomeAccess privateAccess = HomeAccess.Disabled)
    {
        string Check(string path) => trustedDesktop
            ? $"if ! (test -O {path} && test -G {path}); then test \"$(stat -c '%u:%g' {path})\" = '0:0'; printf 'core_mount_translation=1\\n'; fi\nmode=$(stat -c '%a' {path}); test $((0$mode & 0022)) -eq 0"
            : $"test -O {path}\ntest -G {path}";
        return Check("/working") + (homeAccess == HomeAccess.Disabled ? "" : "\n" + Check("/home"))
            + (privateAccess == HomeAccess.Disabled ? "" : "\n" + Check("/private"));
    }

    private async Task<SandboxMountAuthority> CheckMountedReadinessAsync(SdkSandbox sdk, CanonicalSandboxMountResolver.Mounts mounted, bool trustedDesktop, CancellationToken ct)
    {
        var suffix = ".core-probe-" + Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var authority = mounted.Authority with { TranslatedOwnership = trustedDesktop };
        List<(CanonicalWorkspaceFilesystem Files, string Root, bool ReadOnly)> roots = [(mounted.Working, "/working", authority.WorkingReadOnly)];
        if (mounted.Home is not null) roots.Add((mounted.Home, "/home", authority.HomeAccess == HomeAccess.ReadOnly));
        if (mounted.Private is not null) roots.Add((mounted.Private, "/private", authority.PrivateAccess == HomeAccess.ReadOnly));
        static string Identity(CanonicalWorkspaceFilesystem files, string root)
        { var node = files.Stat(root); return $"{node.Device:x}:{node.Inode:x}"; }
        var identities = roots.Select(r => Identity(r.Files, r.Root)).ToArray();
        try
        {
            foreach (var (files, root, _) in roots)
            {
                files.Mkdir(root + "/" + suffix);
                await files.WriteAsync(root + "/" + suffix + "/core", Encoding.UTF8.GetBytes(nonce), 256, long.MaxValue, ct);
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
                {{MountOwnershipProbe(authority.HomeAccess, trustedDesktop, authority.PrivateAccess)}}
                stage=home_environment
                test "$HOME" = '{{(authority.HomeAccess == HomeAccess.Disabled ? "/working" : "/home")}}'
                cd ~
                test "$PWD" = "$HOME"
                """;
            foreach (var (_, root, readOnly) in roots)
            {
                var path = root + "/" + suffix;
                script += $"\nstage={root[1..]}_bytes\ntest \"$(cat '{path}/core')\" = '{nonce}'\n";
                if (readOnly)
                    script += $"stage={root[1..]}_read_only\nif printf denied > '{path}/denied' 2>/dev/null; then exit 72; fi\nif mv '{path}/core' '{path}/moved' 2>/dev/null; then exit 73; fi\nif rm '{path}/core' 2>/dev/null; then exit 74; fi\nif chmod 644 '{path}/core' 2>/dev/null; then exit 75; fi\n";
                else
                    script += $$"""
                        stage={{root[1..]}}_write
                        printf '%s' '{{nonce}}' > '{{path}}/from-container'
                        chmod 600 '{{path}}/from-container'
                        ln '{{path}}/core' '{{path}}/hard'
                        ln -s '{{path}}/core' '{{path}}/symbolic'
                        mv '{{path}}/from-container' '{{path}}/renamed'
                        printf delete > '{{path}}/delete'
                        rm '{{path}}/delete'
                        stage=git
                        git -C '{{path}}' init -q
                        git -C '{{path}}' add core
                        git -C '{{path}}' -c user.name=Core -c user.email=probe@example.invalid -c core.hooksPath=/dev/null commit -qm probe
                        git -C '{{path}}' checkout -qb compatibility
                        git -C '{{path}}' status --porcelain
                        git clone -q '{{path}}' '{{path}}/clone'
                        test "$(cat '{{path}}/clone/core')" = '{{nonce}}'

                        """;
                foreach (var (_, destination, _) in roots.Where(r => r.Root != root))
                    script += $"stage=cross_root_links\nif ln '{path}/core' '{destination}/{suffix}/cross-root' 2>/dev/null; then exit 71; fi\n";
            }
            var options = CommandOptions(authority, "/working", 30);
            options.Envs = GitEnvironment(authority, roots.Where(r => !r.ReadOnly).Select(r => r.Root + "/" + suffix).ToArray());
            var result = await sdk.Commands.RunAsync(["/bin/sh", "-c", script], options, null, ct);
            if (result.ExitCode != 0) throw CanonicalSandboxMountResolver.Incompatible("Sandbox filesystem access modes, root isolation or private file semantics failed compatibility validation.");
            for (var i = 0; i < roots.Count; i++)
            {
                var (files, root, readOnly) = roots[i];
                if (Identity(files, root) != identities[i]) throw CanonicalSandboxMountResolver.Incompatible("Canonical workspace root changed during compatibility validation.");
                if (readOnly) continue;
                foreach (var child in new[] { "renamed", "hard", "symbolic" })
                    if (Encoding.UTF8.GetString(await files.ReadAsync(root + "/" + suffix + "/" + child, 256, ct)) != nonce)
                        throw CanonicalSandboxMountResolver.Incompatible("Core and daemon paths do not share canonical backing bytes.");
                if (files.Stat(root + "/" + suffix + "/renamed").Mode != 0x180)
                    throw CanonicalSandboxMountResolver.Incompatible("Mounted file permissions do not preserve private creation.");
            }
            return authority with { TranslatedOwnership = trustedDesktop && result.Logs.Stdout.Any(line => line.Text.Contains("core_mount_translation=1", StringComparison.Ordinal)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw CanonicalSandboxMountResolver.Incompatible("Automatic preparation could not establish safe canonical filesystem access; files were retained."); }
        finally
        {
            foreach (var (files, root, _) in roots)
                try { files.Delete(root + "/" + suffix, true, CancellationToken.None); } catch { }
        }
    }
}
