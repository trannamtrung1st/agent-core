using System.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserSettleTests(BrowserHostFixture fixture) : IClassFixture<BrowserHostFixture>
{
    [Fact]
    public async Task Navigation_includes_content_that_arrives_after_the_document_loads()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var observed = await fixture.Session.NavigateAsync(
            new BrowserNavigateRequest(id, new Uri(origin + "/settle-delayed")));

        Assert.Null(observed.ErrorCode);
        Assert.Contains("Orders", observed.Observation!.VisibleText, StringComparison.Ordinal);
        Assert.Contains("AC-SETTLE-ROW", observed.Observation.VisibleText, StringComparison.Ordinal);
        Assert.Equal(true, observed.Observation.Settled);
    }

    [Fact]
    public async Task Click_includes_a_result_that_arrives_after_the_action()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var home = await fixture.Session.NavigateAsync(
            new BrowserNavigateRequest(id, new Uri(origin + "/settle-click")));
        Assert.Null(home.ErrorCode);
        Assert.DoesNotContain("AC-SETTLE-RESULT", home.Observation!.VisibleText, StringComparison.Ordinal);
        var search = Assert.Single(home.Observation.Elements, element => element.Name == "Search");

        var clicked = await fixture.Session.ActAsync(new BrowserActRequest(id, "click", search.Ref, null));

        Assert.Null(clicked.ErrorCode);
        Assert.Contains("AC-SETTLE-RESULT", clicked.Observation!.VisibleText, StringComparison.Ordinal);
        Assert.Equal(true, clicked.Observation.Settled);
    }

    [Fact]
    public async Task Explicit_stable_observe_returns_content_the_automatic_settle_missed()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var early = await fixture.Session.NavigateAsync(
            new BrowserNavigateRequest(id, new Uri(origin + "/settle-pending")));
        Assert.Null(early.ErrorCode);
        Assert.Contains("Loading...", early.Observation!.VisibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("AC-SETTLE-LATE", early.Observation.VisibleText, StringComparison.Ordinal);
        Assert.Equal(false, early.Observation.Settled);

        var settled = await fixture.Session.ObserveAsync(
            id,
            new BrowserObserveOptions("stable", 4000));

        Assert.Null(settled.ErrorCode);
        Assert.Contains("AC-SETTLE-LATE", settled.Observation!.VisibleText, StringComparison.Ordinal);
        Assert.Equal(true, settled.Observation.Settled);
        Assert.EndsWith("/settle-pending", settled.Observation.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stable_observe_on_a_static_page_invalidates_the_previous_refs()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var home = await fixture.Session.NavigateAsync(new BrowserNavigateRequest(id, new Uri(origin + "/")));
        var search = Assert.Single(home.Observation!.Elements, element => element.Name == "Search");

        var again = await fixture.Session.ObserveAsync(
            id,
            new BrowserObserveOptions("stable", BrowserToolLimits.DefaultObserveTimeoutMs));

        Assert.Null(again.ErrorCode);
        Assert.Equal(true, again.Observation!.Settled);
        Assert.Contains(again.Observation.Elements, element => element.Name == "Search");
        Assert.DoesNotContain(again.Observation.Elements, element => element.Ref == search.Ref);
        var stale = await fixture.Session.ActAsync(new BrowserActRequest(id, "click", search.Ref, null));
        Assert.Equal("stale_reference", stale.ErrorCode);
    }

    [Fact]
    public async Task Continuous_changes_return_inside_the_requested_timeout()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        await fixture.Session.NavigateAsync(new BrowserNavigateRequest(id, new Uri(origin + "/settle-churn")));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var started = Stopwatch.GetTimestamp();

        var observed = await fixture.Session.ObserveAsync(
            id,
            new BrowserObserveOptions("stable", 1000),
            guard.Token);

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.Null(observed.ErrorCode);
        Assert.Equal(false, observed.Observation!.Settled);
        Assert.Contains("Live", observed.Observation.VisibleText, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromSeconds(2.5), elapsed.ToString());
    }

    [Fact]
    public async Task Static_navigation_settles_without_using_the_maximum_wait()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        var home = await fixture.Session.NavigateAsync(new BrowserNavigateRequest(id, new Uri(origin + "/")));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Null(home.ErrorCode);
        Assert.Contains("Record lookup", home.Observation!.VisibleText, StringComparison.Ordinal);
        Assert.Equal(true, home.Observation.Settled);
        Assert.True(elapsed < TimeSpan.FromSeconds(3), elapsed.ToString());

        var quickStart = Stopwatch.GetTimestamp();
        var quick = await fixture.Session.ObserveAsync(id);
        Assert.Null(quick.ErrorCode);
        Assert.Null(quick.Observation!.Settled);
        Assert.True(Stopwatch.GetElapsedTime(quickStart) < TimeSpan.FromSeconds(1.5), Stopwatch.GetElapsedTime(quickStart).ToString());
    }

    [Fact]
    public async Task Fill_stays_immediate_while_check_select_press_and_uncheck_settle()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var page = await fixture.Session.NavigateAsync(new BrowserNavigateRequest(id, new Uri(origin + "/state")));
        Assert.Null(page.ErrorCode);

        var note = Assert.Single(page.Observation!.Elements, element => element.Name == "Empty note");
        var started = Stopwatch.GetTimestamp();
        var filled = await fixture.Session.ActAsync(new BrowserActRequest(id, "fill", note.Ref, "AC-FILL"));
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.Null(filled.ErrorCode);
        Assert.Null(filled.Observation!.Settled);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), elapsed.ToString());
        Assert.Equal("AC-FILL", Assert.Single(filled.Observation.Elements, element => element.Name == "Empty note").State?.Value);

        var published = Assert.Single(filled.Observation.Elements, element => element.Name == "Published");
        var checkedBox = await fixture.Session.ActAsync(new BrowserActRequest(id, "check", published.Ref, null));
        Assert.Null(checkedBox.ErrorCode);
        Assert.Equal(true, checkedBox.Observation!.Settled);
        Assert.Equal(true, Assert.Single(checkedBox.Observation.Elements, element => element.Name == "Published").State?.Checked);

        var featured = Assert.Single(checkedBox.Observation.Elements, element => element.Name == "Featured");
        var uncheckedBox = await fixture.Session.ActAsync(new BrowserActRequest(id, "uncheck", featured.Ref, null));
        Assert.Null(uncheckedBox.ErrorCode);
        Assert.Equal(true, uncheckedBox.Observation!.Settled);
        Assert.Equal(false, Assert.Single(uncheckedBox.Observation.Elements, element => element.Name == "Featured").State?.Checked);

        var category = Assert.Single(uncheckedBox.Observation.Elements, element => element.Name == "Category");
        var selected = await fixture.Session.ActAsync(new BrowserActRequest(id, "select", category.Ref, "grouped"));
        Assert.Null(selected.ErrorCode);
        Assert.Equal(true, selected.Observation!.Settled);
        Assert.Equal("Grouped", Assert.Single(selected.Observation.Elements, element => element.Name == "Category").State?.SelectedText);

        var pressTarget = Assert.Single(selected.Observation.Elements, element => element.Name == "Empty note");
        var pressed = await fixture.Session.ActAsync(new BrowserActRequest(id, "press", pressTarget.Ref, "Tab"));
        Assert.Null(pressed.ErrorCode);
        Assert.Equal(true, pressed.Observation!.Settled);
    }

    [Fact]
    public async Task Cancelling_a_stable_wait_stops_the_observation()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        await fixture.Session.NavigateAsync(new BrowserNavigateRequest(id, new Uri(origin + "/settle-churn")));
        using var cts = new CancellationTokenSource();
        var pending = fixture.Session.ObserveAsync(id, new BrowserObserveOptions("stable", 4000), cts.Token).AsTask();
        await Task.Delay(150);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }
}
