namespace AgentCore.Application.Tools;

/// <summary>Fixed vocabulary only; provider strings and historical package claims never enter this summary.</summary>
public static class SandboxFailureSummary
{
    public static string Describe(string? category, string? phase, string sync, bool terminated)
    {
        var cause = category switch {
            "cancellation" => "Sandbox execution was cancelled.",
            "commandTimeout" => "The sandbox command timed out.",
            "sourceAuthorizationChanged" => "Sandbox source authority changed.",
            "provisioningFailure" => "Sandbox provisioning failed.",
            "mountFailure" => "The platform workspace mount could not be verified.",
            "backgroundProcessesActive" => "Untracked child processes remained after the command; scratch files were not imported.",
            "processInspectionFailure" => "Process quiescence could not be confirmed; remaining child processes or inspection failure prevented file synchronization.",
            "unsafeFiles" => "Workload files failed safe collection checks.",
            "workspaceConflict" => "Canonical Workspace changes conflicted with sandbox synchronization.",
            "storageLimit" => "Live sandbox staging exceeded its bounded storage safeguard.",
            "uncertainCommit" => "The canonical file commit outcome requires inspection.",
            _ => "The sandbox execution outcome could not be confirmed."
        };
        var files = sync switch { "committed" => "Canonical files were committed.", "notCommitted" => "Canonical files were not committed.", _ => "Canonical file effects remain uncertain; inspect /working before an explicit reset." };
        return cause + " " + files + " " + (terminated ? "Termination and staging cleanup are confirmed." : "Remote termination or staging cleanup is pending.")
            + " No command was replayed. Inspect the Session sandbox receipt; unresolved outcomes require owner recovery. This evidence does not establish that an SDK or image is unavailable.";
    }
}
