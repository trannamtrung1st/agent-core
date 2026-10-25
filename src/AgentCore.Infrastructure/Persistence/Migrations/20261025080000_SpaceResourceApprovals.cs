using AgentCore.Infrastructure.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AgentCore.Infrastructure.Persistence.Migrations;
[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261025080000_SpaceResourceApprovals")]
public sealed partial class SpaceResourceApprovals : Migration
{
    protected override void Up(MigrationBuilder builder) => Rebuild(builder, true);
    protected override void Down(MigrationBuilder builder) => Rebuild(builder, false);
    private static void Rebuild(MigrationBuilder builder, bool up)
    {
        if (!up) builder.Sql("""
            CREATE TEMP TABLE SpaceSourceApprovalDowngradeGuard(Rows INTEGER CHECK(Rows=0));
            INSERT INTO SpaceSourceApprovalDowngradeGuard SELECT
              (SELECT count(*) FROM AgentRuns r,json_each(r.PayloadJson,'$.admission.configuration.resources') item WHERE json_type(item.value,'$.spaceSource')='object')
              +(SELECT count(*) FROM TenantApprovalRequests WHERE instr(PayloadJson,'space:')>0);
            DROP TABLE SpaceSourceApprovalDowngradeGuard;
            """);
        using var stream=typeof(SpaceResourceApprovals).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql.20261025080000_SpaceResourceApprovals.Previous.json")
            ?? throw new InvalidOperationException("Frozen Space source predecessor is missing.");
        using var memory=new MemoryStream(); stream.CopyTo(memory); var bytes=memory.ToArray();
        if(Convert.ToHexStringLower(SHA256.HashData(bytes))!="e54efbf3d51e7ba1d5bc4194add1f8c33cedb6efc1f2daebdd05158ae19ab36f") throw new InvalidOperationException("Frozen Space source predecessor changed.");
        var previous=JsonSerializer.Deserialize<Dictionary<string,string>>(bytes)!;
        var sql=new System.Text.StringBuilder();
        foreach(var item in previous.Where(p=>p.Key.StartsWith("trigger:",StringComparison.Ordinal))) sql.AppendLine("DROP TRIGGER "+item.Key[8..]+";");
        if(!up) foreach(var name in NewGuardNames) sql.AppendLine("DROP TRIGGER "+name+";");
        var source=previous["table:TenantApprovalRequests"];
        var ddl=up ? TenantPublicDownloads.ReplaceCheck(source,SpaceResourceApprovalPersistence.ValuesConstraint) : source;
        var columns=source.Split('\n').Where(line=>line.StartsWith("    \"",StringComparison.Ordinal)).Select(line=>"\""+line.TrimStart().Split('"')[1]+"\"");
        var names=string.Join(",",columns);
        sql.AppendLine(ddl.Replace("CREATE TABLE \"TenantApprovalRequests\"","CREATE TABLE \"__space_resource_approvals\"",StringComparison.Ordinal).TrimEnd(';')+";");
        sql.AppendLine("INSERT INTO __space_resource_approvals ("+names+") SELECT "+names+" FROM TenantApprovalRequests;");
        sql.AppendLine("DROP TABLE TenantApprovalRequests; ALTER TABLE __space_resource_approvals RENAME TO TenantApprovalRequests;");
        foreach(var item in previous.Where(p=>p.Key.StartsWith("trigger:",StringComparison.Ordinal) || p.Key.StartsWith("index:",StringComparison.Ordinal) && p.Value.Contains("ON \"TenantApprovalRequests\"",StringComparison.Ordinal))) sql.AppendLine(item.Value.TrimEnd(';')+";");
        if(up) sql.AppendLine(SourceGuards);
        builder.Sql("PRAGMA foreign_keys=0;",suppressTransaction:true);
        builder.Sql(sql.ToString());
        builder.Sql("PRAGMA foreign_keys=1;",suppressTransaction:true);
    }
}
