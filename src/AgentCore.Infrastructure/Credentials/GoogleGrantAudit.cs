using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace AgentCore.Infrastructure.Credentials;

/// <summary>Offline operator audit. No HTTP, host startup, migrations, token output or writes.</summary>
public static class GoogleGrantAudit
{
    public static async Task<IReadOnlyDictionary<string, int>> ReadAsync(string database, string keyRoot, string canonicalClientId, CancellationToken ct = default)
    {
        if (!File.Exists(database) || !Directory.Exists(keyRoot))
            throw new InvalidOperationException("Audit requires the existing database and matching credential key directory.");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRoot), b => b.SetApplicationName("AgentCore.Credentials").DisableAutomaticKeyGeneration());
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(database), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await db.OpenAsync(ct).ConfigureAwait(false);
        await using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.CredentialId,c.ProtectionVersion,c.ProtectedPayload,t.TenantId,t.DeletedAt,
                COALESCE(e.ProfileJson,d.ProfileJson),
                (SELECT count(*) FROM AgentCredentialBindings b WHERE b.CredentialId=c.CredentialId),c.Status
            FROM Credentials c LEFT JOIN TenantCredentials t ON t.CredentialId=c.CredentialId
            LEFT JOIN EmailAccountProfiles e ON e.CredentialId=c.CredentialId
            LEFT JOIN DriveAccountProfiles d ON d.CredentialId=c.CredentialId
            WHERE c.Purpose='GoogleDriveAccount' OR (c.Purpose='EmailAccount' AND json_extract(e.ProfileJson,'$.Preset')=0)
            """;
        var counts = new Dictionary<string, int> { ["canonical"] = 0, ["otherClient"] = 0, ["unstamped"] = 0, ["invalid"] = 0,
            ["unreadable"] = 0, ["synthetic"] = 0, ["noGrant"] = 0, ["retiredAccounts"] = 0, ["affectedTenants"] = 0, ["affectedBindings"] = 0, ["connected"] = 0, ["activeConnected"] = 0 };
        var affected = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            using var profile = JsonDocument.Parse(reader.IsDBNull(5) ? "{}" : reader.GetString(5));
            if (profile.RootElement.TryGetProperty("ConnectionStatus", out var status) && status.ValueKind == JsonValueKind.Number && status.GetInt32() == 1)
            { counts["connected"]++; if (reader.IsDBNull(4) && reader.GetString(7) == "Active") counts["activeConnected"]++; }
            if (!reader.IsDBNull(4)) counts["retiredAccounts"]++;
            var category = "noGrant";
            if (profile.RootElement.TryGetProperty("Synthetic", out var synthetic) && synthetic.ValueKind == JsonValueKind.True) category = "synthetic";
            else if (reader.GetString(2).Length > 0)
            {
                try
                {
                    var protector = provider.CreateProtector("AgentCore.SystemCredential", reader.GetInt32(1).ToString(System.Globalization.CultureInfo.InvariantCulture), Guid.Parse(reader.GetString(0)).ToString("D"));
                    var payload = protector.Unprotect(reader.GetString(2));
                    if (!payload.StartsWith("agent-core.google-oauth.v1:", StringComparison.Ordinal)) category = "unstamped";
                    else
                    {
                        try { category = GoogleOAuthRefreshGrant.Read(payload).ClientId == canonicalClientId ? "canonical" : "otherClient"; }
                        catch (Application.Sessions.AgentCoreException) { category = "invalid"; }
                    }
                }
                catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException or InvalidOperationException) { category = "unreadable"; }
            }
            counts[category]++;
            if (category is "otherClient" or "unstamped" or "invalid" or "unreadable")
            {
                if (!reader.IsDBNull(3)) affected.Add(reader.GetString(3));
                counts["affectedBindings"] += reader.GetInt32(6);
            }
        }
        counts["affectedTenants"] = affected.Count;
        return counts;
    }
}
