using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private static readonly HttpClient CompatibilityClient = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    // Only the authenticated trusted server can attest daemon mount provenance.
    // OS names or container-reported metadata alone never authorize an exception.
    private async Task<bool> AttestMountsAsync(string providerId, CanonicalSandboxMountResolver.Mounts mounts,
        string image, string ownerLabel, string ownerValue, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Origin(), "/agent-core/mounts/" + Uri.EscapeDataString(providerId)));
        request.Headers.Add("OPEN-SANDBOX-API-KEY", options.ApiKey);
        request.Content = JsonContent.Create(new {
            ownerLabel, ownerValue, image, uid = mounts.Authority.Uid, gid = mounts.Authority.Gid,
            working = mounts.Volumes.Single(v => v.MountPath == "/working").Host!.Path,
            home = mounts.Volumes.SingleOrDefault(v => v.MountPath == "/home")?.Host?.Path,
            homeReadOnly = mounts.Authority.HomeAccess == HomeAccess.ReadOnly,
            workingReadOnly = mounts.Authority.WorkingReadOnly,
            privateHome = mounts.Volumes.SingleOrDefault(v => v.MountPath == "/private")?.Host?.Path,
            privateReadOnly = mounts.Authority.PrivateAccess == HomeAccess.ReadOnly
        });
        try
        {
            using var response = await CompatibilityClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw CanonicalSandboxMountResolver.Incompatible("Trusted server compatibility validation is unavailable or refused the mounts. Update the trusted server and verify the selected profile.");
            // Fixed, small trusted response. Never surface provider bodies/paths.
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 4096) throw new JsonException();
            using var data = JsonDocument.Parse(bytes);
            var root = data.RootElement;
            if (root.GetProperty("contractVersion").GetInt32() != SandboxProfileVerification.CurrentContractVersion
                || root.GetProperty("runtimeUser").GetString() != $"{mounts.Authority.Uid}:{mounts.Authority.Gid}") throw new JsonException();
            var topology = root.GetProperty("topology").GetString();
            logger?.LogInformation("Sandbox mount provenance verified. Topology={Topology} RuntimeUid={RuntimeUid} RuntimeGid={RuntimeGid} ImageId={ImageId} ImageDefaultUser={ImageDefaultUser} WorkingIdentity={WorkingIdentity} HomeIdentity={HomeIdentity}",
                topology, mounts.Authority.Uid, mounts.Authority.Gid, root.GetProperty("imageId").GetString(), root.GetProperty("imageUser").GetString(), mounts.Authority.WorkingIdentity, mounts.Authority.HomeIdentity);
            return topology switch {
                "linux" => false,
                "docker-desktop" => true,
                _ => throw new JsonException()
            };
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw CanonicalSandboxMountResolver.Incompatible("Sandbox filesystem compatibility could not be established by the trusted server. Check server readiness and reverify the selected profile."); }
    }

    internal static Dictionary<string, string> GitEnvironment(SandboxMountAuthority authority, IEnumerable<string>? repositories = null)
    {
        var env = new Dictionary<string, string> { ["HOME"] = authority.HomeAccess == HomeAccess.Disabled ? "/working" : "/home" };
        if (!authority.TranslatedOwnership) return env;
        // Command-local exact directories, never persisted to shared HOME or a
        // global Git configuration. No wildcard or sibling Session can be trusted.
        var roots = new[] { "/working" }.Concat(authority.HomeAccess == HomeAccess.Disabled ? [] : new[] { "/home" })
            .Concat(authority.PrivateIdentity is null ? [] : new[] { "/private" });
        var trusted = roots.Concat(repositories ?? []).Distinct(StringComparer.Ordinal).ToArray();
        env["GIT_CONFIG_COUNT"] = (trusted.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Clear inherited safe.directory values before adding exact validated roots.
        env["GIT_CONFIG_KEY_0"] = "safe.directory"; env["GIT_CONFIG_VALUE_0"] = "";
        for (var i = 0; i < trusted.Length; i++)
        {
            var path = trusted[i];
            if (!roots.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal))
                || path.Split('/').Any(part => part is "." or "..") || path.Contains('\n') || path.Contains('*'))
                throw CanonicalSandboxMountResolver.Incompatible("Git repository is outside validated mount authority.");
            env[$"GIT_CONFIG_KEY_{i + 1}"] = "safe.directory"; env[$"GIT_CONFIG_VALUE_{i + 1}"] = path;
        }
        return env;
    }

    private IEnumerable<string> GitRepositories(SandboxLease lease)
    {
        if (lease.MountAuthority?.TranslatedOwnership != true) return [];
        var mounted = ResolveMounts(lease);
        var result = new List<string>(); var visited = 0;
        void Walk(CanonicalWorkspaceFilesystem files, string path, int depth)
        {
            // Exhausting discovery never blocks ordinary commands or grants a
            // broader exception. Undiscovered repositories keep Git's protection.
            if (depth > 128 || ++visited > 8192) return;
            string? after = null;
            do
            {
                var page = files.List(path, after, 256);
                foreach (var entry in page.Entries)
                {
                    if (++visited > 8192) return;
                    if (entry.Path == path + "/.git")
                    {
                        var directory = files.Stat(path);
                        if (directory.Uid != lease.MountAuthority!.Uid || directory.Gid != lease.MountAuthority.Gid)
                            throw CanonicalSandboxMountResolver.Incompatible("Git repository ownership could not be verified.");
                        result.Add(path);
                    }
                    else if (entry.Directory) Walk(files, entry.Path, depth + 1);
                }
                after = page.NextPath;
            } while (after is not null);
        }
        Walk(mounted.Working, "/working", 0);
        if (mounted.Home is not null) Walk(mounted.Home, "/home", 0);
        if (mounted.Private is not null) Walk(mounted.Private, "/private", 0);
        return result;
    }
}
