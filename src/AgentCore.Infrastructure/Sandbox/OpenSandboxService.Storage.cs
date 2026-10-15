using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using OpenSandbox.Models;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private async Task<Execution> GuardCommandStorageAsync(SandboxLease lease, Func<CancellationToken, Task<Execution>> command, CancellationToken ct)
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
                    try { Mounts.CheckLiveUsage(lease.Generation, lease.Profile.StorageBytes, lifetime.Token); }
                    catch (IOException) { /* Concurrent ordinary writes: retry the bounded next sample. */ }
                    catch (AgentCoreException error) when (error.Code is "ValidationError" or "Conflict") { /* Replaced/inaccessible entries are never followed. Strict collection validates the settled tree. */ }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { limitFailure = error; await lifetime.CancelAsync(); }
        }
        var watcher = Watch();
        try
        {
            var result = await command(lifetime.Token);
            if (limitFailure is not null) throw limitFailure;
            return result;
        }
        catch (OperationCanceledException) when (limitFailure is not null) { throw limitFailure; }
        finally { await lifetime.CancelAsync(); await watcher; }
    }
}
