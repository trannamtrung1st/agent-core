namespace AgentCore.Infrastructure.Persistence.Migrations;
// Exact generated target-model SQL; moving the literal changes no historical Up/Down statement.
public partial class TenantExperienceConsent
{
    internal static string ValuesConstraint
    {
        get
        {
            using var stream = typeof(TenantExperienceConsent).Assembly.GetManifestResourceStream(
                "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261021214545_TenantExperienceConsent.Check.sql")
                ?? throw new InvalidOperationException("Frozen historical Experience consent model SQL is missing.");
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
