using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using AgentCore.Infrastructure.Persistence;
using System.Security.Cryptography.X509Certificates;

namespace AgentCore.Infrastructure.Configuration;

/// <summary>Reject retired host inputs before resolving services or opening persisted state.</summary>
public static class ConfigurationCutover
{
    private static readonly (string Prefix, string Replacement)[] Retired =
    [
        ("GoogleEmailOAuth", "GoogleOAuth (reconnect accounts whose client binding cannot be proven)"),
        ("GoogleDriveOAuth", "GoogleOAuth (reconnect accounts whose client binding cannot be proven)"),
        ("GOOGLE_EMAIL_OAUTH_", "GoogleOAuth"),
        ("GOOGLE_DRIVE_OAUTH_", "GoogleOAuth"),
        ("Providers:SpeechRecognizers", "Providers:Speech:Recognition"),
        ("Providers:SpeechSynthesizers", "Providers:Speech:Synthesis"),
        ("FilePreview:Limits:CsvBytes", "FilePreview:Limits:DelimitedBytes"),
        ("GOOGLE_OAUTH_", "GoogleOAuth:* (translate orchestration inputs with scripts/dev-env.py or scripts/compose.py)"),
        ("AGENTCORE_DRIVE_ENABLED", "GoogleDrive:Enabled (translate at the launcher/Compose boundary)")
    ];

    public static void Validate(IConfiguration configuration)
    {
        foreach (var entry in configuration.AsEnumerable())
        {
            if (entry.Value is null) continue;
            foreach (var (prefix, replacement) in Retired)
            {
                if (!entry.Key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    && !entry.Key.StartsWith(prefix.EndsWith('_') ? prefix : prefix + ":", StringComparison.OrdinalIgnoreCase)) continue;
                throw new InvalidOperationException($"Retired or untranslated configuration key {entry.Key}. Use {replacement}. Values are redacted.");
            }
        }
    }

    // This is the deliberately narrow, directly terminated, single-host deployment posture.
    // Run before opening a database, reconciling state or starting any worker.
    public static void ValidateDeployment(IConfiguration configuration, bool development, bool maintenance = false)
    {
        if (development) return;
        try { ValidateDeploymentCore(configuration, maintenance); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException
            || ex is InvalidOperationException && !ex.Message.StartsWith("Deployment preflight refused:", StringComparison.Ordinal))
        { throw new InvalidOperationException("Deployment preflight refused: invalid_configuration. See the production deployment runbook. Values are redacted."); }
    }

    private static bool IsHttpsListener(string value)
    {
        if (value != value.Trim()) return false;
        var candidate = value;
        foreach (var wildcard in new[] { "+", "*" })
        {
            var prefix = "https://" + wildcard;
            if (value.Equals(prefix, StringComparison.OrdinalIgnoreCase)) candidate = "https://0.0.0.0";
            else if (value.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)) candidate = "https://0.0.0.0:" + value[(prefix.Length + 1)..];
        }
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && Uri.CheckHostName(uri.Host.Trim('[', ']')) != UriHostNameType.Unknown && uri.Port is >= 1 and <= 65535
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/";
    }

    private static void ValidateDeploymentCore(IConfiguration configuration, bool maintenance)
    {
        void Require(bool condition, string code)
        {
            if (!condition) throw new InvalidOperationException($"Deployment preflight refused: {code}. See the production deployment runbook. Values are redacted.");
        }
        Require(!configuration.GetValue("Security:Http:AllowInsecureLocalHttp", false), "local_http_exception");
        Require(!configuration.GetValue("Hosting:TrustPublishedPortGateway", false), "gateway_trust");
        Require(!configuration.GetValue("Observability:LogConversationContent", false), "private_content_logging");
        var telemetry = configuration.GetSection("Observability").Get<AgentCore.Application.Observability.ObservabilityOptions>() ?? new();
        Require(!telemetry.OtlpEnabled || telemetry.HasValidOtlpEndpoint()
            && (new Uri(telemetry.OtlpEndpoint!).Scheme == "https" || new Uri(telemetry.OtlpEndpoint!).IsLoopback), "secure_telemetry_origin_required");
        Require(!configuration.GetSection("Security:EphemeralBootstrap").GetChildren().Any(), "ephemeral_identity");
        Require(!configuration.GetValue("Browser:Enabled", false) && !configuration.GetValue("OpenSandbox:Enabled", false), "unqualified_native_effects");
        var hosts = (configuration["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Require(hosts.Length > 0 && hosts.All(h => !h.Contains('*') && Uri.CheckHostName(h.Trim('[', ']')) != UriHostNameType.Unknown), "exact_hosts_required");
        if (!maintenance)
        {
            var urls = (configuration["urls"] ?? configuration["ASPNETCORE_URLS"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            Require(urls.Length > 0 && urls.All(IsHttpsListener), "https_listener_required");
            Require(!configuration.GetSection("Kestrel:Endpoints").GetChildren().Any(), "unsupported_listener_override");
            try
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    configuration["Kestrel:Certificates:Default:Path"] ?? "",
                    configuration["Kestrel:Certificates:Default:Password"],
                    OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
                Require(certificate.HasPrivateKey && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow
                    && certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow, "usable_certificate_required");
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { Require(false, "usable_certificate_required"); }
        }
        var persistence = configuration.GetSection("Persistence").Get<PersistenceOptions>() ?? new();
        Require(persistence.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase), "durable_sqlite_required");
        try
        {
            var connection = new SqliteConnectionStringBuilder(persistence.ConnectionString);
            Require(Path.IsPathFullyQualified(connection.DataSource) && connection.Mode != SqliteOpenMode.Memory
                && connection.DataSource != ":memory:", "absolute_database_required");
            var root = Path.GetDirectoryName(Path.GetFullPath(connection.DataSource))!;
            Require(Directory.Exists(root) && Path.GetDirectoryName(root) is not null, "private_installation_required");
            var roots = new[] { persistence.WorkspaceRoot, persistence.AttachmentRoot, persistence.ArtifactRoot,
                persistence.DefinitionResourceRoot, persistence.CredentialProtectionKeyRoot };
            Require(roots.All(Path.IsPathFullyQualified), "absolute_roots_required");
            roots = roots.Select(Path.GetFullPath).ToArray();
            Require(roots.All(p => p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                && roots.Distinct(StringComparer.Ordinal).Count() == roots.Length
                && !roots.Any(a => roots.Any(b => a != b && a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.Ordinal))), "disjoint_installation_roots_required");
            var existingDatabase = new FileInfo(connection.DataSource) is { Exists: true, Length: > 0 };
            Require(!existingDatabase || roots.All(Directory.Exists), "installed_root_missing");
            Require(existingDatabase || !Directory.EnumerateFiles(root, "*.db").Any()
                && !roots.Any(path => Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()), "existing_state_requires_database");
            foreach (var path in roots.Append(root).Append(connection.DataSource))
                for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                    Require(new DirectoryInfo(current).LinkTarget is null && new FileInfo(current).LinkTarget is null, "linked_root");
            if (!OperatingSystem.IsWindows())
                Require((File.GetUnixFileMode(root) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0, "private_installation_permissions");
            foreach (var path in roots)
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
                else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var probe = Path.Combine(path, ".preflight-" + Guid.NewGuid().ToString("N"));
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Require(false, "storage_unavailable"); }
    }
}
