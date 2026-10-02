using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

public sealed class NopCommerceBrowserProbeFactAttribute : FactAttribute
{
    public NopCommerceBrowserProbeFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AGENTCORE_NOPCOMMERCE_PROBE"), "1", StringComparison.Ordinal))
        {
            Skip = "Opt-in AGENTCORE_NOPCOMMERCE_PROBE=1 is required. Default suites do not open nopCommerce.";
        }
    }
}

public sealed class NopCommerceBrowserProbeTests
{
    [NopCommerceBrowserProbeFact]
    public async Task Create_page_exposes_usable_controls_and_upload_after_save()
    {
        var origin = Environment.GetEnvironmentVariable("AGENTCORE_NOPCOMMERCE_ORIGIN") ?? "http://127.0.0.1:5088";
        var agentText = Environment.GetEnvironmentVariable("AGENTCORE_NOPCOMMERCE_AGENT")
            ?? "01a0fcc9-e7b6-7a19-84e9-bc0aec2396d6";
        var agentId = Guid.Parse(agentText);
        var profileRoot = FindProfileRoot(agentId);
        var sku = "AC-PROBE-001";
        var session = new PlaywrightBrowserSession(
            new BrowserOptions
            {
                Enabled = true,
                Headless = true,
                InteractionMode = nameof(BrowserInteractionMode.InteractiveDemo),
                PolicyMode = nameof(BrowserPolicyMode.Restricted),
                ProfileMode = nameof(BrowserProfileMode.PersistentAgent),
                ProfileRoot = profileRoot,
                FixtureEnabled = false,
                TargetOrigins = [origin],
                InteractionOrigins = [origin]
            },
            loggerFactory: null);
        var browserId = Guid.NewGuid();
        await session.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(session.IsAvailable, "Chromium is not ready for the nopCommerce probe.");
            ((IBrowserProfileBinding)session).BindSession(browserId, agentId);
            var prior = await session.NavigateAsync(
                new BrowserNavigateRequest(browserId, new Uri($"{origin}/search?q={sku}")),
                CancellationToken.None);
            Assert.Null(prior.ErrorCode);
            Assert.True(
                !(prior.Observation?.VisibleText ?? string.Empty).Contains("AC Probe", StringComparison.Ordinal),
                $"Probe product {sku} is already on the storefront. Run scripts/nopcommerce-demo.sh reset before another create probe. This probe does not delete store data.");
            var absent = await SubmitSkuLookup(session, browserId, origin, "AC-MISSING-001");
            Assert.Contains("/Admin/Product/List", absent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/Admin/Product/Edit/", absent, StringComparison.OrdinalIgnoreCase);
            var create = await session.NavigateAsync(
                new BrowserNavigateRequest(browserId, new Uri($"{origin}/Admin/Product/Create")),
                CancellationToken.None);
            Assert.Null(create.ErrorCode);
            Assert.NotNull(create.Observation);
            Assert.DoesNotContain(
                create.Observation.Elements,
                element => element.Name.Contains("token", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("click"));
            var settled = await session.ObserveAsync(browserId, CancellationToken.None);
            Assert.Null(settled.ErrorCode);
            var current = settled.Observation!;
            var visible = Names(current);
            Assert.Contains(current.Elements, element => element.Name.Contains("advanced", StringComparison.OrdinalIgnoreCase));
            var name = Require(current, element => element.Name.Contains("Product name", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"), visible);
            var skuField = Require(current, element => element.Name.Equals("SKU", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"), visible);
            Assert.Equal(string.Empty, name.State?.Value);
            Assert.Equal(string.Empty, skuField.State?.Value);
            var priceOrCard = current.Elements.FirstOrDefault(element =>
                element.Name.Contains("Price", StringComparison.OrdinalIgnoreCase)
                && element.Actions.Contains("fill"))
                ?? Require(current, element => element.Name.Contains("Price", StringComparison.OrdinalIgnoreCase), visible);
            var save = Require(current, element => element.Name.Contains("Save and Continue", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("click"), visible);

            var named = await session.ActAsync(new BrowserActRequest(browserId, "fill", name.Ref, "AC Probe"), CancellationToken.None);
            Assert.Null(named.ErrorCode);
            current = named.Observation!;
            Assert.Equal("AC Probe", Require(current, element => element.Name.Contains("Product name", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"), Names(current)).State?.Value);
            skuField = Require(current, element => element.Name.Equals("SKU", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"), Names(current));
            var skuSet = await session.ActAsync(new BrowserActRequest(browserId, "fill", skuField.Ref, sku), CancellationToken.None);
            Assert.Null(skuSet.ErrorCode);
            current = skuSet.Observation!;
            Assert.Equal(sku, Require(current, element => element.Name.Equals("SKU", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"), Names(current)).State?.Value);
            var description = Require(
                current,
                element => element.Name.Contains("Short description", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"),
                Names(current));
            const string summary = "Probe keyboard for the catalog.";
            var described = await session.ActAsync(new BrowserActRequest(browserId, "fill", description.Ref, summary), CancellationToken.None);
            Assert.Null(described.ErrorCode);
            current = described.Observation!;
            Assert.Contains(
                summary,
                Require(current, element => element.Name.Contains("Short description", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"), Names(current)).State?.Value,
                StringComparison.Ordinal);
            priceOrCard = current.Elements.FirstOrDefault(element =>
                element.Name.Contains("Price", StringComparison.OrdinalIgnoreCase)
                && element.Actions.Contains("fill"))
                ?? Require(current, element => element.Name.Contains("Price", StringComparison.OrdinalIgnoreCase), Names(current));
            if (!priceOrCard.Actions.Contains("fill"))
            {
                var opened = await session.ActAsync(new BrowserActRequest(browserId, priceOrCard.Actions[0], priceOrCard.Ref, null), CancellationToken.None);
                Assert.Null(opened.ErrorCode);
                priceOrCard = Require(opened.Observation!, element => element.Name.Contains("Price", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"), Names(opened.Observation!));
            }

            var priced = await session.ActAsync(new BrowserActRequest(browserId, "fill", priceOrCard.Ref, "99"), CancellationToken.None);
            Assert.Null(priced.ErrorCode);
            var priceState = Require(
                priced.Observation!,
                element => element.Name.Contains("Price", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"),
                Names(priced.Observation!)).State?.Value ?? string.Empty;
            Assert.Contains("99", priceState, StringComparison.Ordinal);
            var published = priced.Observation!.Elements.FirstOrDefault(element =>
                element.Name.Contains("Published", StringComparison.OrdinalIgnoreCase)
                && element.State?.Checked is not null);
            if (published is not null)
            {
                Assert.True(published.State!.Checked);
            }

            save = Require(
                priced.Observation!,
                element => element.Name.Contains("Save and Continue", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("click"),
                Names(priced.Observation!));
            var saved = await session.ActAsync(new BrowserActRequest(browserId, "click", save.Ref, null), CancellationToken.None);
            Assert.Null(saved.ErrorCode);
            Assert.Contains("/Admin/Product/Edit/", saved.Observation!.Url, StringComparison.OrdinalIgnoreCase);

            var settledEdit = await session.ObserveAsync(browserId, CancellationToken.None);
            Assert.Null(settledEdit.ErrorCode);
            var upload = settledEdit.Observation!.Elements.FirstOrDefault(element =>
                element.Actions.Contains("upload")
                && element.Name.Contains("Picture", StringComparison.OrdinalIgnoreCase));
            if (upload is null)
            {
                var multimedia = Require(
                    settledEdit.Observation,
                    element => element.Name.Contains("Multimedia", StringComparison.OrdinalIgnoreCase)
                        || element.Name.Contains("Picture", StringComparison.OrdinalIgnoreCase),
                    Names(settledEdit.Observation));
                var expanded = await session.ActAsync(
                    new BrowserActRequest(browserId, multimedia.Actions[0], multimedia.Ref, null),
                    CancellationToken.None);
                Assert.Null(expanded.ErrorCode);
                upload = Require(
                    expanded.Observation!,
                    element => element.Actions.Contains("upload") && element.Name.Contains("Picture", StringComparison.OrdinalIgnoreCase),
                    Names(expanded.Observation!));
            }

            Assert.Equal(["upload"], upload.Actions);
            var png = await File.ReadAllBytesAsync(FindKeyboardImage());
            var uploaded = await session.ActAsync(
                new BrowserActRequest(browserId, "upload", upload.Ref, null, new BrowserUpload("ac-keyboard.png", "image/png", png)),
                CancellationToken.None);
            Assert.Null(uploaded.ErrorCode);
            Assert.All(
                uploaded.Observation!.Elements,
                element => Assert.DoesNotContain("fakepath", element.State?.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            var pictured = await session.ObserveAsync(browserId, CancellationToken.None);
            Assert.Null(pictured.ErrorCode);
            Assert.Contains("/Admin/Product/Edit/", pictured.Observation!.Url, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                pictured.Observation.Elements,
                element => element.Name.Contains("Picture", StringComparison.OrdinalIgnoreCase)
                    || element.Actions.Contains("upload"));

            var storefront = await session.NavigateAsync(
                new BrowserNavigateRequest(browserId, new Uri($"{origin}/search?q={sku}")),
                CancellationToken.None);
            Assert.Null(storefront.ErrorCode);
            Assert.Contains("AC Probe", storefront.Observation!.VisibleText, StringComparison.Ordinal);
            Assert.Contains("99", storefront.Observation.VisibleText, StringComparison.Ordinal);
            var present = await SubmitSkuLookup(session, browserId, origin, sku);
            Assert.Contains("/Admin/Product/Edit/", present, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await session.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<string> SubmitSkuLookup(
        PlaywrightBrowserSession session,
        Guid browserId,
        string origin,
        string sku)
    {
        var list = await session.NavigateAsync(
            new BrowserNavigateRequest(browserId, new Uri($"{origin}/Admin/Product/List")),
            CancellationToken.None);
        Assert.Null(list.ErrorCode);
        var observed = await session.ObserveAsync(browserId, CancellationToken.None);
        Assert.Null(observed.ErrorCode);
        var field = Require(
            observed.Observation!,
            element => element.Actions.Contains("fill")
                && element.Name.Contains("SKU", StringComparison.OrdinalIgnoreCase)
                && element.Name.Contains("directly", StringComparison.OrdinalIgnoreCase),
            Names(observed.Observation!));
        var filled = await session.ActAsync(
            new BrowserActRequest(browserId, "fill", field.Ref, sku),
            CancellationToken.None);
        Assert.Null(filled.ErrorCode);
        var go = Require(
            filled.Observation!,
            element => element.Actions.Contains("click")
                && element.Name.Equals("Go", StringComparison.OrdinalIgnoreCase),
            Names(filled.Observation!));
        var clicked = await session.ActAsync(
            new BrowserActRequest(browserId, "click", go.Ref, null),
            CancellationToken.None);
        Assert.Null(clicked.ErrorCode);
        return clicked.Observation!.Url;
    }

    private static string Names(BrowserObservation observation) =>
        string.Join(" | ", observation.Elements.Select(element => $"{element.Name} [{string.Join(",", element.Actions)}]"));

    private static BrowserElement Require(BrowserObservation observation, Func<BrowserElement, bool> match, string detail)
    {
        var found = observation.Elements.FirstOrDefault(match);
        Assert.True(found is not null, detail);
        return found!;
    }

    private static string FindProfileRoot(Guid agentId)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var relative in new[]
            {
                "local/browser-profiles",
                "src/AgentCore.Api/data/browser-profiles",
                "data/browser-profiles"
            })
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (Directory.Exists(Path.Combine(candidate, agentId.ToString("D"))))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Persistent nopCommerce browser profile was not found.");
    }

    private static string FindKeyboardImage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "deploy", "nopcommerce", "assets", "ac-keyboard.png");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("ac-keyboard.png");
    }
}
