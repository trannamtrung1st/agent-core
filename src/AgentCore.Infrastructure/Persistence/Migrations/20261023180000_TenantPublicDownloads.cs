using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261023180000_TenantPublicDownloads")]
public sealed class TenantPublicDownloads : RetireEmbeddedRunApproval
{
    // Extend the frozen predecessor target rather than duplicating its unchanged model.
    internal const string ReceiptConstraint = "PublicDownloadJson IS NULL OR COALESCE(length(PublicDownloadJson)<=32768 AND json_valid(PublicDownloadJson) AND Outcome=0 AND json_type(PublicDownloadJson,'$.RequestedUrl')='text' AND length(json_extract(PublicDownloadJson,'$.RequestedUrl')) BETWEEN 1 AND 4096 AND json_type(PublicDownloadJson,'$.FinalUrl')='text' AND length(json_extract(PublicDownloadJson,'$.FinalUrl')) BETWEEN 1 AND 4096 AND json_type(PublicDownloadJson,'$.Path')='text' AND (json_extract(PublicDownloadJson,'$.Path') LIKE '/home/%' OR json_extract(PublicDownloadJson,'$.Path') LIKE '/working/%') AND json_type(PublicDownloadJson,'$.ContentType')='text' AND length(json_extract(PublicDownloadJson,'$.ContentType')) BETWEEN 1 AND 200 AND json_type(PublicDownloadJson,'$.ByteCount')='integer' AND json_extract(PublicDownloadJson,'$.ByteCount') BETWEEN 0 AND 2147483647 AND json_type(PublicDownloadJson,'$.Sha256')='text' AND length(json_extract(PublicDownloadJson,'$.Sha256'))=64 AND json_extract(PublicDownloadJson,'$.Sha256') NOT GLOB '*[^0-9a-f]*',0)";
    private const string PriorTools = "'workspace.write','workspace.patch','workspace.mkdir','workspace.copy','workspace.move','workspace.delete','workspace.batch','office.document.create','office.document.edit','office.spreadsheet.create','office.spreadsheet.edit'";
    internal new static string ValuesConstraint => Extend(TenantConfigurationRemedyAuthoring.ValuesConstraint);
    private static string Extend(string sql) => sql.Replace(PriorTools, PriorTools + ",'web.download'", StringComparison.Ordinal)
        .Replace("WHEN 'workspace.copy' THEN", "WHEN 'web.download' THEN coalesce(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.path'), '/home/downloads')\n        WHEN 'workspace.copy' THEN", StringComparison.Ordinal);
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        modelBuilder.Entity("AgentCore.Infrastructure.Security.TenantApprovalRecord", entity => entity.ToTable("TenantApprovalRequests", table => table.HasCheckConstraint("CK_TenantApprovalRequest_Values", ValuesConstraint)));
        modelBuilder.Entity("AgentCore.Infrastructure.Security.TenantOperationReceiptRecord", entity => {
            entity.Property<string>("PublicDownloadJson").HasColumnType("TEXT");
            entity.ToTable("TenantOperationReceipts", table => table.HasCheckConstraint("CK_TenantOperationReceipt_PublicDownload", ReceiptConstraint));
        });
    }
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, true);
    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, false);

    private static string Read(string name, string digest = null)
    {
        using var stream = typeof(TenantPublicDownloads).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql." + name)
            ?? throw new InvalidOperationException("Frozen public-download predecessor is missing.");
        using var reader = new StreamReader(stream); var text = reader.ReadToEnd();
        if (digest is not null && Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))) != digest) throw new InvalidOperationException("Frozen public-download predecessor changed.");
        return text;
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, bool up)
    {
        // Compose existing frozen predecessors, without duplicating their large unchanged schemas.
        var previous = JsonSerializer.Deserialize<Dictionary<string,string>>(Read("20261023140000_TenantConfigurationRemedyAuthoring.Previous.json", "616905a52b832d8286088245602c65accbf83760a361bf0db2522e791e4d3826"))!;
        var guards = previous.Where(p => p.Key.StartsWith("trigger:", StringComparison.Ordinal)).ToDictionary(p => p.Key[8..], p => p.Value);
        foreach (var part in Regex.Split(Read("20261023140000_TenantConfigurationRemedyAuthoring.Guards.sql"), @"(?m)(?=^CREATE TRIGGER )").Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var name = Regex.Match(part, @"^CREATE TRIGGER (\w+)").Groups[1].Value;
            if (name.Length == 0) throw new InvalidOperationException("Invalid frozen configuration guard.");
            guards[name] = part.Trim().TrimEnd(';');
        }
        foreach (var guard in JsonSerializer.Deserialize<Dictionary<string,string>>(Read("20261023160000_RetireEmbeddedRunApproval.Guards.json", "b665d21d53c260211662c5c72fbb815a3fd012e3d53c8d23bf6b828fa8fd8eaa"))!) guards[guard.Key] = guard.Value;
        var sql = new StringBuilder();
        if (!up) migrationBuilder.Sql("""
            CREATE TEMP TABLE PublicDownloadDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO PublicDownloadDowngradeGuard SELECT
                (SELECT count(*) FROM TenantOperationReceipts WHERE PublicDownloadJson IS NOT NULL)
                + (SELECT count(*) FROM TenantApprovalRequests WHERE json_extract(PayloadJson,'$.Operation.WorkspaceMutation.ToolName')='web.download')
                + (SELECT count(*) FROM AgentRuns WHERE instr(PayloadJson,'web.download')>0);
            DROP TABLE PublicDownloadDowngradeGuard;
            """);
        foreach (var name in guards.Keys) sql.AppendLine("DROP TRIGGER " + name + ";");
        var ddl = ReplaceCheck(previous["table:TenantApprovalRequests"], up ? ValuesConstraint : TenantConfigurationRemedyAuthoring.ValuesConstraint);
        var tableName = ddl.IndexOf("TenantApprovalRequests", StringComparison.Ordinal);
        sql.AppendLine(ddl[..tableName] + "__download_TenantApprovalRequests" + ddl[(tableName + "TenantApprovalRequests".Length)..] + ";");
        sql.AppendLine("INSERT INTO __download_TenantApprovalRequests SELECT * FROM TenantApprovalRequests; DROP TABLE TenantApprovalRequests; ALTER TABLE __download_TenantApprovalRequests RENAME TO TenantApprovalRequests;");
        foreach (var index in previous.Where(p => p.Key.StartsWith("index:", StringComparison.Ordinal) && p.Value.Contains(" ON \"TenantApprovalRequests\"", StringComparison.Ordinal))) sql.AppendLine(index.Value.TrimEnd(';') + ";");
        sql.AppendLine(up ? "ALTER TABLE TenantOperationReceipts ADD COLUMN PublicDownloadJson TEXT NULL CONSTRAINT CK_TenantOperationReceipt_PublicDownload CHECK (" + ReceiptConstraint + ");" : "ALTER TABLE TenantOperationReceipts DROP COLUMN PublicDownloadJson;");
        foreach (var guard in guards.Values) sql.AppendLine((up ? Extend(guard) : guard).TrimEnd().TrimEnd(';') + ";");
        migrationBuilder.Sql("PRAGMA foreign_keys=0;", suppressTransaction:true);
        migrationBuilder.Sql(sql.ToString());
        migrationBuilder.Sql("PRAGMA foreign_keys=1;", suppressTransaction:true);
    }
    private static string ReplaceCheck(string ddl, string next)
    {
        var start = ddl.IndexOf('(', ddl.IndexOf("CHECK", ddl.IndexOf("CK_TenantApprovalRequest_Values", StringComparison.Ordinal), StringComparison.Ordinal));
        var depth = 1; var quoted = false;
        for (var i = start + 1; i < ddl.Length; i++)
        {
            if (ddl[i] == '\'') { if (quoted && i + 1 < ddl.Length && ddl[i + 1] == '\'') { i++; continue; } quoted = !quoted; }
            if (quoted) continue;
            if (ddl[i] == '(') depth++; else if (ddl[i] == ')' && --depth == 0) return ddl[..(start + 1)] + next + ddl[i..];
        }
        throw new InvalidOperationException("Frozen approval constraint is unbalanced.");
    }
}
