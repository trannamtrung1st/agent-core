using System.Globalization;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using OpenSandbox;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    public async ValueTask<SandboxProfileVerification> VerifyProfileAsync(SandboxProfile profile, CancellationToken ct, bool imageOnly = false)
    {
        var started = time.GetUtcNow(); var stdout = new BoundedText(32768); var stderr = new BoundedText(32768);
        var authority = await Images.InspectAsync(profile, ct, requireVerification: false);
        SandboxProfileVerification Observation(bool passed, string message, string? code = null, int? exit = null) =>
            new(profile.Id, profile.Revision, time.GetUtcNow(), passed, message, code, exit, stdout.Text, stderr.Text, stdout.Truncated || stderr.Truncated,
                Math.Max(0,(long)(time.GetUtcNow()-started).TotalMilliseconds), passed ? null : Guid.CreateVersion7().ToString("D"), 1, profile.RegistryCredentialId, authority.CredentialRevision, authority.SecretRevision);
        if (authority.Status != "SourceAuthorized") return Observation(false, authority.Message, "sourceAuthority");
        if (!options.Enabled) return Observation(false, "OpenSandbox is disabled in host configuration.", "disabled");
        var budget = 45 + profile.MaxCommandSeconds + CommitGraceSeconds;
        if (!imageOnly && budget > profile.MaxLifetimeSeconds && (!string.IsNullOrWhiteSpace(profile.InitializationScript) || !string.IsNullOrWhiteSpace(profile.InstanceInitializationScript)))
            return Observation(false, "Maximum lifetime cannot cover initialization and validation. Increase it within the host ceiling.", "lifetime");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(budget));
        SdkSandbox? sdk = null;
        var result = Observation(false, "Private server or pinned image is unavailable. Check local server readiness.", "provisioning");
        var phase = "provisioning";
        try
        {
            var auth = await Images.ResolveAuthAsync(profile, deadline.Token, requireVerification: false);
            if (!OciImageReference.Parse(profile.ImageReference).IsLocal) await RegistryAuthorizationProbe.CheckAsync(options, OciImageReference.Parse(profile.ImageReference), auth, deadline.Token);
            var currentAuthority = await Images.InspectAsync(profile,deadline.Token,requireVerification:false);
            if (currentAuthority.Status != "SourceAuthorized" || currentAuthority.CredentialRevision != authority.CredentialRevision || currentAuthority.SecretRevision != authority.SecretRevision)
                throw AgentCoreErrors.Forbidden("Source authorization changed before provisioning. Read the current credential/profile state and verify again.");
            sdk = await SdkSandbox.CreateAsync(new SandboxCreateOptions { ConnectionConfig = Connection(), Image = profile.ImageReference, ImageAuth = auth,
                TimeoutSeconds = Math.Min(profile.MaxLifetimeSeconds, budget), ReadyTimeoutSeconds = 45,
                Resource = new Dictionary<string,string> { ["cpu"] = profile.Cpu.ToString(CultureInfo.InvariantCulture), ["memory"] = profile.MemoryMiB + "Mi" },
                Metadata = new Dictionary<string,string> { ["agent-core-image-verification"] = Guid.CreateVersion7().ToString("N") },
                NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] } }, deadline.Token);
            auth = null;
            phase = "readiness";
            var output = await InternalCommandAsync(sdk, ["python","-I","-c","import os; assert os.getuid()>0; assert not os.path.exists('/var/run/docker.sock'); assert os.access('/working',os.W_OK); print('verified')"], deadline.Token);
            if (output.Trim() != "verified") throw new InvalidOperationException();
            var lease = new SandboxLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, profile, SandboxLeaseState.Ready, sdk.Id,
                time.GetUtcNow(), time.GetUtcNow().AddSeconds(budget), BaselineProcesses: await ProcessIdentitiesAsync(sdk, deadline.Token));
            phase = "initialization";
            if (imageOnly) result = Observation(true, "Image readiness passed: non-root Python is available. Initialization was not executed.");
            else
            {
                var setupStarted = time.GetUtcNow();
                var failed = false;
                foreach (var (stage, script) in new[] { ("Profile", profile.InitializationScript), ("Instance", profile.InstanceInitializationScript) })
                {
                    if (string.IsNullOrWhiteSpace(script)) continue;
                    phase = "initialization";
                    var remaining = profile.MaxCommandSeconds - (time.GetUtcNow()-setupStarted).TotalSeconds;
                    if (remaining <= 0) throw new TimeoutException();
                    using var scriptDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    scriptDeadline.CancelAfter(TimeSpan.FromSeconds(remaining));
                    var initialized = await sdk.Commands.RunAsync(script,
                        new RunCommandOptions { WorkingDirectory = "/working", TimeoutSeconds = Math.Max(1,(int)Math.Ceiling(remaining)) },
                        new ExecutionHandlers { SkipAccumulation = true, OnStdout = m => { stdout.Append(m.Text); return Task.CompletedTask; },
                            OnStderr = m => { stderr.Append(m.Text); return Task.CompletedTask; } }, scriptDeadline.Token);
                    if (initialized.ExitCode != 0)
                    {
                        result = Observation(false, initialized.ExitCode is { } exit ? $"Initialization failed with exit code {exit} during {stage} setup. Correct the script and verify again."
                            : $"{stage} initialization termination was not confirmed. Inspect output and verify again.", "initialization", initialized.ExitCode);
                        failed = true; break;
                    }
                }
                if (!failed)
                {
                    phase = "validation";
                    await ConfirmQuiescentAsync(sdk, lease, deadline.Token);
                    _ = await CollectAsync(sdk, profile, deadline.Token);
                    result = Observation(true, "Non-root Python readiness, initialization and scratch validation passed in a disposable sandbox.", exit:0);
                }
            }
        }
        catch (OperationCanceledException)
        { result = Observation(false, ct.IsCancellationRequested ? "Verification was canceled; success was not confirmed." : $"Verification timed out during {phase}. Check the profile limits and script.", "canceledOrTimeout"); }
        catch (AgentCoreException error) when (phase == "provisioning")
        { result = Observation(false, error.Message, "sourceAuthorization"); }
        catch
        { result = Observation(false, $"Verification failed during {phase}. Check private server readiness, the pinned image and initialization script. Provider exception details are withheld; bounded script output is available separately.", phase); }
        finally
        {
            if (sdk is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await sdk.KillAsync(cleanup.Token); }
                catch { result = Observation(false, "Verification sandbox cleanup was not confirmed. Inspect the local server before verifying again.", "cleanup"); }
                finally { await sdk.DisposeAsync(); }
            }
        }
        return result;
    }
}
