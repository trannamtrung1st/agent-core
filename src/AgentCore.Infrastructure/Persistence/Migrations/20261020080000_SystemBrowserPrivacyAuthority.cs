using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261020080000_SystemBrowserPrivacyAuthority")]
public sealed class SystemBrowserPrivacyAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var operation in new[] { "INSERT", "UPDATE" })
        {
            var transition = operation == "INSERT" ? "NEW.Revision = 1" : "NEW.Id = OLD.Id AND NEW.Revision = OLD.Revision + 1";
            migrationBuilder.Sql($$"""
                CREATE TRIGGER TR_BrowserPrivacy_System_{{operation}} BEFORE {{operation}} ON BrowserPrivacy
                BEGIN
                    SELECT RAISE(ABORT, 'Invalid system Browser privacy policy') WHERE NOT COALESCE((
                        NEW.Id = 1 AND {{transition}} AND NEW.Revision > 0
                        AND length(CAST(NEW.PolicyJson AS BLOB)) <= 40960 AND json_valid(NEW.PolicyJson)
                        AND json_type(NEW.PolicyJson) = 'object' AND (SELECT count(*) FROM json_each(NEW.PolicyJson)) = 4
                        AND (SELECT count(DISTINCT key) FROM json_each(NEW.PolicyJson) WHERE key IN ('Mode','Revision','UnmaskedOrigins','TrustedGraphicsOrigins')) = 4
                        AND json_type(NEW.PolicyJson,'$.Mode') = 'integer' AND json_extract(NEW.PolicyJson,'$.Mode') IN (0,1,2)
                        AND json_type(NEW.PolicyJson,'$.Revision') = 'integer' AND json_extract(NEW.PolicyJson,'$.Revision') = NEW.Revision
                        AND json_type(NEW.PolicyJson,'$.UnmaskedOrigins') = 'array' AND json_array_length(NEW.PolicyJson,'$.UnmaskedOrigins') <= 32
                        AND json_type(NEW.PolicyJson,'$.TrustedGraphicsOrigins') = 'array' AND json_array_length(NEW.PolicyJson,'$.TrustedGraphicsOrigins') <= 32
                        AND (json_extract(NEW.PolicyJson,'$.Mode') = 1 AND json_array_length(NEW.PolicyJson,'$.UnmaskedOrigins') > 0
                            OR json_extract(NEW.PolicyJson,'$.Mode') <> 1 AND json_array_length(NEW.PolicyJson,'$.UnmaskedOrigins') = 0)
                        AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PolicyJson) arrays, json_each(arrays.value) origin
                            WHERE arrays.key IN ('UnmaskedOrigins','TrustedGraphicsOrigins') AND (origin.type <> 'text' OR length(origin.value) > 512
                                OR NOT (origin.value LIKE 'http://_%' OR origin.value LIKE 'https://_%') OR instr(substr(origin.value,instr(origin.value,'://')+3),'/') > 0
                                OR instr(origin.value,'*') > 0 OR instr(origin.value,char(92)) > 0 OR instr(origin.value,'?') > 0 OR instr(origin.value,'#') > 0 OR instr(origin.value,'@') > 0))
                        AND (SELECT count(*) FROM json_each(NEW.PolicyJson,'$.UnmaskedOrigins')) = (SELECT count(DISTINCT value COLLATE NOCASE) FROM json_each(NEW.PolicyJson,'$.UnmaskedOrigins'))
                        AND (SELECT count(*) FROM json_each(NEW.PolicyJson,'$.TrustedGraphicsOrigins')) = (SELECT count(DISTINCT value COLLATE NOCASE) FROM json_each(NEW.PolicyJson,'$.TrustedGraphicsOrigins'))
                    ),0);
                END;
                """);
        }
        migrationBuilder.Sql("CREATE TRIGGER TR_BrowserPrivacy_System_Delete BEFORE DELETE ON BrowserPrivacy BEGIN SELECT RAISE(ABORT, 'System Browser privacy is retained'); END;");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TEMP TABLE BrowserPrivacyRollbackGuard (OwnedRows INTEGER CHECK (OwnedRows=0));
        INSERT INTO BrowserPrivacyRollbackGuard SELECT count(*) FROM BrowserPrivacy;
        DROP TABLE BrowserPrivacyRollbackGuard;
        DROP TRIGGER TR_BrowserPrivacy_System_Delete;
        DROP TRIGGER TR_BrowserPrivacy_System_UPDATE;
        DROP TRIGGER TR_BrowserPrivacy_System_INSERT;
        """);
}
