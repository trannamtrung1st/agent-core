using AgentCore.Application.Ports;
namespace AgentCore.Infrastructure.Persistence;

internal static class DriveOperationTransitions
{
    internal static bool Allowed(string current, string next) => current switch
    {
        "Planned" => next is "Dispatched" or "Rejected",
        "Dispatched" => next is "Dispatched" or "Indeterminate" or "Succeeded" or "Failed",
        "Indeterminate" => next is "Indeterminate" or "Succeeded" or "Failed",
        _ => false
    };
    internal static bool SameIntent(DriveOperation before, DriveOperation after) => before.Tool == after.Tool
        && before.CreatedAtUtc.ToUnixTimeMilliseconds() == after.CreatedAtUtc.ToUnixTimeMilliseconds()
        && Allowed(before.State, after.State) && (after.State is not ("Succeeded" or "Failed") || after.ResultJson is not null)
        && (after.State != "Failed" || HasNonExecutionProof(after));
    private static bool HasNonExecutionProof(DriveOperation operation)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<DriveOperationFailure>(operation.ResultJson!)
            is { Proof: "initial_request_not_executed", Code.Length: > 0, Message.Length: > 0 }; }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
