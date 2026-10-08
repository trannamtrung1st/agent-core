using AgentCore.Tests.Shared;
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
        var observed = await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/settle-delayed")));

        Assert.Null(observed.ErrorCode);
        Assert.Contains("Orders", observed.Observation!.Content!, StringComparison.Ordinal);
        Assert.Contains("AC-SETTLE-ROW", observed.Observation.Content!, StringComparison.Ordinal);
        Assert.Equal(true, observed.Observation.Settled);
    }

    [Fact]
    public async Task Click_includes_a_result_that_arrives_after_the_action()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var home = await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/settle-click")));
        Assert.Null(home.ErrorCode);
        Assert.DoesNotContain("AC-SETTLE-RESULT", home.Observation!.Content!, StringComparison.Ordinal);
        var search = (await BrowserTestQueries.Find(fixture.Session, id, "Search"));

        var clicked = await fixture.Session.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Click, search.Ref, null));

        Assert.Null(clicked.ErrorCode);
        Assert.Contains("AC-SETTLE-RESULT", clicked.Observation!.Content!, StringComparison.Ordinal);
        Assert.Equal(true, clicked.Observation.Settled);
    }

    [Fact]
    public async Task Explicit_stable_observe_returns_content_the_automatic_settle_missed()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var early = await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/settle-pending")));
        Assert.Null(early.ErrorCode);
        Assert.Contains("Loading...", early.Observation!.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("AC-SETTLE-LATE", early.Observation.Content!, StringComparison.Ordinal);
        Assert.Equal(false, early.Observation.Settled);

        var settled = await fixture.Session.ExecuteAsync(BrowserTestRequests.Inspect(
            id,
            new BrowserOptionsData { Condition = "stable", TimeoutMs = 4000 }));

        Assert.Null(settled.ErrorCode);
        Assert.Contains("AC-SETTLE-LATE", settled.Observation!.Content!, StringComparison.Ordinal);
        Assert.Equal(true, settled.Observation.Settled);
        Assert.EndsWith("/settle-pending", settled.Observation.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stable_observe_on_a_static_page_preserves_semantic_refs()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var home = await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/")));
        var search = (await BrowserTestQueries.Find(fixture.Session, id, "Search"));

        var again = await fixture.Session.ExecuteAsync(BrowserTestRequests.Inspect(
            id,
            new BrowserOptionsData { Condition = "stable", TimeoutMs = BrowserToolLimits.DefaultObserveTimeoutMs }));

        Assert.Null(again.ErrorCode);
        Assert.Equal(true, again.Observation!.Settled);
        Assert.NotEmpty((await BrowserTestQueries.Find(fixture.Session, id, "Search")).Ref);
        Assert.Null((await fixture.Session.ExecuteAsync(new(id, BrowserOperation.Click, new() { Ref = search.Ref }))).ErrorCode);
        var stale = await fixture.Session.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Click, search.Ref, null));
        Assert.Equal("stale_reference", stale.ErrorCode);
    }

    [Fact]
    public async Task Continuous_changes_return_inside_the_requested_timeout()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/settle-churn")));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var started = Stopwatch.GetTimestamp();

        var observed = await fixture.Session.ExecuteAsync(BrowserTestRequests.Inspect(id, new BrowserOptionsData { Condition = "stable", TimeoutMs = 1000 }), guard.Token);

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.Null(observed.ErrorCode);
        Assert.Equal(false, observed.Observation!.Settled);
        Assert.Contains("Live", observed.Observation.Content!, StringComparison.Ordinal);
        Assert.True(elapsed < TimeSpan.FromSeconds(2.5), elapsed.ToString());
    }

    [Fact]
    public async Task Static_navigation_settles_without_using_the_maximum_wait()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var started = Stopwatch.GetTimestamp();
        var home = await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/")));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Null(home.ErrorCode);
        Assert.Contains("Record lookup", home.Observation!.Content!, StringComparison.Ordinal);
        Assert.Equal(true, home.Observation.Settled);
        Assert.True(elapsed < TimeSpan.FromSeconds(3), elapsed.ToString());

        var quickStart = Stopwatch.GetTimestamp();
        var quick = await fixture.Session.ExecuteAsync(BrowserTestRequests.Inspect(id));
        Assert.Null(quick.ErrorCode);
        Assert.Null(quick.Observation!.Settled);
        Assert.True(Stopwatch.GetElapsedTime(quickStart) < TimeSpan.FromSeconds(1.5), Stopwatch.GetElapsedTime(quickStart).ToString());
    }

    [Fact]
    public async Task Fill_stays_immediate_while_check_select_press_and_uncheck_settle()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        var page = await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/state")));
        Assert.Null(page.ErrorCode);

        var note = (await BrowserTestQueries.Find(fixture.Session, id, "Empty note"));
        var started = Stopwatch.GetTimestamp();
        var filled = await fixture.Session.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.Type, note.Ref, "AC-FILL"));
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.Null(filled.ErrorCode);
        Assert.Null(filled.Observation!.Settled);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), elapsed.ToString());
        Assert.Equal("AC-FILL", (await BrowserTestQueries.Find(fixture.Session, id, "Empty note")).State?.Value);

        var published = (await BrowserTestQueries.Find(fixture.Session, id, "Published"));
        var checkedBox = await fixture.Session.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.FillForm, published.Ref, null, Checked: true));
        Assert.Null(checkedBox.ErrorCode);
        Assert.Equal(true, checkedBox.Observation!.Settled);
        Assert.Equal(true, (await BrowserTestQueries.Find(fixture.Session, id, "Published")).State?.Checked);

        var featured = (await BrowserTestQueries.Find(fixture.Session, id, "Featured"));
        var uncheckedBox = await fixture.Session.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.FillForm, featured.Ref, null, Checked: false));
        Assert.Null(uncheckedBox.ErrorCode);
        Assert.Equal(true, uncheckedBox.Observation!.Settled);
        Assert.Equal(false, (await BrowserTestQueries.Find(fixture.Session, id, "Featured")).State?.Checked);

        var category = (await BrowserTestQueries.Find(fixture.Session, id, "Category"));
        var selected = await fixture.Session.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.SelectOption, category.Ref, "grouped"));
        Assert.Null(selected.ErrorCode);
        Assert.Equal(true, selected.Observation!.Settled);
        Assert.Equal("Grouped", (await BrowserTestQueries.Find(fixture.Session, id, "Category")).State?.SelectedText);

        var pressTarget = (await BrowserTestQueries.Find(fixture.Session, id, "Empty note"));
        var pressed = await fixture.Session.ExecuteAsync(BrowserTestRequests.Interaction(id, BrowserOperation.PressKey, pressTarget.Ref, "Tab"));
        Assert.Null(pressed.ErrorCode);
        Assert.Equal(true, pressed.Observation!.Settled);
    }

    [Fact]
    public async Task Cancelling_a_stable_wait_stops_the_observation()
    {
        var origin = fixture.Session.Fixture.Origin!;
        var id = Guid.NewGuid();
        await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/settle-churn")));
        using var cts = new CancellationTokenSource();
        var pending = fixture.Session.ExecuteAsync(BrowserTestRequests.Inspect(id, new BrowserOptionsData { Condition = "stable", TimeoutMs = 4000 }), cts.Token).AsTask();
        await Task.Delay(150);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }
}
