namespace AgentCore.Infrastructure.Persistence.Migrations;

// Frozen forward-cutover contract; historical migration formulas remain unchanged.
public partial class CanonicalModelIdentity
{
    internal static string ApprovalValuesConstraint => FrozenMigrationMetadata.Read("f4fdf7512e8065d97a1b4669a464e99b2fba5b03dff4a04339b5d95df352e22d");
}
