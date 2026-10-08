using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserEvidenceProgressTests
{
    [Fact]
    public void Native_tree_changes_reset_evidence_but_new_refs_do_not()
    {
        var progress = new BrowserEvidenceProgress();
        string Tree(string text, string reference) => System.Text.Json.JsonSerializer.Serialize(new
        { url = "http://store.test/", content = $"- treeitem \"{text}\"" });
        progress.Note(ToolCatalog.BrowserSnapshot, Tree("Asset 1", "el_old"));
        progress.Note(ToolCatalog.BrowserSnapshot, Tree("Asset 1", "el_new"));
        Assert.Equal(1, progress.Repeated);
        progress.Note(ToolCatalog.BrowserSnapshot, Tree("Asset 2", "el_latest"));
        Assert.Equal(0, progress.Repeated);
    }
    [Fact]
    public void Identical_observations_stop_after_two_repeats()
    {
        var progress = new BrowserEvidenceProgress();
        progress.Note(ToolCatalog.BrowserNavigate, Page("http://store.test/orders", "Orders grid", "el_1"));
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/orders", "Orders grid", "el_2"));
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/orders", "Orders grid", "el_3"));
        Assert.False(progress.ShouldStop);
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/orders", "Orders grid", "el_4"));
        Assert.True(progress.ShouldStop);
        Assert.Equal(2, progress.Repeated);
    }

    [Fact]
    public void Navigation_and_actions_reset_the_streak_when_visible_text_stays_the_same()
    {
        var progress = new BrowserEvidenceProgress();
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/edit", "Edit product", "el_1", value: ""));
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/edit", "Edit product", "el_2", value: ""));
        progress.Note(ToolCatalog.BrowserClick, Page("http://store.test/edit", "Edit product", "el_3", value: "Keyboard"));
        progress.Note(ToolCatalog.BrowserClick, Page("http://store.test/edit", "Edit product", "el_4", value: "AC-KBD"));
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/edit", "Edit product", "el_5", value: "AC-KBD"));
        progress.Note(ToolCatalog.BrowserNavigate, Page("http://store.test/storefront", "Edit product", "el_6"));
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/storefront", "Edit product", "el_7"));
        Assert.False(progress.ShouldStop);
        Assert.Equal(0, progress.Repeated);
    }

    [Fact]
    public void Changed_control_state_is_new_observation_evidence()
    {
        var progress = new BrowserEvidenceProgress();
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/edit", "Edit product", "el_1", value: ""));
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/edit", "Edit product", "el_2", value: "Keyboard"));
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/edit", "Edit product", "el_9", value: "Keyboard", settled: true));
        Assert.False(progress.ShouldStop);
        Assert.Equal(0, progress.Repeated);
    }

    [Fact]
    public void Errors_do_not_count_as_repeated_evidence()
    {
        var progress = new BrowserEvidenceProgress();
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/orders", "Orders grid", "el_1"));
        progress.Note(ToolCatalog.BrowserSnapshot, """{"error":"timeout","message":"Browser operation timed out."}""");
        progress.Note(ToolCatalog.BrowserSnapshot, Page("http://store.test/orders", "Orders grid", "el_2"));
        Assert.False(progress.ShouldStop);
        Assert.Equal(1, progress.Repeated);
    }

    [Fact]
    public void Alternative_semantic_search_resets_streak_but_repeating_the_same_matches_does_not()
    {
        var progress = new BrowserEvidenceProgress();
        var page = Page("http://store.test/", "Catalog", "el_1");
        var search = """{"snapshotId":"snap_1","matches":[{"ref":"el_2","role":"button","name":"Open review","actions":["click"]}]}""";
        progress.Note(ToolCatalog.BrowserSnapshot, page);
        progress.Note(ToolCatalog.BrowserSnapshot, page);
        Assert.Equal(1, progress.Repeated);
        progress.Note(ToolCatalog.BrowserFind, search);
        Assert.Equal(0, progress.Repeated);
        progress.Note(ToolCatalog.BrowserSnapshot, page);
        progress.Note(ToolCatalog.BrowserSnapshot, page);
        progress.Note(ToolCatalog.BrowserFind, search.Replace("el_2", "el_3"));
        Assert.Equal(1, progress.Repeated);
        progress.Note(ToolCatalog.BrowserSnapshot, page);
        Assert.True(progress.ShouldStop);
    }

    private static string Page(string url, string visible, string reference, string value = "", bool? settled = null)
    {
        var settledJson = settled is bool flag ? $",\"settled\":{(flag ? "true" : "false")}" : string.Empty;
        var state = value.Length == 0 ? string.Empty : $",\"state\":{{\"value\":\"{value}\"}}";
        return $$"""
        {"untrustedBrowserContent":true,"url":"{{url}}","title":"Store","content":"{{visible}}"{{settledJson}},"targets":[{"ref":"{{reference}}","role":"textbox","name":"Name","actions":["fill"]{{state}}}]}
        """;
    }
}
