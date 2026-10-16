using AgentCore.Domain.Conversation;
using OpenSandbox.Models;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private async Task<Execution> GuardCommandStorageAsync(SandboxLease lease, Func<CancellationToken, Task<Execution>> command, CancellationToken ct, CanonicalSandboxMountResolver.Mounts? verification = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? limitFailure = null;
        async Task Watch()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), time, lifetime.Token);
                    await Check(lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { limitFailure = error; await lifetime.CancelAsync(); }
        }
        async ValueTask Check(CancellationToken token)
        {
            if (verification is null) await CheckMountedAuthorityAsync(lease, token);
            CanonicalSandboxMountResolver.CheckUsage(verification ?? ResolveMounts(lease), lease.Profile.StorageBytes, token);
        }
        var watcher = Watch();
        try
        {
            await Check(ct);
            var result = await command(lifetime.Token);
            if (limitFailure is not null) throw limitFailure;
            await Check(ct);
            return result;
        }
        catch (OperationCanceledException) when (limitFailure is not null) { throw limitFailure; }
        finally { await lifetime.CancelAsync(); await watcher; }
    }
}
