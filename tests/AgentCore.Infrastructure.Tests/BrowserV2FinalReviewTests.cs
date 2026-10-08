using System.Text.Json;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserV2FinalReviewTests
{
    [Theory]
    [InlineData("alert")]
    [InlineData("confirm")]
    [InlineData("prompt")]
    public async Task Modal_inspection_never_discloses_cookie_storage_or_credential_values(string kind)
    {
        var browser = new PlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single()+"/credential-login?reflect=1")))).ErrorCode);
            var context=browser.ContextFor(id)!; var page=context.Pages[0];
            var observation=await browser.SnapshotAsync(id);
            var reference=observation.Observation!.Elements.Single(e=>e.Actions.Contains("fill_credential")).Ref;
            Assert.Null((await browser.FillCredentialAsync(id,reference,(_,_)=>ValueTask.FromResult("credential-private-1574"))).ErrorCode);
            await context.AddCookiesAsync([new() { Name="review", Value="cookie-private-7291", Url=page.Url }]);
            await page.EvaluateAsync("() => { localStorage.setItem('token','local-private-8392'); sessionStorage.setItem('token','session-private-9483'); }");
            var shown=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Dialog+=(_,_)=>shown.TrySetResult();
            var pending=page.EvaluateAsync("kind => window[kind]([document.cookie,localStorage.token,sessionStorage.token,document.querySelector('input[type=password]').value].join(' '))",kind);
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result=await browser.ExecuteAsync(new(id,"browser.dialog",JsonSerializer.SerializeToElement(new {operation="inspect"})));
            Assert.Null(result.ErrorCode);
            Assert.DoesNotContain("private-",result.DataJson!);
            Assert.Contains("[redacted]",result.DataJson!);
            Assert.Null((await browser.ExecuteAsync(new(id,"browser.dialog",JsonSerializer.SerializeToElement(new {operation="dismiss"})))).ErrorCode);
            await pending;
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Tab_urls_hide_known_secrets_and_sensitive_query_parameters()
    {
        var browser=new PlaywrightBrowser(new() {Enabled=true,Headless=true,FixturePort=0},null);
        await browser.StartAsync(CancellationToken.None); var id=Guid.NewGuid();
        try
        {
            Assert.Null((await browser.NavigateAsync(new(id,new Uri(browser.HostPolicy.NavigationOrigins.Single()+"/browser-v2.html?compact=1")))).ErrorCode);
            var page=browser.ContextFor(id)!.Pages[0];
            await page.EvaluateAsync("() => { localStorage.setItem('token','storage-private-7291'); history.replaceState(null,'','?ordinary=storage-private-7291&access_token=unknown-private-8392&api_key=api-private-7364&privateKey=key-private-9183&key=bare-private-7264&X-Amz-Signature=signature-private-8314&view=summary'); }");
            var secondary=await browser.ContextFor(id)!.NewPageAsync();
            await secondary.GotoAsync(browser.HostPolicy.NavigationOrigins.Single()+"/browser-v2-frame.html");
            await secondary.EvaluateAsync("() => { sessionStorage.setItem('token','secondary-private quote'); history.replaceState(null,'','?ordinary='+encodeURIComponent(sessionStorage.token)); }");
            var result=await browser.ExecuteAsync(new(id,"browser.tabs",JsonSerializer.SerializeToElement(new {operation="list"})));
            Assert.Null(result.ErrorCode); Assert.DoesNotContain("private-",result.DataJson!); Assert.Contains("view=summary",result.DataJson!);
            var snapshot=await browser.SnapshotAsync(id); Assert.DoesNotContain("private-",snapshot.Observation!.Url);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Canceled_resize_fences_a_non_cooperative_provider_call_before_recovery()
    {
        var browser=new PlaywrightBrowser(new() {Enabled=true,Headless=true,FixturePort=0},null);
        await browser.StartAsync(CancellationToken.None); var id=Guid.NewGuid();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? native=null;
        try
        {
            Assert.Null((await browser.NavigateAsync(new(id,new Uri(browser.HostPolicy.NavigationOrigins.Single()+"/browser-v2.html?compact=1")))).ErrorCode);
            var original=browser.ContextFor(id)!.Pages[0];
            browser.ResizeProbe=(page,w,h)=>native=Delayed(page,w,h);
            async Task Delayed(IPage page,int w,int h) { entered.TrySetResult(); await release.Task; await page.SetViewportSizeAsync(w,h); }
            using var cancel=new CancellationTokenSource();
            var pending=browser.ExecuteAsync(new(id,"browser.resize",JsonSerializer.SerializeToElement(new {width=900,height=700})),cancel.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            Assert.True(original.IsClosed);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<PlaywrightException>(()=>native!);
            Assert.Null((await browser.SnapshotAsync(id)).ErrorCode);
        }
        finally { release.TrySetResult(); await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Canceled_tab_navigation_closes_the_pending_tab_and_retains_original()
    {
        var browser=new PlaywrightBrowser(new() {Enabled=true,Headless=true,FixturePort=0},null);
        await browser.StartAsync(CancellationToken.None); var id=Guid.NewGuid();
        try
        {
            var origin=browser.HostPolicy.NavigationOrigins.Single();
            Assert.Null((await browser.NavigateAsync(new(id,new Uri(origin+"/browser-v2.html?compact=1")))).ErrorCode);
            var context=browser.ContextFor(id)!; var original=context.Pages[0];
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.RouteAsync("**/review-stalled",async route=>{ entered.TrySetResult(); await release.Task; try { await route.AbortAsync(); } catch(PlaywrightException){} });
            using var cancel=new CancellationTokenSource();
            var pending=browser.ExecuteAsync(new(id,"browser.tabs",JsonSerializer.SerializeToElement(new {operation="new",url=origin+"/review-stalled"})),cancel.Token).AsTask();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
                Assert.Same(original,Assert.Single(context.Pages));
                Assert.Null((await browser.SnapshotAsync(id)).ErrorCode);
            }
            finally { release.TrySetResult(); }
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
