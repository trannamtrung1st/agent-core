namespace AgentCore.Infrastructure.Sandbox;

/// <summary>Literal shell/argv execution with private creation defaults under the declared Core/workload identity.</summary>
internal static class SandboxCommandPermissions
{
    // Canonical /working and authorized /home use the same verified non-root UID/GID.
    // No cross-UID staging or world-writable permission workaround remains.
    // Pass owner text and argv as positional arguments, never interpolated shell source.
    internal static string[] Shell(string script) => ["/bin/sh", "-c", "umask 077; exec /bin/sh -c \"$1\"", "agent-core-command", script];
    internal static string[] Argv(IEnumerable<string> argv) => ["/bin/sh", "-c", "umask 077; exec \"$@\"", "agent-core-command", .. argv];
}
