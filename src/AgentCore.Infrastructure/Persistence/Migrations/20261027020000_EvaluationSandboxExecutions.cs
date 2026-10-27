using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261027020000_EvaluationSandboxExecutions")]
public sealed class EvaluationSandboxExecutions : Migration
{
    internal static void Build(ModelBuilder model) { UnifiedEvaluationTargetModel.Build(model);
        model.Entity("AgentCore.Infrastructure.Security.TenantApprovalRecord").ToTable("TenantApprovalRequests", table => table.HasCheckConstraint("CK_TenantApprovalRequest_Values", EvaluationSandboxApprovalSchema.Values)); model.Entity("AgentCore.Infrastructure.Persistence.EvaluationSandboxRow", e => {
            foreach (var name in new[] { "TenantId", "ExecutionId", "AttemptId", "ApprovalRequestId" }) e.Property<Guid>(name).HasColumnType("TEXT");
            e.Property<string>("CheckId").IsRequired().HasColumnType("TEXT"); e.Property<string>("Json").IsRequired().HasColumnType("TEXT");
            e.Property<long>("Revision").IsConcurrencyToken().HasColumnType("INTEGER"); e.Property<long>("DueAt").HasColumnType("INTEGER");
            e.Property<int>("Phase").HasColumnType("INTEGER"); e.Property<bool>("CleanupPending").HasColumnType("INTEGER"); e.Property<bool>("FindingPublished").HasColumnType("INTEGER");
            e.HasKey("TenantId", "ExecutionId"); e.HasIndex("TenantId", "AttemptId", "CheckId").IsUnique();
            e.HasIndex("Phase", "CleanupPending", "DueAt"); e.HasIndex("ApprovalRequestId");
            e.ToTable("EvaluationSandboxExecutions");
            e.HasOne("AgentCore.Infrastructure.Persistence.EvaluationAttemptRow", null).WithMany().HasForeignKey("TenantId", "AttemptId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            e.HasOne("AgentCore.Infrastructure.Security.TenantApprovalRecord", null).WithMany().HasForeignKey("ApprovalRequestId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        }); }
    protected override void BuildTargetModel(ModelBuilder model) => Build(model);
    protected override void Up(MigrationBuilder migration)
    {
        migration.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migration.Sql(EvaluationSandboxApprovalSchema.Rebuild());
        migration.Sql("""
        CREATE TABLE EvaluationSandboxExecutions (
            TenantId TEXT NOT NULL, ExecutionId TEXT NOT NULL, AttemptId TEXT NOT NULL, ApprovalRequestId TEXT NOT NULL, CheckId TEXT NOT NULL,
            Revision INTEGER NOT NULL, Phase INTEGER NOT NULL, CleanupPending INTEGER NOT NULL, FindingPublished INTEGER NOT NULL, DueAt INTEGER NOT NULL, Json TEXT NOT NULL,
            PRIMARY KEY(TenantId,ExecutionId),
            FOREIGN KEY(ApprovalRequestId) REFERENCES TenantApprovalRequests(RequestId) ON DELETE RESTRICT,
            FOREIGN KEY(TenantId,AttemptId) REFERENCES EvaluationAttempts(TenantId,AttemptId) ON DELETE RESTRICT);
        CREATE INDEX IX_EvaluationSandboxExecutions_ApprovalRequestId ON EvaluationSandboxExecutions(ApprovalRequestId);
        CREATE UNIQUE INDEX IX_EvaluationSandboxExecutions_TenantId_AttemptId_CheckId ON EvaluationSandboxExecutions(TenantId,AttemptId,CheckId);
        CREATE INDEX IX_EvaluationSandboxExecutions_Phase_CleanupPending_DueAt ON EvaluationSandboxExecutions(Phase,CleanupPending,DueAt);
        CREATE TEMP TABLE EvaluationSandboxForeignKeys(Rows INTEGER CHECK(Rows=0));
        INSERT INTO EvaluationSandboxForeignKeys SELECT count(*) FROM pragma_foreign_key_check;
        DROP TABLE EvaluationSandboxForeignKeys;
        """);
        migration.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }
    protected override void Down(MigrationBuilder migration)
    {
        migration.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        migration.Sql("""
            CREATE TEMP TABLE EvaluationSandboxRollbackGuard(Rows INTEGER CONSTRAINT RestoreMatchingEvaluationSandboxBackup CHECK(Rows=0));
            INSERT INTO EvaluationSandboxRollbackGuard SELECT count(*) FROM EvaluationSandboxExecutions;
            INSERT INTO EvaluationSandboxRollbackGuard SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.SandboxCommand.Evaluation')='object';
            DROP TABLE EvaluationSandboxRollbackGuard;
            DROP TABLE EvaluationSandboxExecutions;
            """);
        migration.Sql(EvaluationSandboxApprovalSchema.Rebuild(native: false));
        migration.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }
}
