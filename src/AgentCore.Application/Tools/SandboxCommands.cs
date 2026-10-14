using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tools;

public static class SandboxCommands
{
    public static void Validate(SandboxCommandRequest request)
    {
        if ((request.Command is not null) == (request.Argv is not null)
            || request.Command is { } command && (string.IsNullOrWhiteSpace(command) || !ValidText(command) || Encoding.UTF8.GetByteCount(command) > 16384)
            || request.Argv is { } argv && (argv.Count is < 1 or > 128 || argv.Any(a => a is null || !ValidText(a)) || string.IsNullOrWhiteSpace(argv[0]) || argv.Sum(a => Encoding.UTF8.GetByteCount(a)) > 16384)
            || request.TimeoutSeconds is < 1 or > 180
            || request.Cwd.Length > 512 || !(request.Cwd == "/working" || request.Cwd.StartsWith("/working/", StringComparison.Ordinal))
            || request.Cwd.Contains('\\') || !ValidText(request.Cwd) || request.Cwd[1..].Split('/').Any(p => p is "" or "." or ".."))
            throw AgentCoreErrors.Validation("Use one bounded command or argv, a /working directory and a 1–180 second timeout.");
    }
    private static bool ValidText(string value)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); return !value.Contains('\0'); }
        catch (EncoderFallbackException) { return false; }
    }
}
