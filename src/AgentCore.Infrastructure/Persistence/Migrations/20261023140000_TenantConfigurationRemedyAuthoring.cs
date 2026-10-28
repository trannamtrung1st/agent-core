using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;
public partial class TenantConfigurationRemedyAuthoring : Migration
{
    internal void BuildFrozenTarget(Microsoft.EntityFrameworkCore.ModelBuilder model) => BuildTargetModel(model);
    internal static string ValuesConstraint => Read("Check.sql");
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, true);
    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, false);
    private static string Read(string suffix)
    {
        using var stream = typeof(TenantConfigurationRemedyAuthoring).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql.20261023140000_TenantConfigurationRemedyAuthoring." + suffix)
            ?? throw new InvalidOperationException("Frozen configuration migration source is missing.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, bool up)
    {
        var source = Read("Previous.json");
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))) != "616905a52b832d8286088245602c65accbf83760a361bf0db2522e791e4d3826") throw new InvalidOperationException("Frozen predecessor metadata changed.");
        var previous = JsonSerializer.Deserialize<Dictionary<string,string>>(source)!; var sql = new StringBuilder();
        if (!up) sql.AppendLine("""
            CREATE TEMP TABLE ConfigurationDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO ConfigurationDowngradeGuard SELECT (SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.ManagementMutation.Remedy')='object' OR json_type(PayloadJson,'$.Operation.ManagementMutation.Binding')='object')
             +(SELECT count(*) FROM AgentRuns WHERE TenantId IS NOT NULL AND json_extract(PayloadJson,'$.admission.activation.kind')=8);
            DROP TABLE ConfigurationDowngradeGuard;
            """);
        foreach (var guard in previous.Keys.Where(k=>k.StartsWith("trigger:",StringComparison.Ordinal))) sql.AppendLine("DROP TRIGGER IF EXISTS "+guard[8..]+";");
        foreach (var guard in new[] { "TR_TenantApprovals_ConfigurationRemedy_INSERT", "TR_TenantApprovals_CredentialBinding_INSERT", "TR_TenantOperationReceipts_ConfigurationBinding_INSERT", "TR_TenantOperationReceipts_ConfigurationBinding_UPDATE", "TR_TenantRuns_Configuration_INSERT", "TR_TenantRuns_Configuration_UPDATE" }) sql.AppendLine("DROP TRIGGER IF EXISTS "+guard+";");
        var ddl=previous["table:TenantApprovalRequests"];
        if(up) ddl=ReplaceCheck(ddl,"CK_TenantApprovalRequest_Values",ValuesConstraint);
        var tableName=ddl.IndexOf("TenantApprovalRequests",StringComparison.Ordinal);
        sql.AppendLine(ddl[..tableName]+"__configuration_TenantApprovalRequests"+ddl[(tableName+"TenantApprovalRequests".Length)..]+";");
        sql.AppendLine("INSERT INTO __configuration_TenantApprovalRequests SELECT * FROM TenantApprovalRequests; DROP TABLE TenantApprovalRequests; ALTER TABLE __configuration_TenantApprovalRequests RENAME TO TenantApprovalRequests;");
        foreach(var index in previous.Where(i=>i.Key.StartsWith("index:",StringComparison.Ordinal))) sql.AppendLine(index.Value+";");
        foreach(var guard in previous.Where(i=>i.Key.StartsWith("trigger:",StringComparison.Ordinal))) sql.AppendLine(guard.Value+";");
        if(up) sql.AppendLine(Read("Guards.sql"));
        migrationBuilder.Sql("PRAGMA foreign_keys=0;",suppressTransaction:true); migrationBuilder.Sql(sql.ToString()); migrationBuilder.Sql("PRAGMA foreign_keys=1;",suppressTransaction:true);
    }
    private static string ReplaceCheck(string ddl,string name,string next)
    {
        var start=ddl.IndexOf('(',ddl.IndexOf("CHECK",ddl.IndexOf(name,StringComparison.Ordinal),StringComparison.Ordinal)); var depth=1; var quoted=false;
        for(var i=start+1;i<ddl.Length;i++)
        {
            if(ddl[i]=='\'') { if(quoted && i+1<ddl.Length && ddl[i+1]=='\'') { i++; continue; } quoted=!quoted; }
            if(quoted) continue;
            if(ddl[i]=='(') depth++; else if(ddl[i]==')' && --depth==0) return ddl[..(start+1)]+next+ddl[i..];
        }
        throw new InvalidOperationException("Frozen predecessor constraint is unbalanced.");
    }
}
