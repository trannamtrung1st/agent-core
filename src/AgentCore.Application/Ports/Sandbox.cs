using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public sealed record SandboxRequest(
    Guid SessionId,
    Guid RunId,
    AgentDefinition Definition,
    string Verb,
    IReadOnlyList<string> Arguments,
    string? ExportLogicalPath = null);

public sealed record SandboxResult(
    bool Succeeded,
    int ExitCode,
    string Output,
    Guid? ArtifactId,
    string SafeMessage,
    bool Truncated = false);

public interface ISandboxExecutor
{
    ValueTask<SandboxResult> RunAsync(SandboxRequest request, CancellationToken cancellationToken = default);
}

public sealed class UnavailableSandboxExecutor : ISandboxExecutor
{
    public ValueTask<SandboxResult> RunAsync(SandboxRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SandboxResult(
            false,
            ExitCode: -1,
            Output: "",
            ArtifactId: null,
            "Container sandbox is unavailable."));
    }
}
