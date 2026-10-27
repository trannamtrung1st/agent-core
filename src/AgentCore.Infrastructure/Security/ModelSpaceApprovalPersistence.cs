namespace AgentCore.Infrastructure.Security;

// Preserve the complete Space approval shape while using canonical model identities.
internal static class ModelSpaceApprovalPersistence
{
    internal static string ValuesConstraint => Persistence.Migrations.TaskApprovalSchema.Values;
}
