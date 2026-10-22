namespace AgentCore.Infrastructure.Security;

internal static class ManagementChildReadApprovalPersistence
{
    internal static string Extend(string prior)
    {
        const string before = "'harness.inspect','configuration.inspect'";
        if (!prior.Contains(before, StringComparison.Ordinal)) throw new InvalidOperationException("Expected compound management read constraint.");
        return prior.Replace(before, before + ",'skills.list','skills.inspect','resources.list','resources.inspect'", StringComparison.Ordinal);
    }
}
