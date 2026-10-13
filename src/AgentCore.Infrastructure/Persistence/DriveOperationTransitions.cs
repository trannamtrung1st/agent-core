using AgentCore.Application.Ports;
namespace AgentCore.Infrastructure.Persistence;

internal static class DriveOperationTransitions
{
    internal static bool Allowed(string current, string next) => current switch
    {
        "Planned" => next is "Dispatched" or "Rejected",
        "Dispatched" => next is "Dispatched" or "Indeterminate" or "Succeeded",
        "Indeterminate" => next is "Indeterminate" or "Succeeded",
        _ => false
    };
    internal static bool SameIntent(DriveOperation before, DriveOperation after) => before.Tool == after.Tool
        && before.CreatedAtUtc.ToUnixTimeMilliseconds() == after.CreatedAtUtc.ToUnixTimeMilliseconds()
        && Allowed(before.State, after.State) && (after.State != "Succeeded" || after.ResultJson is not null);
}
