using System.Security.Cryptography;
using System.Text;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class FrozenMigrationMetadataTests
{
    [Fact]
    public void Infrastructure_string_heap_retains_room_for_remaining_implementation()
    {
        using var stream = File.OpenRead(typeof(AgentCoreDbContext).Assembly.Location);
        using var pe = new PEReader(stream);
        var bytes = pe.GetMetadataReader().GetHeapSize(HeapIndex.UserString);
        // ECMA-335 user-string offsets have 24 bits; retain at least 2 MiB of headroom.
        Assert.InRange(bytes, 1, 14 * 1024 * 1024);
    }

    // Original generated literal digests captured before extraction, independent of resource loading.
    public static TheoryData<string, string, string> OriginalConstraints => new()
    {
        { "20261025012506_SpaceMembershipOwnership", "CK_TenantApprovalRequest_Values", "c3d774f8790cf8b52cbdd012556731a6df9713964ea08f15cb2bd3370b946d2f" },
        { "20261025013639_SpaceEnvironmentsAndSecrets", "CK_TenantApprovalRequest_Values", "c3d774f8790cf8b52cbdd012556731a6df9713964ea08f15cb2bd3370b946d2f" },
        { "20261025015353_SessionSpaceContext", "CK_TenantApprovalRequest_Values", "c3d774f8790cf8b52cbdd012556731a6df9713964ea08f15cb2bd3370b946d2f" },
        { "20261019023004_TenantWorkspaceMutationReview", "CK_TenantApprovalRequest_Values", "1dc7ebc70d6c458a8be931167318eaa492cbab6d6d3b0a226e25601871b5910e" },
        { "20261019035251_TenantWorkspaceOfficeReadReview", "CK_TenantApprovalRequest_Values", "f80f4bd00b2f15746c1d28c5c00650d1daf7e035d96ad69a1eff632b71f2bc11" },
        { "20261019041906_TenantWorkspaceOfficeMutation", "CK_TenantApprovalRequest_Values", "edd8d1823130ede8b42d81ddcd6f54fa1b4ca2483bc58f58e69c80ed09f65191" },
        { "20261019053112_TenantArtifactWorkspaceSourceReview", "CK_TenantApprovalRequest_Values", "ad63bf10af6db168dc1da7f93bf070a5b68fdbadce05d98213f1f1bbf496d983" },
        { "20261019071133_TenantResourceReadReview", "CK_TenantApprovalRequest_Values", "39d8ac314f89f38fb8046827aeed4f1ff87cadb8df3fcc49097bbefa7041eb60" },
        { "20261019075545_TenantResourceNamespaceReview", "CK_TenantApprovalRequest_Values", "0c368b41f787b6f6e086e6aa5e12b26dfa25e2803b17f577e0dcd3e398ccf2af" },
        { "20261019082753_TenantKnowledgeReadReview", "CK_TenantApprovalRequest_Values", "094374c08172b945c1b082b9af858417ee8b4f1b19d54b454ebfdbe9234ac1e4" },
        { "20261019085021_TenantArtifactResourceSourceReview", "CK_TenantApprovalRequest_Values", "75bb2ce9aaabee47e15be019ee7ffe633abcb4bacd113fac07e6c1b235239982" },
        { "20261019092834_TenantAttachmentOwnership", "CK_TenantApprovalRequest_Values", "75bb2ce9aaabee47e15be019ee7ffe633abcb4bacd113fac07e6c1b235239982" },
        { "20261019114545_TenantAttachmentReadReview", "CK_TenantApprovalRequest_Values", "7a71590346cd73bc0047293a5bfb5c6844a9282cf8b28703c0876aecfd4b3e8d" },
        { "20261019132944_TenantAttachmentArtifactSource", "CK_TenantApprovalRequest_Values", "127deebc6e32ceb33604d08d787587b6dd5a007013a87fe029bd01381709be3d" },
        { "20261019151350_TenantAttachmentNamespaceReview", "CK_TenantApprovalRequest_Values", "348faa7e772e50549b25f4d4d5ec64a2e99f4dc7b3a6f085b071ad9160749654" },
        { "20261019160245_TenantHumanMaterializedArtifact", "CK_TenantApprovalRequest_Values", "348faa7e772e50549b25f4d4d5ec64a2e99f4dc7b3a6f085b071ad9160749654" },
        { "20261020000100_TenantBrowserArtifactPublication", "CK_TenantApprovalRequest_Values", "348faa7e772e50549b25f4d4d5ec64a2e99f4dc7b3a6f085b071ad9160749654" },
        { "20261020060000_TenantBrowserProfileReset", "CK_TenantApprovalRequest_Values", "348faa7e772e50549b25f4d4d5ec64a2e99f4dc7b3a6f085b071ad9160749654" },
        { "20261020100000_TenantBackgroundRuntime", "CK_TenantApprovalRequest_Values", "348faa7e772e50549b25f4d4d5ec64a2e99f4dc7b3a6f085b071ad9160749654" },
        { "20261020110000_TenantBackgroundCreationReview", "CK_TenantApprovalRequest_Values", "8b4fddde4bae9ddb3ab0e2118c84232fa8b2a10da5521f5f628c1992d62b00b1" },
        { "20261020120000_TenantBackgroundReportReview", "CK_TenantApprovalRequest_Values", "a9762c26d85d6cd243461d46f308ed4b06eca256e950db53ac02e29ffe538fa9" },
        { "20261020130000_TenantCompletionReadReview", "CK_TenantApprovalRequest_Values", "a8cd0d96012bed1c2301dfe4be4b43b04847d43447e7e801451bf1a0f4ba33c8" },
        { "20261020140000_TenantReportSourceReadReview", "CK_TenantApprovalRequest_Values", "50cd35f57b0ea514bb1cb7be698a8851cff3afbe1bd85004b00b3b2c31680e1e" },
        { "20261020150000_TenantAccountReadReview", "CK_TenantApprovalRequest_Values", "6c62fbad2bcfda9e23ba268cb0b58d163ecfa953524a88aab8f39856b39a144c" },
        { "20261020160000_TenantAccountActionReview", "CK_TenantApprovalRequest_Values", "a48d985be2644de51902c3e8514e455d08f1986c1b442086588799e928bcbbcc" },
        { "20261020170000_TenantAccountOperationRead", "CK_TenantApprovalRequest_Values", "a48d985be2644de51902c3e8514e455d08f1986c1b442086588799e928bcbbcc" },
        { "20261020180000_TenantAutonomousAutomation", "CK_TenantApprovalRequest_Values", "a48d985be2644de51902c3e8514e455d08f1986c1b442086588799e928bcbbcc" },
        { "20261020190000_TenantIndependentAccountConsent", "CK_TenantApprovalRequest_Values", "a2491a25fbd3a9272156307e5d030a58a726db330b988e84f434aecf06cc9b37" },
        { "20261020200000_TenantIndependentChildControls", "CK_TenantApprovalRequest_Values", "730786a9ecb61b4a5cf42641e2d5e961827079d957560b31fab964734c4f6212" },
        { "20261021010000_TenantAutomationAdmissionApproval", "CK_TenantApprovalRequest_Values", "8554f4754f3f27be8103b82d3d51540dffed67dae16c9c3e1180edcfa9c4eca1" },
        { "20261021020000_TenantWebhookOwnership", "CK_TenantApprovalRequest_Values", "8554f4754f3f27be8103b82d3d51540dffed67dae16c9c3e1180edcfa9c4eca1" },
        { "20261021030000_TenantEventOwnership", "CK_TenantApprovalRequest_Values", "f06d1dfb35fecb60e87e81124021ac5be5df70161e63661ba84c932898acf3e9" },
        { "20261021040000_TenantEmailWatchOwnership", "CK_TenantApprovalRequest_Values", "7ccc7e1be14ea1b151e8b1848415809162ecf40125776bed524c325c3ff225e9" },
        { "20261021050000_TenantAutomationCompletionDestination", "CK_TenantApprovalRequest_Values", "c0da734e50fe2549ee689ea0eaf9236a85d1db5e2f37f94b3e6d7a5500e70a53" },
        { "20261021060000_TenantAutomationConversation", "CK_TenantApprovalRequest_Values", "77b8dfbe325f295525000a169da9d2814e0981952ecde11a167621a483a6f9d2" },
        { "20261021102840_RetireOwnerCapabilityStore", "CK_TenantApprovalRequest_Values", "77b8dfbe325f295525000a169da9d2814e0981952ecde11a167621a483a6f9d2" },
        { "20261021144350_TenantUserProfiles", "CK_TenantApprovalRequest_Values", "77b8dfbe325f295525000a169da9d2814e0981952ecde11a167621a483a6f9d2" },
        { "20261021164125_TenantLearnedMemory", "CK_TenantApprovalRequest_Values", "77b8dfbe325f295525000a169da9d2814e0981952ecde11a167621a483a6f9d2" },
        { "20261021185601_TenantMemoryOperations", "CK_TenantApprovalRequest_Values", "092a998ca09d3b65d4e321e15d472c94718cc70e4e0853eb7dc85b64a03a934c" },
        { "20261021201045_TenantContinuityRead", "CK_TenantApprovalRequest_Values", "1a1c84603d84d6e31b9a3d3aa4cb7d900d9dc33078399f8f80db3d07cfbf32d3" },
        { "20261021212948_TenantExperience", "CK_TenantApprovalRequest_Values", "1a1c84603d84d6e31b9a3d3aa4cb7d900d9dc33078399f8f80db3d07cfbf32d3" },
    };

    [Theory]
    [MemberData(nameof(OriginalConstraints))]
    public void Historical_target_model_constraint_retains_exact_original_SQL_bytes(string migrationId, string constraint, string expectedSha256)
    {
        using var db = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=:memory:").Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[migrationId], db.Database.ProviderName!);
        var sql = migration.TargetModel.GetEntityTypes().SelectMany(e => e.GetCheckConstraints()).Single(c => c.Name == constraint).Sql;
        Assert.Equal(expectedSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
    }

    [Fact]
    public async Task Historical_to_current_SQLite_roundtrip_preserves_actual_guards_indexes_and_current_model()
    {
        var path = Path.Combine(Path.GetTempPath(), "frozen-migration-metadata-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var db = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>()
                .UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
            var migrator = db.GetService<IMigrator>();
            const string historical = "20261019023004_TenantWorkspaceMutationReview";
            await migrator.MigrateAsync(historical);
            var original = await Schema(db);
            await migrator.MigrateAsync("20261023140000_TenantConfigurationRemedyAuthoring");
            var predecessor = await Schema(db);
            Assert.Equal(133, predecessor.Keys.Count(k => k.StartsWith("trigger:", StringComparison.Ordinal)));
            await migrator.MigrateAsync("20261023180000_TenantPublicDownloads");
            var downloads = await Schema(db);
            Assert.Equal(135, downloads.Keys.Count(k => k.StartsWith("trigger:", StringComparison.Ordinal)));
            foreach (var guard in predecessor.Where(item => item.Key.StartsWith("trigger:", StringComparison.Ordinal)))
            {
                // Public downloads extends only these two documented tool allowlists.
                var expected = guard.Key is "trigger:TR_AgentRuns_DirectWorkspace_Insert" or "trigger:TR_AgentRuns_DirectWorkspace_Update"
                    ? guard.Value.Replace("'office.spreadsheet.edit')", "'office.spreadsheet.edit','web.download')", StringComparison.Ordinal) : guard.Value;
                Assert.Equal(expected, downloads[guard.Key]);
            }
            await migrator.MigrateAsync("20261025080000_SpaceResourceApprovals");
            var spaces = await Schema(db);
            await migrator.MigrateAsync("20261025085000_TenantDynamicModels");
            var modelPredecessor = await Schema(db);
            foreach (var guard in spaces.Where(item => item.Key.StartsWith("trigger:", StringComparison.Ordinal)))
                Assert.Equal(guard.Value, modelPredecessor[guard.Key]);
            await migrator.MigrateAsync();
            var current = await Schema(db);
            Assert.Equal(190, current.Keys.Count(k => k.StartsWith("trigger:", StringComparison.Ordinal)));
            foreach (var guard in modelPredecessor.Where(item => item.Key.StartsWith("trigger:", StringComparison.Ordinal)))
            {
                // Canonical identity changes these four field references. The
                // dedicated cutover fixture checks their new closed identities.
                if (guard.Key is "trigger:TR_TenantApprovals_AutomationAdmission_INSERT" or "trigger:TR_TenantApprovals_ConfigurationRemedy_INSERT"
                    or "trigger:TR_TenantApprovals_CredentialBinding_INSERT" or "trigger:TR_TenantOccurrences_Source_Update")
                {
                    Assert.NotEqual(guard.Value, current[guard.Key]);
                    Assert.DoesNotContain("CatalogKey", current[guard.Key]); Assert.DoesNotContain("ProviderAlias", current[guard.Key]);
                }
                else Assert.Equal(guard.Value, current[guard.Key]);
            }
            Assert.False(db.Database.HasPendingModelChanges());
            await migrator.MigrateAsync(historical);
            SameSchema(original, await Schema(db));
            await migrator.MigrateAsync();
            SameSchema(current, await Schema(db));
            Assert.False(db.Database.HasPendingModelChanges());
            using var check = db.Database.GetDbConnection().CreateCommand();
            check.CommandText = "PRAGMA foreign_key_check";
            using var result = await check.ExecuteReaderAsync();
            Assert.False(await result.ReadAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    private static void SameSchema(SortedDictionary<string, string> expected, SortedDictionary<string, string> actual)
    {
        var changes = expected.Keys.Union(actual.Keys).Where(key => expected.GetValueOrDefault(key) != actual.GetValueOrDefault(key))
            .Select(key =>
            {
                var before = expected.GetValueOrDefault(key, "<absent>"); var after = actual.GetValueOrDefault(key, "<absent>");
                var offset = Enumerable.Range(0, Math.Min(before.Length, after.Length)).FirstOrDefault(i => before[i] != after[i], Math.Min(before.Length, after.Length));
                return key + " at " + offset + ": expected " + before.Substring(Math.Max(0, offset - 50), Math.Min(180, before.Length - Math.Max(0, offset - 50)))
                    + "; actual " + after.Substring(Math.Max(0, offset - 50), Math.Min(180, after.Length - Math.Max(0, offset - 50)));
            }).ToArray();
        Assert.True(changes.Length == 0, string.Join("\n", changes));
    }

    private static async Task<SortedDictionary<string, string>> Schema(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE type IN ('index','trigger') AND sql IS NOT NULL ORDER BY type,name";
        using var rows = await command.ExecuteReaderAsync();
        var values = new SortedDictionary<string, string>();
        while (await rows.ReadAsync())
        {
            var kind = rows.GetString(0); var sql = rows.GetString(2);
            // Older hand-written Down scripts recreate the same indexes with different
            // identifier quoting/spacing. Keep literals/operators/filters and trigger SQL exact.
            if (kind == "index")
            {
                var tokens = System.Text.RegularExpressions.Regex.Matches(sql,
                    """'(?:[^']|'')*'|"(?:[^"]|"")*"|[A-Za-z_][A-Za-z0-9_$]*|\d+|!=|<>|<=|>=|[^\s;]""")
                    .Select(m => m.Value.StartsWith('"') ? m.Value[1..^1].Replace("\"\"", "\"") : m.Value);
                sql = System.Text.Json.JsonSerializer.Serialize(tokens);
            }
            values.Add(kind + ":" + rows.GetString(1), sql);
        }
        return values;
    }
}
