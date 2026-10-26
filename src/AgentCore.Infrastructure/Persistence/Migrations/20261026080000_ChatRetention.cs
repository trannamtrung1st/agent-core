using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class ChatRetention : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("RetentionKind", "Sessions", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<int>("RetainAfterTerminalDays", "Sessions", nullable: true);
        migrationBuilder.AddColumn<long>("RetentionDueAtUtc", "Sessions", nullable: true);
        migrationBuilder.AddColumn<int>("SavedChatRetentionDays", "Tenants", nullable: true);
        migrationBuilder.CreateIndex("IX_Sessions_RetentionDueAtUtc_SessionId", "Sessions", new[] { "RetentionDueAtUtc", "SessionId" });
        migrationBuilder.Sql("""
            CREATE TEMP TABLE ChatLifecycleSourceGuard(Rows INTEGER CONSTRAINT RequiresExplicitLifecycleSourceRepair CHECK(Rows=0));
            INSERT INTO ChatLifecycleSourceGuard SELECT count(*) FROM SessionSnapshots
                WHERE LifecycleSource='Legacy' AND (LifecycleStatus IS NULL OR LifecycleStatus<>'Ended');
            DROP TABLE ChatLifecycleSourceGuard;
            UPDATE SessionSnapshots SET LifecycleSource='Host' WHERE LifecycleSource='Legacy' AND LifecycleStatus='Ended';
            CREATE TRIGGER TR_Sessions_RetentionImmutable BEFORE UPDATE ON Sessions
            WHEN OLD.RetentionKind<>NEW.RetentionKind OR OLD.RetainAfterTerminalDays IS NOT NEW.RetainAfterTerminalDays
                OR OLD.RetentionKind=1 AND OLD.RetentionDueAtUtc IS NOT NEW.RetentionDueAtUtc
                OR OLD.RetentionKind=0 AND OLD.RetentionDueAtUtc IS NOT NULL AND OLD.RetentionDueAtUtc IS NOT NEW.RetentionDueAtUtc
            BEGIN SELECT RAISE(ABORT,'Session retention is immutable'); END;
            CREATE TRIGGER TR_Sessions_RetentionValid BEFORE INSERT ON Sessions
            WHEN NEW.RetentionKind NOT IN (0,1) OR NEW.RetainAfterTerminalDays NOT BETWEEN 1 AND 3650
                OR NEW.RetentionKind=1 AND (NEW.RetainAfterTerminalDays IS NOT NULL
                    OR NEW.RetentionDueAtUtc IS NULL OR NEW.RetentionDueAtUtc<>NEW.CreatedAtUtc+86400000
                    OR json_extract(NEW.OriginJson,'$.kind')<>0)
            BEGIN SELECT RAISE(ABORT,'Invalid Session retention'); END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE ChatRetentionDowngradeGuard(Rows INTEGER CONSTRAINT RetentionDowngradeRequiresExplicitExport CHECK(Rows=0));
            INSERT INTO ChatRetentionDowngradeGuard SELECT count(*) FROM Sessions WHERE RetentionKind<>0 OR RetainAfterTerminalDays IS NOT NULL;
            INSERT INTO ChatRetentionDowngradeGuard SELECT count(*) FROM Tenants WHERE SavedChatRetentionDays IS NOT NULL;
            DROP TABLE ChatRetentionDowngradeGuard;
            DROP TRIGGER TR_Sessions_RetentionImmutable;
            DROP TRIGGER TR_Sessions_RetentionValid;
            """);
        migrationBuilder.DropIndex("IX_Sessions_RetentionDueAtUtc_SessionId", "Sessions");
        // Native SQLite column removal preserves the many external trigger owners.
        // EF table rebuilding would temporarily drop Sessions beneath those triggers.
        migrationBuilder.Sql("""
            ALTER TABLE Sessions DROP COLUMN RetentionKind;
            ALTER TABLE Sessions DROP COLUMN RetainAfterTerminalDays;
            ALTER TABLE Sessions DROP COLUMN RetentionDueAtUtc;
            ALTER TABLE Tenants DROP COLUMN SavedChatRetentionDays;
            """);
    }
}
