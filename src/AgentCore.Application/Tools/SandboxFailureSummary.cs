namespace AgentCore.Application.Tools;

public static class SandboxFailureSummary
{
    public static string Describe(string? category, string? phase, bool effectsMayHaveOccurred, bool terminated)
    {
        var cause = category switch {
            "cancellation" => "Sandbox execution was cancelled.",
            "commandTimeout" => "The sandbox command timed out.",
            "sourceAuthorizationChanged" => "Sandbox authority changed.",
            "provisioningFailure" => "Sandbox provisioning failed.",
            "mountFailure" or "MountIncompatible" => "Canonical mount access or Unix identity could not be verified.",
            "backgroundProcessesActive" => "Untracked child processes remained after the command.",
            "processInspectionFailure" => "Process quiescence could not be confirmed.",
            "storageLimit" => "Sampled canonical storage usage exceeded its configured budget.",
            _ => "The sandbox execution outcome could not be confirmed."
        };
        return cause + (effectsMayHaveOccurred ? " Complete or partial writes may remain in /home and /working; inspect present files." : " No command dispatch was observed.")
            + (terminated ? " Container termination is confirmed." : " Container termination remains unconfirmed; writes may continue.")
            + " No command was replayed. Inspect the Session receipt and resolve process cleanup before further execution.";
    }
}
