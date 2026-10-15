namespace AgentCore.Infrastructure.Sandbox;

/// <summary>New scratch directories must remain removable by Core when the image uses another UID.</summary>
internal static class SandboxCommandPermissions
{
    // Only /working is mounted, behind the private 0700 generation parent.
    // Pass owner text and argv as positional arguments, never interpolated shell source.
    internal static string[] Shell(string script) => ["/bin/sh", "-c", "umask 000; exec /bin/sh -c \"$1\"", "agent-core-command", script];
    internal static string[] Argv(IEnumerable<string> argv) => ["/bin/sh", "-c", "umask 000; exec \"$@\"", "agent-core-command", .. argv];
}
