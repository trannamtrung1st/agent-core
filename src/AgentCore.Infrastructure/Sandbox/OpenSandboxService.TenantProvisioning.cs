using System.Globalization;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using OpenSandbox.Models;
using OpenSandbox;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private async ValueTask<SandboxLease> ProvisionApprovedGenerationAsync(TenantRuntimeFrame frame, SandboxLease lease, SandboxExecution receipt, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(lease.ExpiresAt - time.GetUtcNow(), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;
        async ValueTask Require(CancellationToken check) { await TenantExecution.RequireAsync(frame, receipt.Id, check); }
        var watcher = Watch();
        async Task Watch()
        {
            try
            {
                while (true) { await Task.Delay(TimeSpan.FromMilliseconds(250), time, token); await Require(token); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { await linked.CancelAsync(); throw; }
        }
        try
        {
            await Require(token);
            await (workspace ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(frame, receipt.Id, token);
            await Require(token);
            var mounted = MountResolver.Resolve(frame.Owner.AgentInstanceId, frame.SessionId,
                (await TenantExecution.RequireAsync(frame, receipt.Id, token)).Request.Operation.SandboxCommand!.WorkspaceAccess ?? throw new AccessDeniedException("sandbox_workspace_context_required"), create: true, ct: token);
            await Require(token);
            var imageAuth = await Images.ResolveAuthAsync(lease.Profile, token, requireVerification: true, Require);
            var image = OciImageReference.Parse(lease.Profile.ImageReference);
            if (!image.IsLocal) await RegistryAuthorizationProbe.CheckAsync(options, image, imageAuth, token);
            await Require(token);
            // Re-read secret epochs immediately before create, after the registry probe.
            imageAuth = await Images.ResolveAuthAsync(lease.Profile, token, requireVerification: true, Require);
            await Require(token);
            lease = await SaveAsync(lease with { TransitionPhase = "createDispatch" }, token);
            await Require(token);
            await using var sdk = await SdkSandbox.CreateAsync(new SandboxCreateOptions {
                ConnectionConfig = Connection(), Image = lease.Profile.ImageReference, ImageAuth = imageAuth,
                TimeoutSeconds = Math.Max(1, (int)Math.Ceiling((lease.ExpiresAt - time.GetUtcNow()).TotalSeconds)), ReadyTimeoutSeconds = 45,
                Resource = new Dictionary<string,string> { ["cpu"] = lease.Profile.Cpu.ToString(CultureInfo.InvariantCulture), ["memory"] = lease.Profile.MemoryMiB + "Mi" },
                Metadata = new Dictionary<string,string> { ["agent-core-generation"] = lease.Generation.ToString("N"),
                    ["agent-core-tenant"] = lease.TenantId!.Value.ToString("N"), ["agent-core-session"] = lease.SessionId.ToString("N"),
                    ["agent-core-executor"] = lease.ExecutorPrincipalId!.Value.ToString("N"),
                    ["agent-core-filesystem-contract"] = SandboxProfileVerification.CurrentContractVersion.ToString(CultureInfo.InvariantCulture) },
                Volumes = mounted.Volumes, NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] }
            }, token);
            imageAuth = null;
            // Link the returned technical handle even if authority was revoked during create, so exact cleanup can retire it.
            lease = await SaveAsync(lease with { ProviderId = sdk.Id, TransitionPhase = "mountValidation" }, CancellationToken.None);
            await Require(token);
            if (!MatchesGeneration(lease, (await sdk.GetInfoAsync(token)).Metadata))
                throw new AccessDeniedException("sandbox_generation_unavailable");
            await Require(token);
            var translated = await AttestMountsAsync(sdk.Id, mounted, lease.Profile.ImageReference, "agent-core-generation", lease.Generation.ToString("N"), token);
            await Require(token);
            var authority = await CheckMountedReadinessAsync(sdk, mounted, translated, token);
            await Require(token);
            lease = await SaveAsync(lease with { MountAuthority = authority }, token);
            lease = await SaveAsync(lease with { BaselineProcesses = await ProcessIdentitiesAsync(sdk, token), TransitionPhase = "initialization" }, token);
            await Require(token);
            if (!string.IsNullOrWhiteSpace(lease.Profile.InitializationScript) || !string.IsNullOrWhiteSpace(lease.Profile.InstanceInitializationScript))
                lease = await InitializeLockedAsync(sdk, lease, token);
            else
                lease = await SaveAsync(lease with { Initialization = new("Succeeded", lease.Profile.Id, lease.Profile.Revision,
                    lease.Profile.InstanceRevision, time.GetUtcNow(), time.GetUtcNow(), 0) }, token);
            await Require(token);
            return await SaveAsync(lease with { InitializedAt = time.GetUtcNow() }, token);
        }
        finally { await linked.CancelAsync(); await watcher; }
    }
}
