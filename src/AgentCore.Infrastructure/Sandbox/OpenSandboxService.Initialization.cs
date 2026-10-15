using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private async ValueTask<SandboxLease> InitializeLockedAsync(SdkSandbox sdk, SandboxLease lease, SandboxAccess access, CancellationToken ct)
    {
        var evidence = new SandboxInitializationEvidence("Running", lease.Profile.Id, lease.Profile.Revision, lease.Profile.InstanceRevision, time.GetUtcNow());
        lease = await SaveAsync(lease with { Initialization = evidence }, ct);
        var stdout = new BoundedText(32768); var stderr = new BoundedText(32768);
        var lastSave = time.GetUtcNow().AddSeconds(-1);
        async Task SaveOutputAsync()
        {
            if (time.GetUtcNow() - lastSave < TimeSpan.FromSeconds(1)) return;
            lastSave = time.GetUtcNow();
            evidence = evidence with { Stdout = stdout.Text, Stderr = stderr.Text, Truncated = stdout.Truncated || stderr.Truncated };
            lease = await SaveAsync(lease with { Initialization = evidence }, ct);
        }
        try
        {
            foreach (var (phase, script) in new[] { ("Profile", lease.Profile.InitializationScript), ("Instance", lease.Profile.InstanceInitializationScript) })
            {
                if (string.IsNullOrWhiteSpace(script)) continue;
                evidence = evidence with { Phase = phase }; lease = await SaveAsync(lease with { Initialization = evidence }, ct);
                // The combined setup has one profile-governed execution budget.
                var remaining = lease.Profile.MaxCommandSeconds - (time.GetUtcNow() - evidence.StartedAt!.Value).TotalSeconds;
                if (remaining <= 0) throw new TimeoutException();
                using var scriptDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                scriptDeadline.CancelAfter(TimeSpan.FromSeconds(remaining));
                var result = await GuardCommandStorageAsync(lease, token => sdk.Commands.RunAsync(SandboxCommandPermissions.Shell(script), CommandOptions(lease, "/working", Math.Max(1, (int)Math.Ceiling(remaining))),
                    new ExecutionHandlers { SkipAccumulation = true,
                        OnStdout = async m => { stdout.Append(m.Text); await SaveOutputAsync(); },
                        OnStderr = async m => { stderr.Append(m.Text); await SaveOutputAsync(); } }, token), scriptDeadline.Token);
                evidence = evidence with { ExitCode = result.ExitCode };
                if (result.ExitCode != 0) throw new InvalidOperationException("Initialization failed.");
            }
            evidence = evidence with { Phase = "validation" };
            await ConfirmQuiescentAsync(sdk, lease, ct);
            var finished = time.GetUtcNow();
            evidence = evidence with { State = "Succeeded", FinishedAt = finished, ExitCode = 0, Stdout = stdout.Text, Stderr = stderr.Text,
                Truncated = stdout.Truncated || stderr.Truncated, DurationMs = Math.Max(0,(long)(finished-evidence.StartedAt!.Value).TotalMilliseconds) };
            return await SaveAsync(lease with { Initialization = evidence }, CancellationToken.None);
        }
        catch
        {
            var finished = time.GetUtcNow();
            evidence = evidence with { State = "Failed", FinishedAt = finished, Stdout = stdout.Text, Stderr = stderr.Text,
                Truncated = stdout.Truncated || stderr.Truncated, DurationMs = Math.Max(0,(long)(finished-evidence.StartedAt!.Value).TotalMilliseconds), DiagnosticId = Guid.CreateVersion7().ToString("D") };
            // If persistence fails, the durable Running observation remains an
            // uncertainty fence. Reconciliation never reruns setup for this generation.
            try { await SaveAsync(lease with { Initialization = evidence, CleanupReason = "initializationFailed" }, CancellationToken.None); } catch { }
            throw;
        }
    }
}
