using AgentCore.Domain.Definitions;
using OpenSandbox;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    public async ValueTask<bool> VerifyProfileAsync(SandboxProfile profile, CancellationToken ct)
    {
        options.ValidateProfile(profile);
        if (!options.Enabled) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        SdkSandbox? sdk = null;
        var passed = false;
        try
        {
            sdk = await SdkSandbox.CreateAsync(new SandboxCreateOptions { ConnectionConfig = Connection(), Image = profile.ImageReference,
                TimeoutSeconds = 120, ReadyTimeoutSeconds = 45, Resource = new Dictionary<string,string> { ["cpu"] = "1", ["memory"] = "512Mi" },
                Metadata = new Dictionary<string,string> { ["agent-core-image-verification"] = Guid.CreateVersion7().ToString("N") },
                NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] } }, deadline.Token);
            var output = await InternalCommandAsync(sdk, ["python","-I","-c","import os; assert os.getuid()>0; assert not os.path.exists('/var/run/docker.sock'); print('verified')"], deadline.Token);
            passed = output.Trim() == "verified";
        }
        catch { passed = false; }
        finally
        {
            if (sdk is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await sdk.KillAsync(cleanup.Token); } catch { passed = false; }
                finally { await sdk.DisposeAsync(); }
            }
        }
        return passed;
    }
}
