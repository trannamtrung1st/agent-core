using AgentCore.Application.Sessions;

namespace AgentCore.Api.Http;

internal static class WorkspaceQuery
{
    internal static void Query(HttpContext http, params string[] allowed)
    {
        if (http.Request.Query.Any(q => !allowed.Contains(q.Key, StringComparer.Ordinal) || q.Value.Count != 1))
            throw AgentCoreErrors.Validation("Unknown or repeated workspace query parameter.");
    }
}
