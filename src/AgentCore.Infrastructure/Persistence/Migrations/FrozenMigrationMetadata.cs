using System.Security.Cryptography;
using System.Text;

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Generated target models share byte-identical, content-addressed constraint metadata.
// Historical migration Up/Down statements remain frozen in their existing owners.
internal static class FrozenMigrationMetadata
{
    internal static string Read(string sha256)
    {
        using var stream = typeof(FrozenMigrationMetadata).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.Metadata." + sha256 + ".sql")
            ?? throw new InvalidOperationException("Frozen migration metadata SQL is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sql = reader.ReadToEnd();
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))) != sha256)
            throw new InvalidOperationException("Frozen migration metadata SQL has changed.");
        return sql;
    }
}
