using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserEvidenceProgressTests
{
    [Fact]
    public void Identical_page_evidence_stops_after_two_repeats()
    {
        var progress = new BrowserEvidenceProgress();
        progress.Note(ToolCatalog.BrowserNavigate, Page("http://store.test/orders", "Orders grid", "el_1"));
        progress.Note(ToolCatalog.BrowserObserve, Page("http://store.test/orders", "Orders grid", "el_2"));
        Assert.False(progress.ShouldStop);
        progress.Note(ToolCatalog.BrowserObserve, Page("http://store.test/orders", "Orders grid", "el_3"));
        Assert.True(progress.ShouldStop);
    }

    [Fact]
    public void A_different_page_or_changed_text_resets_the_streak()
    {
        var progress = new BrowserEvidenceProgress();
        progress.Note(ToolCatalog.BrowserNavigate, Page("http://store.test/orders", "Orders grid", "el_1"));
        progress.Note(ToolCatalog.BrowserObserve, Page("http://store.test/orders", "Orders grid", "el_2"));
        progress.Note(ToolCatalog.BrowserNavigate, Page("http://store.test/products", "Orders grid", "el_3"));
        progress.Note(ToolCatalog.BrowserAct, Page("http://store.test/products", "Product rows", "el_4"));
        progress.Note(ToolCatalog.BrowserObserve, Page("http://store.test/products", "Product rows", "el_5"));
        Assert.False(progress.ShouldStop);
        Assert.Equal(1, progress.Repeated);
    }

    [Fact]
    public void Errors_do_not_count_as_repeated_evidence()
    {
        var progress = new BrowserEvidenceProgress();
        progress.Note(ToolCatalog.BrowserNavigate, Page("http://store.test/orders", "Orders grid", "el_1"));
        progress.Note(ToolCatalog.BrowserObserve, """{"error":"timeout","message":"Browser operation timed out."}""");
        progress.Note(ToolCatalog.BrowserObserve, Page("http://store.test/orders", "Orders grid", "el_2"));
        Assert.False(progress.ShouldStop);
        Assert.Equal(1, progress.Repeated);
    }

    private static string Page(string url, string visible, string reference) =>
        $$"""
        {"untrustedBrowserContent":true,"url":"{{url}}","title":"Store","visibleText":"{{visible}}","elements":[{"ref":"{{reference}}","role":"link","name":"Row"}]}
        """;
}
