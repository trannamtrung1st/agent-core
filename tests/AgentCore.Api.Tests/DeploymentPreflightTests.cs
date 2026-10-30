using AgentCore.Infrastructure.Configuration;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Api.Tests;

public sealed class DeploymentPreflightTests
{
    [Theory]
    [InlineData("Security:Http:AllowInsecureLocalHttp", "true", "local_http_exception")]
    [InlineData("Hosting:TrustPublishedPortGateway", "true", "gateway_trust")]
    [InlineData("Observability:LogConversationContent", "true", "private_content_logging")]
    [InlineData("Security:EphemeralBootstrap:Password", "private-canary", "ephemeral_identity")]
    [InlineData("OpenSandbox:Enabled", "true", "unqualified_native_effects")]
    [InlineData("AllowedHosts", "*", "exact_hosts_required")]
    [InlineData("urls", "http://0.0.0.0:8080", "https_listener_required")]
    [InlineData("Kestrel:Certificates:Default:Path", "private-canary", "usable_certificate_required")]
    [InlineData("urls", "https://+:notaport", "https_listener_required")]
    [InlineData("urls", "https://user:private-canary@localhost:8443", "https_listener_required")]
    [InlineData("Security:Http:AllowInsecureLocalHttp", "private-canary", "invalid_configuration")]
    public void Unsafe_deployment_refuses_before_storage_without_values(string key, string value, string reason)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AllowedHosts"] = "app.example.test", ["urls"] = "https://+:8443", [key] = value }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => ConfigurationCutover.ValidateDeployment(config, false));
        Assert.Contains(reason, error.Message);
        Assert.DoesNotContain("private-canary", error.ToString());
        // Native loopback development retains its existing explicit exceptions.
        ConfigurationCutover.ValidateDeployment(config, true);
    }

    [Fact]
    public void Maintenance_still_requires_durable_storage_and_exact_host_policy()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AllowedHosts"] = "app.example.test", ["Persistence:Provider"] = "InMemory" }).Build();
        Assert.Contains("durable_sqlite_required", Assert.Throws<InvalidOperationException>(
            () => ConfigurationCutover.ValidateDeployment(config, false, true)).Message);
    }

    [Fact]
    public void Ownership_is_exclusive_and_release_allows_reopen_without_deleting_lock_inode()
    {
        var root = Path.Combine(Path.GetTempPath(), "ph-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        root = new DirectoryInfo(root).ResolveLinkTarget(true)?.FullName ?? root;
        // macOS's temporary directory may contain a symlinked /var ancestor.
        if (OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
        try
        {
            var db = Path.Combine(root, "app.db");
            using (SqliteInstallationLease.Acquire(db))
                Assert.Throws<InvalidOperationException>(() => SqliteInstallationLease.Acquire(db));
            using (SqliteInstallationLease.Acquire(db)) Assert.True(File.Exists(db + ".owner.lock"));
            Assert.False(File.Exists(db));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Installed_roots_are_never_replaced_with_empty_directories_or_a_fresh_database()
    {
        var root = Path.Combine(Path.GetTempPath(), "ph-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var values = new Dictionary<string, string?> { ["AllowedHosts"] = "app.example.test", ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = "Data Source=" + Path.Combine(root, "app.db") };
            foreach (var name in new[] { "WorkspaceRoot", "AttachmentRoot", "ArtifactRoot", "DefinitionResourceRoot", "CredentialProtectionKeyRoot" })
                values["Persistence:" + name] = Path.Combine(root, name);
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            File.WriteAllText(Path.Combine(root, "app.db"), "existing database witness");
            Assert.Contains("installed_root_missing", Assert.Throws<InvalidOperationException>(
                () => ConfigurationCutover.ValidateDeployment(config, false, true)).Message);
            Assert.False(Directory.Exists(Path.Combine(root, "WorkspaceRoot")));
            File.Delete(Path.Combine(root, "app.db"));
            Directory.CreateDirectory(Path.Combine(root, "WorkspaceRoot"));
            File.WriteAllText(Path.Combine(root, "WorkspaceRoot", "retained"), "retained state");
            Assert.Contains("existing_state_requires_database", Assert.Throws<InvalidOperationException>(
                () => ConfigurationCutover.ValidateDeployment(config, false, true)).Message);
            Assert.False(File.Exists(Path.Combine(root, "app.db")));
        }
        finally { Directory.Delete(root, true); }
    }
}
