namespace AgentCore.Infrastructure.Sandbox;

/// <summary>Fixed, nonsecret compatibility evidence; no provider output is reflected into an error.</summary>
internal static class SandboxImageReadiness
{
    internal const string Requirements = "Use an ordinary Linux image with /bin/sh and /proc. The platform supplies a writable /working mount; Python and a pre-created directory are not required. Check the platform mount configuration if scratch is unavailable.";
    internal const string Script = """
        printf 'agent-core-image-readiness-v1\n'
        status=0
        fail() { printf '%s\n' "$1"; status=1; }
        if [ ! -d /proc/self ]; then fail processUnavailable; fi
        if [ ! -d /working ] || [ ! -w /working ] || [ -L /working ]; then
          fail scratchUnavailable
        elif ! printf ready > /working/.agent-core-mount-probe; then
          fail scratchUnavailable
        fi
        if [ -e /var/run/docker.sock ]; then fail dockerSocket; fi
        printf 'complete\n'
        exit "$status"
        """;

    internal static string? Describe(string output, int? exitCode)
    {
        var lines = output.Trim().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (lines.Length < 2 || lines[0] != "agent-core-image-readiness-v1" || lines[^1] != "complete")
            throw new InvalidOperationException("Image readiness was not confirmed.");
        var reasons = lines[1..^1];
        if (exitCode != (reasons.Length == 0 ? 0 : 1) || reasons.Distinct(StringComparer.Ordinal).Count() != reasons.Length)
            throw new InvalidOperationException("Image readiness was not confirmed.");
        var problems = reasons.Select(reason => reason switch
        {
            "processUnavailable" => "Linux process inspection is unavailable",
            "scratchUnavailable" => "/working is missing, not writable or a symbolic link",
            "dockerSocket" => "a Docker socket is exposed",
            _ => throw new InvalidOperationException("Image readiness was not confirmed.")
        }).ToArray();
        return problems.Length == 0 ? null : "Image is not sandbox-compatible: " + string.Join("; ", problems) + ". " + Requirements;
    }
}
