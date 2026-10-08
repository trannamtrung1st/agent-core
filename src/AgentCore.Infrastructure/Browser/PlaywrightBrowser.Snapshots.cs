using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class PlaywrightBrowser
{
    private async Task FencePageAsync(SessionBrowser session, Guid sessionId)
    {
        var previous = session.Page;
        var url = previous.Url;
        await previous.CloseAsync().ConfigureAwait(false);
        ForgetPage(session, previous);
        session.Dialog = null; session.PendingAction = null;
        RemoveRefs(sessionId); session.SnapshotId = ""; session.SnapshotIndex = [];
        session.Page = await session.Context.NewPageAsync().ConfigureAwait(false);
        RememberPage(session, session.Page); session.Generation++;
        if (IsAllowed(session, url))
            try { await session.Page.GotoAsync(url, new PageGotoOptions { Timeout = 1000, WaitUntil = WaitUntilState.DOMContentLoaded }); }
            catch (PlaywrightException) { }
    }

    private async Task<BrowserSnapshot> ScopeSnapshotAsync(SessionBrowser session, Guid sessionId,
        BrowserSnapshot snapshot, ILocator scope, CancellationToken ct)
    {
        var native = await scope.AriaSnapshotAsync(new LocatorAriaSnapshotOptions { Mode = AriaSnapshotMode.Default, Timeout = TimeoutMs() }).WaitAsync(ct);
        var secrets = await CollectSecretsAsync(session, ct);
        var content = new StringBuilder();
        var elements = new List<BrowserElement>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var first = true;
        foreach (var line in native.Split('\n'))
        {
            var match = Regex.Match(line, "^\\s*- ([a-z]+)(?: \"((?:[^\"\\\\]|\\\\.)*)\")?");
            if (!match.Success || !Enum.TryParse<AriaRole>(match.Groups[1].Value, true, out var role))
            { content.AppendLine(Redact(line, secrets, session.ProtectedValues)); continue; }
            var name = match.Groups[2].Success ? Regex.Unescape(match.Groups[2].Value) : null;
            var key = role + "\n" + name;
            ordinals.TryGetValue(key, out var ordinal);
            if (!first) ordinals[key] = ordinal + 1;
            var locator = first ? scope : scope.GetByRole(role, new LocatorGetByRoleOptions { Name = name, Exact = true }).Nth(ordinal);
            first = false;
            var described = await locator.EvaluateAsync<string>(DescribeElement).WaitAsync(ct);
            if (described is null or "null" or "") continue;
            using var doc = JsonDocument.Parse(described);
            var actions = doc.RootElement.GetProperty("actions").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var token = MintToken();
            _refs[token] = new(sessionId, session.Generation, locator, actions);
            var safeName = Redact(name ?? doc.RootElement.GetProperty("name").GetString() ?? "", secrets, session.ProtectedValues);
            elements.Add(new(token, match.Groups[1].Value, Clip(safeName, BrowserToolLimits.MaxAccessibleNameLength), actions,
                ReadControlState(described, safeName, secrets, session.ProtectedValues)));
            content.AppendLine(Redact(line, secrets, session.ProtectedValues) + " [ref=" + token + "]");
        }
        // Full-frame discovery remains available to find even after a targeted response.
        return snapshot with { Content = Clip(content.ToString(), BrowserToolLimits.MaxSnapshotChars), Elements = elements };
    }
}
