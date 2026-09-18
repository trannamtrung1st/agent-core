using System.Diagnostics;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Sandbox;

public sealed class DockerSandboxExecutor(
    ISessionWorkspace workspace,
    IArtifactStore? artifacts = null,
    string dockerPath = "docker",
    string image = SandboxLimits.Image) : ISandboxExecutor
{
    public string? LastHostConfigJson { get; private set; }
    public string? LastMountsJson { get; private set; }
    public async ValueTask<SandboxResult> RunAsync(
        SandboxRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workspace is not FileSessionWorkspace files)
        {
            return Fail("Sandbox requires a file session workspace.");
        }

        if (!SandboxCommand.TryNormalize(request.Verb, request.Arguments, out var argv, out var error))
        {
            return Fail(error);
        }

        RolePermissions.EnsureLogicalPathAllowed("/workspace/working", request.SessionId);
        await files.EnsureAsync(request.SessionId, request.Definition, cancellationToken).ConfigureAwait(false);
        var working = files.PhysicalWorkingDirectory(request.SessionId);
        Directory.CreateDirectory(working);

        var name = $"acsbx-{request.RunId:N}";
        LastHostConfigJson = null;
        LastMountsJson = null;
        try
        {
            await DockerAsync(
                    [
                        "create",
                        "--name", name,
                        "--network", "none",
                        "--read-only",
                        "--user", "65534:65534",
                        "--cap-drop", "ALL",
                        "--security-opt", "no-new-privileges",
                        "--memory", "64m",
                        "--cpus", "0.5",
                        "--pids-limit", SandboxLimits.PidsLimit.ToString(),
                        "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
                        "--label", $"{SandboxLimits.LabelKey}=1",
                        "--label", $"{SandboxLimits.SessionLabelKey}={request.SessionId:N}",
                        "--mount", $"type=bind,source={working},destination=/workspace/working,readonly=false",
                        image,
                        .. argv
                    ],
                    cancellationToken)
                    .ConfigureAwait(false);

            LastHostConfigJson = (await DockerAsync(
                    ["inspect", "-f", "{{json .HostConfig}}", name],
                    cancellationToken)
                .ConfigureAwait(false)).Output;
            LastMountsJson = (await DockerAsync(
                    ["inspect", "-f", "{{json .Mounts}}", name],
                    cancellationToken)
                .ConfigureAwait(false)).Output;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(SandboxLimits.Timeout);
            string output;
            int exit;
            var truncated = false;
            try
            {
                var started = await DockerAsync(["start", "-a", name], linked.Token, SandboxLimits.MaxOutputBytes)
                    .ConfigureAwait(false);
                output = started.Output;
                truncated = started.Truncated;
                exit = truncated
                    ? await ReadContainerExitCodeAsync(name, linked.Token).ConfigureAwait(false)
                    : started.ExitCode;
            }
            catch (OperationCanceledException)
            {
                await TryDockerAsync(["kill", name], CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            Guid? artifactId = null;
            if (request.ExportLogicalPath is { } export && artifacts is not null)
            {
                RolePermissions.EnsureLogicalPathAllowed(export, request.SessionId);
                if (!export.StartsWith("/workspace/working/", StringComparison.Ordinal))
                {
                    return Fail("Sandbox export is limited to /workspace/working.");
                }

                var content = await files.ReadAsync(request.SessionId, request.Definition, export, cancellationToken)
                    .ConfigureAwait(false);
                var created = await artifacts.CreateAsync(
                        request.SessionId,
                        Path.GetFileName(export),
                        content.ContentType,
                        content.Bytes,
                        sourceAttachmentId: null,
                        export,
                        cancellationToken)
                    .ConfigureAwait(false);
                artifactId = created.ArtifactId;
            }

            return new SandboxResult(
                exit == 0,
                exit,
                output,
                artifactId,
                exit == 0 ? "ok" : "Sandbox command failed.",
                truncated);
        }
        finally
        {
            await TryDockerAsync(["rm", "-f", name], CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<int> ReadContainerExitCodeAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var waited = await DockerAsync(["wait", name], cancellationToken).ConfigureAwait(false);
            if (int.TryParse(waited.Output.Trim(), out var waitedCode))
            {
                return waitedCode;
            }
        }
        catch (OperationCanceledException)
        {
            await TryDockerAsync(["kill", name], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fall through to inspect.
        }

        try
        {
            var inspect = await DockerAsync(
                    ["inspect", "-f", "{{.State.ExitCode}}", name],
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (int.TryParse(inspect.Output.Trim(), out var inspected))
            {
                return inspected;
            }
        }
        catch (Exception)
        {
            // The attach-client status is not the container's exit code.
        }

        return -1;
    }

    private Task<DockerExec> DockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        int maxOutput = 32 * 1024) =>
        DockerStaticAsync(arguments, cancellationToken, dockerPath, maxOutput);

    private async Task TryDockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            await DockerAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup must not throw past the original result.
        }
    }

    private static async Task<DockerExec> DockerStaticAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string docker = "docker",
        int maxOutput = 32 * 1024)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = docker,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start docker.");
        }

        BoundedProcessOutput.BoundedRead combined;
        try
        {
            combined = await BoundedProcessOutput.ReadDetailedAsync(process, maxOutput, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
            }

            throw;
        }

        if (process.ExitCode != 0 && arguments.Count > 0 && arguments[0] is "create" or "inspect")
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(combined.Text) ? "docker failed." : combined.Text.Trim());
        }

        return new DockerExec(process.ExitCode, combined.Text, combined.Truncated);
    }

    private static SandboxResult Fail(string message) =>
        new(false, -1, "", null, message);

    private readonly record struct DockerExec(int ExitCode, string Output, bool Truncated);
}

public static class SandboxCommand
{
    public static bool TryNormalize(
        string verb,
        IReadOnlyList<string> arguments,
        out IReadOnlyList<string> argv,
        out string error)
    {
        argv = [];
        error = "";
        var name = verb.Trim().ToLowerInvariant();
        if (name is "echo")
        {
            foreach (var argument in arguments)
            {
                if (argument.StartsWith('-') && argument is not "-n"
                    || argument.Contains('`', StringComparison.Ordinal)
                    || argument.Contains('$', StringComparison.Ordinal)
                    || argument.Contains(';', StringComparison.Ordinal)
                    || argument.Contains('|', StringComparison.Ordinal)
                    || argument.Contains('&', StringComparison.Ordinal)
                    || argument.Contains('\n', StringComparison.Ordinal))
                {
                    error = "Sandbox echo arguments are not permitted.";
                    return false;
                }
            }

            argv = ["echo", .. arguments];
            return true;
        }

        if (name is "true" && arguments.Count == 0)
        {
            argv = ["true"];
            return true;
        }

        if (name is "cat" && arguments.Count == 1)
        {
            var path = arguments[0].Replace('\\', '/');
            if (!path.StartsWith("/workspace/working/", StringComparison.Ordinal)
                || path.Contains("..", StringComparison.Ordinal)
                || path.Contains('\0'))
            {
                error = "Sandbox cat is limited to /workspace/working.";
                return false;
            }

            argv = ["cat", path];
            return true;
        }

        if (name is "sleep" && arguments.Count == 1 && int.TryParse(arguments[0], out var seconds) && seconds is >= 1 and <= 5)
        {
            argv = ["sleep", seconds.ToString()];
            return true;
        }

        error = "Sandbox command is not permitted.";
        return false;
    }
}
