namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class DecisionIntelligencePreferences
{
    // Frozen forward contract; no historical migration is rewritten.
    internal static string ApprovalValuesConstraint => FrozenMigrationMetadata.Read("1539861882a444217fe349234906baff71bc5570ca0b18ce9a284cd81ba41018");
}
