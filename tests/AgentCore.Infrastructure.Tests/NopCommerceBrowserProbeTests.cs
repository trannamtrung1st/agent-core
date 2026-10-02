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
            var priceOrCard = current.Elements.FirstOrDefault(element =>
                element.Name.Contains("Price", StringComparison.OrdinalIgnoreCase)
                && element.Actions.Contains("fill"))
                ?? Require(current, element => element.Name.Contains("Price", StringComparison.OrdinalIgnoreCase), visible);
            var save = Require(current, element => element.Name.Contains("Save and Continue", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("click"), visible);

            var named = await session.ActAsync(new BrowserActRequest(browserId, "fill", name.Ref, "AC Probe"), CancellationToken.None);
            Assert.Null(named.ErrorCode);
            current = named.Observation!;
            skuField = Require(current, element => element.Name.Equals("SKU", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("fill"), Names(current));
            var skuSet = await session.ActAsync(new BrowserActRequest(browserId, "fill", skuField.Ref, sku), CancellationToken.None);
            Assert.Null(skuSet.ErrorCode);
            current = skuSet.Observation!;
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
            save = Require(
                priced.Observation!,
                element => element.Name.Contains("Save and Continue", StringComparison.OrdinalIgnoreCase) && element.Actions.Contains("click"),
                Names(priced.Observation!));
            var saved = await session.ActAsync(new BrowserActRequest(browserId, "click", save.Ref, null), CancellationToken.None);
            Assert.Null(saved.ErrorCode);
            Assert.Contains("/Admin/Product/Edit/", saved.Observation!.Url, StringComparison.OrdinalIgnoreCase);

            var upload = saved.Observation.Elements.FirstOrDefault(element => element.Actions.Contains("upload"));
            if (upload is null)
            {
                var multimedia = Require(
                    saved.Observation,
                    element => element.Name.Contains("Multimedia", StringComparison.OrdinalIgnoreCase)
                        || element.Name.Contains("Picture", StringComparison.OrdinalIgnoreCase),
                    string.Join(" | ", saved.Observation.Elements.Select(element => element.Name)));
                var expanded = await session.ActAsync(
                    new BrowserActRequest(browserId, multimedia.Actions[0], multimedia.Ref, null),
                    CancellationToken.None);
                Assert.Null(expanded.ErrorCode);
                upload = Require(expanded.Observation!, element => element.Actions.Contains("upload"), "picture file input");
            }

            Assert.Equal(["upload"], upload.Actions);
            var png = await File.ReadAllBytesAsync(FindKeyboardImage());
            var uploaded = await session.ActAsync(
                new BrowserActRequest(browserId, "upload", upload.Ref, null, new BrowserUpload("ac-keyboard.png", "image/png", png)),
                CancellationToken.None);
            Assert.Null(uploaded.ErrorCode);

            var storefront = await session.NavigateAsync(
                new BrowserNavigateRequest(browserId, new Uri($"{origin}/search?q={sku}")),
                CancellationToken.None);
            Assert.Null(storefront.ErrorCode);
            Assert.Contains("AC Probe", storefront.Observation!.VisibleText, StringComparison.Ordinal);
            Assert.Contains("99", storefront.Observation.VisibleText, StringComparison.Ordinal);
        }
        finally
        {
            await session.StopAsync(CancellationToken.None);
        }
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
