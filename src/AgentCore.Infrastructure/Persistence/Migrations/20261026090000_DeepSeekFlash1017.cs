using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

/// <summary>Explicit route maintenance; historical pins and copied capabilities remain untouched.</summary>
public partial class DeepSeekFlash1017 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE DeepSeekFlash1017RequiresSettledRuns (Rows INTEGER CONSTRAINT DeepSeekFlash1017RequiresSettledRuns CHECK(Rows=0));
            INSERT INTO DeepSeekFlash1017RequiresSettledRuns SELECT count(*) FROM AgentRuns
            WHERE Status NOT IN (4,5,6)
                AND json_extract(PayloadJson,'$.pinnedModel.binding.route.kind')=3
                AND json_extract(PayloadJson,'$.pinnedModel.externalModelId')='deepseek/deepseek-v4.1-flash';
            DROP TABLE DeepSeekFlash1017RequiresSettledRuns;

            CREATE TEMP TABLE DeepSeekFlash1017RequiresIdentityReconciliation (Rows INTEGER CONSTRAINT DeepSeekFlash1017RequiresIdentityReconciliation CHECK(Rows=0));
            INSERT INTO DeepSeekFlash1017RequiresIdentityReconciliation
            SELECT count(*) FROM ModelPresets old JOIN ModelPresets target ON target.Kind=old.Kind
            WHERE old.Kind=3 AND old.ExternalModelId='deepseek/deepseek-v4.1-flash'
                AND target.ExternalModelId='deepseek/deepseek-v4.1-flash-1017';
            INSERT INTO DeepSeekFlash1017RequiresIdentityReconciliation
            SELECT count(*) FROM ModelOfferings old JOIN ModelOfferings target
                ON target.TenantId=old.TenantId AND target.ConnectionId=old.ConnectionId
            WHERE old.Kind=3 AND old.ExternalModelId='deepseek/deepseek-v4.1-flash'
                AND target.ExternalModelId='deepseek/deepseek-v4.1-flash-1017';
            DROP TABLE DeepSeekFlash1017RequiresIdentityReconciliation;

            PRAGMA defer_foreign_keys=ON;
            DROP TRIGGER TR_ModelOfferings_Identity_UPDATE;
            UPDATE Tenants SET Revision=Revision+1 WHERE TenantId IN (
                SELECT TenantId FROM ModelOfferings WHERE Kind=3 AND ExternalModelId='deepseek/deepseek-v4.1-flash');
            UPDATE ModelPresets SET ExternalModelId='deepseek/deepseek-v4.1-flash-1017', Revision=Revision+1,
                Label=CASE WHEN Label='DeepSeek V4.1 Flash' THEN 'DeepSeek V4.1 Flash 1017' ELSE Label END
            WHERE Kind=3 AND ExternalModelId='deepseek/deepseek-v4.1-flash';
            UPDATE ModelOfferings SET ExternalModelId='deepseek/deepseek-v4.1-flash-1017', Revision=Revision+1,
                Label=CASE WHEN Label='DeepSeek V4.1 Flash' THEN 'DeepSeek V4.1 Flash 1017' ELSE Label END
            WHERE Kind=3 AND ExternalModelId='deepseek/deepseek-v4.1-flash';
            CREATE TRIGGER TR_ModelOfferings_Identity_UPDATE BEFORE UPDATE ON ModelOfferings
            WHEN NEW.OfferingId<>OLD.OfferingId OR NEW.TenantId<>OLD.TenantId OR NEW.ConnectionId<>OLD.ConnectionId OR NEW.Kind<>OLD.Kind
                OR NEW.ExternalModelId<>OLD.ExternalModelId OR NEW.PresetId IS NOT OLD.PresetId OR OLD.Retired=1 AND NEW.Retired<>1
            BEGIN SELECT RAISE(ABORT,'model offering identity and retirement are immutable'); END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Only an unused, unmodified seed can be rolled back without retargeting retained work.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE DeepSeekFlash1017DowngradeRequiresBackupRestore (Rows INTEGER CONSTRAINT DeepSeekFlash1017DowngradeRequiresBackupRestore CHECK(Rows=0));
            INSERT INTO DeepSeekFlash1017DowngradeRequiresBackupRestore SELECT count(*) FROM ModelOfferings
            WHERE Kind=3 AND ExternalModelId='deepseek/deepseek-v4.1-flash-1017';
            INSERT INTO DeepSeekFlash1017DowngradeRequiresBackupRestore SELECT count(*) FROM ModelPresets
            WHERE Kind=3 AND ExternalModelId='deepseek/deepseek-v4.1-flash-1017'
                AND (lower(PresetId)<>'dca10000-0000-4000-8000-000000000007' OR Revision<>2);
            INSERT INTO DeepSeekFlash1017DowngradeRequiresBackupRestore SELECT count(*) FROM ModelPresets
            WHERE Kind=3 AND ExternalModelId='deepseek/deepseek-v4.1-flash';
            DROP TABLE DeepSeekFlash1017DowngradeRequiresBackupRestore;
            UPDATE ModelPresets SET ExternalModelId='deepseek/deepseek-v4.1-flash', Revision=1,
                Label=CASE WHEN Label='DeepSeek V4.1 Flash 1017' THEN 'DeepSeek V4.1 Flash' ELSE Label END
            WHERE Kind=3 AND ExternalModelId='deepseek/deepseek-v4.1-flash-1017';
            """);
    }
}
