namespace AgentCore.Domain.Triggers;

public static class AutomationContextPins
{
    public static void Validate(TriggerOwner owner, Guid? space, Guid? environment)
    {
        if (space == Guid.Empty || environment == Guid.Empty || environment is not null && space is null
            || space is not null && owner.TenantId is null)
            throw new ArgumentException("Automation context requires a tenant Space and an optional Environment in that Space.");
    }
}
