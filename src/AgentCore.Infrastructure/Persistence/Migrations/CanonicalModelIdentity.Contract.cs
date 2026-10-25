namespace AgentCore.Infrastructure.Persistence.Migrations;

// Frozen forward-cutover contract; historical migration formulas remain unchanged.
public partial class CanonicalModelIdentity
{
    internal static string ApprovalValuesConstraint => FrozenMigrationMetadata.Read("c4df2732fcbe10af08be4c816bf2b7ab674d77a77fdf9c3ad1f12a7169211a92");
}
