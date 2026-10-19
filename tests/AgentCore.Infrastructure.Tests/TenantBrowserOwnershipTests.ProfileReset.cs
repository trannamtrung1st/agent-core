using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserOwnershipTests
{
    [Theory] [InlineData("lock")] [InlineData("symlink")] [InlineData("refuse")] [InlineData("unproven")] [InlineData("delete")]
    public async Task Tenant_profile_reset_checks_exact_filesystem_lease_authority_and_actual_absence(string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-profile-boundary-" + Guid.NewGuid().ToString("N"));
        var owner = Owner(); var directory = Path.Combine(root, "tenants", owner.TenantId.ToString("D"), "agents", owner.AgentInstanceId.ToString("D"));
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "sentinel"), "preserve external content");
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "state"), "private state");
        Directory.CreateSymbolicLink(Path.Combine(directory, "linked-child"), outside);
        var browser = new NativePlaywrightBrowser(new() { Enabled = false, ProfileRoot = root }, null);
        FileStream? lease = null;
        try
        {
            var view = await browser.ReadTenantProfileAsync(owner.Agent); Assert.True(view.ProfilePresent);
            if (boundary == "lock") lease = new FileStream(directory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (boundary == "symlink") browser.TenantProfileBeforeDeleteProbe = () => { Directory.Move(directory, directory + "-saved"); Directory.CreateSymbolicLink(directory, outside); return Task.CompletedTask; };
            if (boundary == "unproven") browser.TenantProfileDeleteProbe = _ => { };
            var begins = 0; var authorizations = 0;
            var result = await browser.ResetTenantProfileAsync(owner.Agent, Guid.NewGuid(), view.Witness, (_, _) => { begins++; return ValueTask.FromResult(true); }, _ => { authorizations++; return ValueTask.FromResult(boundary != "refuse"); });
            Assert.Equal(boundary == "delete" ? "reset" : "unknown", result.Status); Assert.Equal(1, begins);
            Assert.Equal(boundary is "lock" or "symlink" ? 0 : 1, authorizations);
            Assert.Equal("preserve external content", File.ReadAllText(Path.Combine(outside, "sentinel")));
            if (boundary is "lock" or "refuse" or "unproven") Assert.Equal("private state", File.ReadAllText(Path.Combine(directory, "state")));
            if (boundary == "delete") { Assert.False(Directory.Exists(directory)); Assert.True(File.Exists(directory + ".lock")); }
        }
        finally { lease?.Dispose(); await browser.StopAsync(CancellationToken.None); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Tenant_profile_reset_preflight_seals_owner_epoch_and_busy_operation_without_creating_a_profile()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-profile-preflight-" + Guid.NewGuid().ToString("N")); var owner = Owner();
        var browser = new NativePlaywrightBrowser(new() { Enabled = false, ProfileRoot = root }, null);
        var otherHost = new NativePlaywrightBrowser(new() { Enabled = false, ProfileRoot = root }, null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var view = await browser.ReadTenantProfileAsync(owner.Agent); Assert.False(view.ProfilePresent); Assert.False(Directory.Exists(root));
            var starts = 0; var stale = await otherHost.ResetTenantProfileAsync(owner.Agent, Guid.NewGuid(), view.Witness,
                (_, _) => { starts++; return ValueTask.FromResult(true); }, _ => ValueTask.FromResult(true));
            Assert.Equal("not_started", stale.Status); Assert.Equal(0, starts);
            browser.TenantProfileBeforeDeleteProbe = async () => { entered.SetResult(); await release.Task; };
            var operation = Guid.NewGuid(); var running = browser.ResetTenantProfileAsync(owner.Agent, operation, view.Witness,
                (_, _) => { starts++; return ValueTask.FromResult(true); }, _ => ValueTask.FromResult(false)).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var busy = await browser.ReadTenantProfileAsync(owner.Agent); Assert.True(busy.Busy); Assert.Equal(operation, busy.InFlightOperation); Assert.Equal("", busy.Witness);
            var duplicate = await browser.ResetTenantProfileAsync(owner.Agent, Guid.NewGuid(), view.Witness,
                (_, _) => throw new InvalidOperationException("Cannot begin concurrent reset."), _ => ValueTask.FromResult(true));
            Assert.Equal("browser_profile_busy", duplicate.ReasonCode);
            await Assert.ThrowsAsync<AccessDeniedException>(() => browser.ReadTenantProfileAsync(owner.Agent with { TenantId = Guid.NewGuid() }).AsTask());
            release.SetResult(); Assert.Equal("unknown", (await running).Status); Assert.Equal(1, starts);
            Assert.Equal(operation, (await browser.ReadTenantProfileAsync(owner.Agent)).LastResult!.OperationId);
        }
        finally { release.TrySetResult(); await browser.StopAsync(CancellationToken.None); await otherHost.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData("root")] [InlineData("agent")] [InlineData("lock")]
    public async Task Tenant_profile_reset_refuses_linked_paths_before_deletion(string link)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-profile-link-" + Guid.NewGuid().ToString("N")); var owner = Owner();
        var profileRoot = Path.Combine(root, "profiles"); var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        var directory = Path.Combine(profileRoot, "tenants", owner.TenantId.ToString("D"), "agents", owner.AgentInstanceId.ToString("D"));
        var browser = new NativePlaywrightBrowser(new() { Enabled = false, ProfileRoot = profileRoot }, null);
        try
        {
            var view = await browser.ReadTenantProfileAsync(owner.Agent);
            if (link == "root") Directory.CreateSymbolicLink(profileRoot, outside);
            else { Directory.CreateDirectory(Path.GetDirectoryName(directory)!); if (link == "agent") Directory.CreateSymbolicLink(directory, outside); else File.CreateSymbolicLink(directory + ".lock", Path.Combine(outside, "lock")); }
            var starts = 0;
            if (link == "lock")
            {
                var result = await browser.ResetTenantProfileAsync(owner.Agent, Guid.NewGuid(), view.Witness, (_, _) => { starts++; return ValueTask.FromResult(true); }, _ => throw new InvalidOperationException());
                Assert.Equal("unknown", result.Status); Assert.Equal(1, starts);
            }
            else await Assert.ThrowsAsync<IOException>(() => browser.ResetTenantProfileAsync(owner.Agent, Guid.NewGuid(), view.Witness, (_, _) => { starts++; return ValueTask.FromResult(true); }, _ => ValueTask.FromResult(true)).AsTask());
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally { await browser.StopAsync(CancellationToken.None); Directory.Delete(root, true); }
    }
}
