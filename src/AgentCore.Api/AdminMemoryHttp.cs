using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;

namespace AgentCore.Api;

internal static class AdminMemoryHttp
{
    internal static AdminLearnedMemoryScope ParseScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw AgentCoreErrors.Validation("scope is required.");
        }

        if (!Enum.TryParse<AdminLearnedMemoryScope>(scope, ignoreCase: true, out var parsed))
        {
            throw AgentCoreErrors.Validation("scope is invalid.");
        }

        return parsed;
    }
}
