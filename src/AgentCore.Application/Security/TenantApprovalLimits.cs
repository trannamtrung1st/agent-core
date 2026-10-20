namespace AgentCore.Application.Security;

// Pending requests stay charged until terminal. Terminal requests use a rolling window;
// the root ceiling lasts for the actual activation lineage, never the chat Session.
public static class TenantApprovalLimits
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public const int Tenant = 128;
    public const int Executor = 32;
    public const int Requester = 32;
    public const int Operation = 8;
    public const int Root = 32;

    public static void Require(int tenant, int executor, int requester, int operation, int root, bool hasHumanRequester = true)
    {
        if (root >= Root) throw new SecurityConflictException("approval_root_limit");
        if (tenant >= Tenant || executor >= Executor || hasHumanRequester && requester >= Requester || operation >= Operation)
            throw new SecurityConflictException("approval_rate_limited");
    }
}
