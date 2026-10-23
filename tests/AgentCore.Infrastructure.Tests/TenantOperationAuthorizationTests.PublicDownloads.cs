using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_public_download_effect_reuses_exact_receipt_without_a_second_transfer(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority:true); var v = await WorkspaceEffectCase(f);
        var native = new DelayedTenantDownload(false); var service = WorkspaceEffects(f, downloads:native);
        var call = new ModelToolCall("exact-public-file", ToolCatalog.WebDownload, """{"url":"https://example.test/source.bin","path":"/working/download.bin"}""");
        await WorkspaceEffectCheckpoint(f, v.Frame, call);
        var first = await service.ExecuteAsync(v.Frame, call); Assert.Equal(TenantOperationOutcome.Completed, first.Receipt!.Outcome);
        Assert.Equal(first, await service.ExecuteAsync(v.Frame, call)); Assert.Equal(1, native.Count);
        Assert.Equal(new byte[] {0, 255, 42}, (await WorkspaceReader(f).InspectTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working/download.bin", 100)).Bytes);
        Assert.NotNull(first.Receipt.PublicDownload); Assert.DoesNotContain(Convert.ToBase64String(new byte[] {0,255,42}), JsonSerializer.Serialize(first.Receipt));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_public_download_effect_settles_a_noncooperative_fetch_at_the_deadline_without_replay(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-23T12:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority:true); var v = await WorkspaceEffectCase(f);
        var native = new DelayedTenantDownload(true); var service = WorkspaceEffects(f, downloads:native);
        var call = new ModelToolCall("late-public-file", ToolCatalog.WebDownload, """{"url":"https://example.test/late.bin","path":"/working/late.bin"}""");
        await WorkspaceEffectCheckpoint(f, v.Frame, call);
        var pending = service.ExecuteAsync(v.Frame, call).AsTask(); await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromSeconds(120));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(TenantOperationOutcome.Indeterminate, result.Receipt!.Outcome); Assert.Null(result.Receipt.PublicDownload);
        native.Release.TrySetResult(); await native.Returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(result, await service.ExecuteAsync(v.Frame, call)); Assert.Equal(1, native.Count);
        Assert.DoesNotContain((await WorkspaceReader(f).ListTenantAsync(new(v.Setup.Tenant, v.Setup.Agent.Instance.InstanceId, v.Frame.SessionId), "/working", null, 100)).Items, i => i.LogicalPath == "/working/late.bin");
    }
    private sealed class DelayedTenantDownload(bool delayed) : IPublicWebFetcher
    {
        public bool SupportsDownloads => true; public int Count;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<PublicWebDownloadResult> DownloadAsync(PublicWebDownloadRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count); Entered.TrySetResult(); if (delayed) await Release.Task;
            Returned.TrySetResult(); return new(request.Url.AbsoluteUri, "application/octet-stream", "download.bin", [0,255,42], null, null);
        }
        public ValueTask<PublicWebFetchResult> FetchAsync(PublicWebFetchRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
