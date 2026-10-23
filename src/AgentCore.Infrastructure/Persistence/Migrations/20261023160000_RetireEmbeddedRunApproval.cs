using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261023160000_RetireEmbeddedRunApproval")]
public class RetireEmbeddedRunApproval : TenantConfigurationRemedyAuthoring
{
    // The predecessor's generated target is frozen migration metadata, not the live model.
    // Reuse it here instead of duplicating the entire unchanged target model.
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        modelBuilder.Entity("AgentCore.Infrastructure.Persistence.AgentRunRecord", entity =>
        {
            entity.Ignore("ApprovalExpiresAtUtc");
            entity.HasIndex("SessionId").IsUnique().HasFilter("Status IN (1, 7)").HasDatabaseName("IX_AgentRuns_SessionExecution");
            entity.ToTable("AgentRuns", table =>
            {
                table.HasTrigger("TR_AgentRuns_NoEmbeddedApproval_INSERT");
                table.HasTrigger("TR_AgentRuns_NoEmbeddedApproval_UPDATE");
            });
        });
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(RetireEmbeddedRunApproval).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261023160000_RetireEmbeddedRunApproval.Guards.json")
            ?? throw new InvalidOperationException("Frozen Run guards are missing.");
        using var reader = new StreamReader(stream); var source = reader.ReadToEnd();
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))) != "b665d21d53c260211662c5c72fbb815a3fd012e3d53c8d23bf6b828fa8fd8eaa")
            throw new InvalidOperationException("Frozen Run guards changed.");
        var guards = JsonSerializer.Deserialize<Dictionary<string, string>>(source)!;
        var sql = new StringBuilder();
        foreach (var guard in guards.Keys) sql.AppendLine("DROP TRIGGER " + guard + ";");
        sql.AppendLine("""
        CREATE TEMP TABLE EmbeddedApprovalRetirementGuard (Rows INTEGER CONSTRAINT RequiresExplicitLegacyApprovalRecovery CHECK(Rows=0));
        INSERT INTO EmbeddedApprovalRetirementGuard SELECT count(*) FROM AgentRuns
            WHERE json_type(PayloadJson,'$.approval') NOT IN ('null') OR ApprovalExpiresAtUtc IS NOT NULL OR (Status=2 AND TenantId IS NULL);
        DROP TABLE EmbeddedApprovalRetirementGuard;
        UPDATE AgentRuns SET PayloadJson=json_remove(PayloadJson,'$.approval') WHERE json_type(PayloadJson,'$.approval')='null';
        ALTER TABLE AgentRuns DROP COLUMN ApprovalExpiresAtUtc;
        DROP INDEX IX_AgentRuns_SessionExecution;
        CREATE UNIQUE INDEX IX_AgentRuns_SessionExecution ON AgentRuns(SessionId) WHERE Status IN (1, 7);
        CREATE TRIGGER TR_AgentRuns_NoEmbeddedApproval_INSERT BEFORE INSERT ON AgentRuns
        WHEN json_type(NEW.PayloadJson,'$.approval') IS NOT NULL OR (NEW.Status=2 AND NEW.TenantId IS NULL)
        BEGIN SELECT RAISE(ABORT,'embedded Run approvals are retired; use canonical tenant requests'); END;
        CREATE TRIGGER TR_AgentRuns_NoEmbeddedApproval_UPDATE BEFORE UPDATE ON AgentRuns
        WHEN json_type(NEW.PayloadJson,'$.approval') IS NOT NULL OR (NEW.Status=2 AND NEW.TenantId IS NULL)
        BEGIN SELECT RAISE(ABORT,'embedded Run approvals are retired; use canonical tenant requests'); END;
        """);
        foreach (var guard in guards.Values) sql.AppendLine(guard.TrimEnd().TrimEnd(';') + ";");
        migrationBuilder.Sql(sql.ToString());
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER TR_AgentRuns_NoEmbeddedApproval_INSERT;
        DROP TRIGGER TR_AgentRuns_NoEmbeddedApproval_UPDATE;
        DROP INDEX IX_AgentRuns_SessionExecution;
        CREATE UNIQUE INDEX IX_AgentRuns_SessionExecution ON AgentRuns(SessionId) WHERE Status IN (1, 7) OR (Status = 2 AND TenantId IS NULL);
        ALTER TABLE AgentRuns ADD COLUMN ApprovalExpiresAtUtc INTEGER NULL;
        """);
}
