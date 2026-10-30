using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AgentCore.Infrastructure.Workflows;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Persistence;

/// <summary>Offline read-only validation of the matching current schema and protected records. Never repairs keys.</summary>
public static class ProtectedStateAudit
{
    public static async Task<int> VerifyAsync(string database, string keyRoot, CancellationToken ct = default)
    {
        if (!File.Exists(database) || !Directory.Exists(keyRoot)) throw new InvalidOperationException("Matching state is required.");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(database), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var context = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connectionString).Options);
        var applied = (await context.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (!applied.SequenceEqual(context.Database.GetMigrations(), StringComparer.Ordinal))
            throw new InvalidOperationException("Matching application schema is required.");
        await using var db = new SqliteConnection(connectionString);
        await db.OpenAsync(ct);
        await using (var check = db.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            if (!Equals(await check.ExecuteScalarAsync(ct), "ok")) throw new InvalidOperationException("Integrity check failed.");
            check.CommandText = "PRAGMA foreign_key_check";
            await using var rows = await check.ExecuteReaderAsync(ct);
            if (await rows.ReadAsync(ct)) throw new InvalidOperationException("Ownership references failed validation.");
        }
        // Reuse the production owner's exact Elsa migration and actual table checks, without hosted services/migrations.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFormalWorkflowRuntime(new() { Provider = "Sqlite", ConnectionString = connectionString });
        await using var workflowServices = services.BuildServiceProvider();
        if (!await workflowServices.GetRequiredService<IFormalWorkflowRuntime>().IsReadyAsync(ct))
            throw new InvalidOperationException("Matching workflow schema is required.");
        IDataProtectionProvider Provider(string child, string application)
        {
            var directory = new DirectoryInfo(Path.Combine(keyRoot, child));
            if (!directory.Exists || !directory.EnumerateFiles("key-*.xml").Any())
                throw new InvalidOperationException("Existing matching protection keys are required.");
            return DataProtectionProvider.Create(directory, builder => builder.SetApplicationName(application).DisableAutomaticKeyGeneration());
        }
        IDataProtectionProvider? credentials = null, oauth = null;
        var verified = 0;
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT CredentialId,ProtectionVersion,ProtectedPayload FROM Credentials WHERE ProtectedPayload<>''";
            await using var rows = await command.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                credentials ??= Provider("", "AgentCore.Credentials");
                _ = credentials.CreateProtector("AgentCore.SystemCredential", rows.GetInt32(1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Guid.Parse(rows.GetString(0)).ToString("D")).Unprotect(rows.GetString(2));
                verified++;
            }
        }
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT TenantId,SecretId,ProtectionVersion,ProtectedPayload FROM TenantSecrets WHERE ProtectedPayload<>''";
            await using var rows = await command.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                credentials ??= Provider("", "AgentCore.Credentials");
                _ = credentials.CreateProtector("AgentCore.TenantSecret", rows.GetInt32(2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Guid.Parse(rows.GetString(0)).ToString("D"), Guid.Parse(rows.GetString(1)).ToString("D")).Unprotect(rows.GetString(3));
                verified++;
            }
        }
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT AttemptId,ProtectedProof FROM CredentialOAuthAttempts WHERE ProtectedProof<>''";
            await using var rows = await command.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                oauth ??= Provider("credential-oauth", "AgentCore.TenantCredentialOAuth.v1");
                _ = oauth.CreateProtector("AgentCore.OAuth.PrivateProof.v1", Guid.Parse(rows.GetString(0)).ToString("D")).Unprotect(rows.GetString(1));
                verified++;
            }
        }
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM AuthenticationSessions";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0)
            {
                var accounts = Provider("account-sessions", "AgentCore.AccountSessions.v1").CreateProtector("AgentCore.OfflineKeyVerification.v1");
                const string witness = "AgentCore offline key verification";
                if (accounts.Unprotect(accounts.Protect(witness)) != witness)
                    throw new InvalidOperationException("Account key verification failed.");
            }
        }
        return verified;
    }
}
