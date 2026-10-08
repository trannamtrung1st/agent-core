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

    private sealed record IndexedNative(string Content, IReadOnlyList<BrowserElement> Elements);

    private async Task<IndexedNative> IndexNativeAsync(SessionBrowser session, Guid sessionId, ILocator root,
        string native, IReadOnlyList<string> secrets, int depth, bool scoped, int limit, CancellationToken ct)
    {
        var content = new StringBuilder();
        var elements = new List<BrowserElement>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var ancestors = new List<(int Depth, string Text, string Ref)>();
        var pending = new List<(string Line, string Role, string? Name, int Depth, string Ancestors, ILocator Locator, string Ref, string[] AncestorRefs, int Ordinal)>();
        if (Encoding.UTF8.GetByteCount(native) > 512 * 1024)
        {
            var prefix = ToolJsonResults.ClipUtf8Prefix(native, 512 * 1024);
            native = prefix[..Math.Max(0, prefix.LastIndexOf('\n'))]; // Never expose a partial protected name.
            session.IndexTruncated = true;
        }
        var first = true;
        var seen = 0;
        foreach (var line in native.Split('\n'))
        {
            var level = line.TakeWhile(char.IsWhiteSpace).Count() / 2;
            ancestors.RemoveAll(a => a.Depth >= level);
            var match = Regex.Match(line, "^\\s*- ([a-z]+)(?: \"((?:[^\"\\\\]|\\\\.)*)\")?");
            if (!match.Success || !Enum.TryParse<AriaRole>(match.Groups[1].Value, true, out var role))
            { if (level < depth) content.AppendLine(Redact(line, secrets, session.ProtectedValues)); continue; }
            var name = match.Groups[2].Success ? Regex.Unescape(match.Groups[2].Value) : null;
            var key = role + "\n" + name;
            ordinals.TryGetValue(key, out var ordinal);
            if (!(scoped && first)) ordinals[key] = ordinal + 1;
            var locator = scoped && first ? root : root.GetByRole(role, new LocatorGetByRoleOptions { Name = name, Exact = true }).Nth(ordinal);
            first = false;
            var ancestry = Clip(string.Join(" > ", ancestors.Select(a => a.Text)), 1200);
            var token = MintToken();
            var ancestorRefs = ancestors.Select(a => a.Ref).ToArray();
            ancestors.Add((level, Redact(match.Groups[1].Value + " " + name, secrets, session.ProtectedValues), token));
            if (level >= depth) continue;
            if (++seen > limit) { session.IndexTruncated = true; break; }
            pending.Add((line, match.Groups[1].Value, name, level, ancestry, locator, token, ancestorRefs, ordinal));
        }
        var expectedMatches = pending.GroupBy(n => (n.Role, n.Name)).ToDictionary(g => g.Key, g => g.Count());
        // A depth/node limit can omit existing semantic duplicates. Establish their
        // native count now so omission alone is not mistaken for a newly ambiguous ref.
        if (scoped || session.IndexTruncated)
            foreach (var batch in expectedMatches.Keys.ToArray().Chunk(32))
            {
                var counts = await Task.WhenAll(batch.Select(key => root.GetByRole(Enum.Parse<AriaRole>(key.Role, true),
                    new LocatorGetByRoleOptions { Name = key.Name, Exact = true }).CountAsync().WaitAsync(ct)));
                for (var i = 0; i < batch.Length; i++) expectedMatches[batch[i]] = counts[i];
            }
        // Explicit native ARIA labels can be described in one read per role. Only use this
        // cache when every target of that role has an unambiguous explicit accessible name;
        // complex accessible-name computation continues through the exact native Locator.
        var cached = new Dictionary<(string Role, string Name), Queue<string>>();
        foreach (var role in pending.Where(n => !(scoped && n.Locator == root)).Select(n => n.Role).Distinct())
        {
            if (!Enum.TryParse<AriaRole>(role, true, out var ariaRole)) continue;
            var descriptions = await root.GetByRole(ariaRole).EvaluateAllAsync<string?>(
                "(els,selected) => { els=els.slice(0,4096); if(!els.every(el=>el.hasAttribute('aria-label') && !el.hasAttribute('aria-labelledby')))return null; const groups=new Map(); for(const el of els){const name=el.getAttribute('aria-label');if(!groups.has(name))groups.set(name,[]);groups.get(name).push(el);} return JSON.stringify(selected.flatMap(item=>{const el=groups.get(item.name)?.[item.ordinal];return el?[{name:item.name,description:(" + DescribeElement + ")(el)}]:[];})); }",
                pending.Where(n => n.Role == role && !(scoped && n.Locator == root)).Select(n => new { name = n.Name, ordinal = n.Ordinal }).ToArray()).WaitAsync(ct);
            if (descriptions is null) continue;
            using var document = JsonDocument.Parse(descriptions);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var key = (role, entry.GetProperty("name").GetString()!);
                if (!cached.TryGetValue(key, out var queue)) cached[key] = queue = new();
                queue.Enqueue(entry.GetProperty("description").GetString()!);
            }
        }
        // Bound remaining concurrent reads for computed accessible names.
        foreach (var batch in pending.Chunk(32))
        {
            var reads = batch.Select(node => !(scoped && node.Locator == root) && node.Name is not null && cached.TryGetValue((node.Role, node.Name), out var queue) && queue.Count > 0
                ? Task.FromResult(queue.Dequeue()) : node.Locator.EvaluateAsync<string>(DescribeElement).WaitAsync(ct)).ToArray();
            var descriptions = await Task.WhenAll(reads);
            for (var i = 0; i < batch.Length; i++)
            {
                var node = batch[i]; var described = descriptions[i];
                if (described is null or "null" or "") continue;
                using var doc = JsonDocument.Parse(described);
                var actions = doc.RootElement.GetProperty("actions").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var token = node.Ref;
                var semantic = scoped && node.Locator == root ? root : root.GetByRole(Enum.Parse<AriaRole>(node.Role, true),
                    new LocatorGetByRoleOptions { Name = node.Name, Exact = true });
                _refs[token] = new(sessionId, session.Generation, node.Locator, actions, semantic,
                    scoped && node.Locator == root ? 1 : expectedMatches[(node.Role, node.Name)]);
                var safeName = Redact(node.Name ?? doc.RootElement.GetProperty("name").GetString() ?? "", secrets, session.ProtectedValues);
                elements.Add(new(token, node.Role, Clip(safeName, BrowserToolLimits.MaxAccessibleNameLength), actions,
                    ReadControlState(described, safeName, secrets, session.ProtectedValues), node.Ancestors, node.Depth, node.AncestorRefs, Clip(Redact(node.Name ?? node.Line, secrets, session.ProtectedValues), 2048)));
                content.AppendLine(Redact(node.Line, secrets, session.ProtectedValues) + " [ref=" + token + "]");
            }
        }
        await AddCustomTargetsAsync(session, sessionId, root, elements, content, secrets, depth, limit, ct);
        return new(content.ToString(), elements);
    }

    private async Task<BrowserSnapshot> ScopeSnapshotAsync(SessionBrowser session, Guid sessionId,
        ILocator scope, int depth, string scopeRef, CancellationToken ct)
    {
        // Invalidate refs, but reuse the full native index without enumerating the whole page again.
        var previous = session.SnapshotIndex.Select(e => (Element: e, Live: _refs.GetValueOrDefault(e.Ref))).ToArray();
        RemoveRefs(sessionId);
        var scopeElement = previous.FirstOrDefault(p => p.Element.Ref == scopeRef).Element;
        var remapped = previous.ToDictionary(p => p.Element.Ref, _ => MintToken());
        var retained = new List<BrowserElement>();
        foreach (var pair in previous)
        {
            if (pair.Live is null) continue;
            var token = remapped[pair.Element.Ref];
            _refs[token] = pair.Live;
            retained.Add(pair.Element with { Ref = token, AncestorRefs = pair.Element.AncestorRefs?.Select(r => remapped.GetValueOrDefault(r, r)).ToArray() });
        }
        session.SnapshotId = "snap_" + Guid.NewGuid().ToString("N");
        var native = await scope.AriaSnapshotAsync(new LocatorAriaSnapshotOptions { Mode = AriaSnapshotMode.Default, Depth = depth, Timeout = TimeoutMs() }).WaitAsync(ct);
        var secrets = await CollectSecretsAsync(session, ct);
        var parsed = await IndexNativeAsync(session, sessionId, scope, native, secrets, depth, true, MaxIndexedNodes, ct);
        // Preserve the root's original semantic match fence: a scoped Nth Locator alone
        // always counts as one even if a duplicate target appears after this snapshot.
        var originalRoot = previous.FirstOrDefault(p => p.Element.Ref == scopeRef).Live;
        if (originalRoot is not null && parsed.Elements.FirstOrDefault() is { } refreshedRoot)
            _refs[refreshedRoot.Ref] = originalRoot;
        // Replace only this subtree's inspected nodes, keeping equally named nodes elsewhere.
        var replaced = previous.Where(p => p.Element.Ref == scopeRef ||
            p.Element.AncestorRefs?.Contains(scopeRef) == true && p.Element.Depth - (scopeElement?.Depth ?? 0) < depth)
            .Select(p => remapped[p.Element.Ref]).ToHashSet();
        var scopedByName = parsed.Elements.GroupBy(e => (e.Role, e.Name)).ToDictionary(g => g.Key, g => new Queue<BrowserElement>(g));
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in previous.Where(p => replaced.Contains(remapped[p.Element.Ref])))
            if (scopedByName.TryGetValue((pair.Element.Role, pair.Element.Name), out var queue) && queue.Count > 0)
                replacements[remapped[pair.Element.Ref]] = queue.Dequeue().Ref;
        // The provider subtree is relative to its root; the shared discovery index must
        // keep page-relative depth and outer ancestry through repeated/nested scopes.
        var outerRefs = scopeElement?.AncestorRefs?.Select(r => remapped.GetValueOrDefault(r, r)).ToArray() ?? [];
        var indexedScope = parsed.Elements.Select(e => e with {
            Depth = e.Depth + (scopeElement?.Depth ?? 0),
            Ancestors = string.Join(" > ", new[] { scopeElement?.Ancestors, e.Ancestors }.Where(a => !string.IsNullOrEmpty(a))),
            AncestorRefs = outerRefs.Concat(e.AncestorRefs ?? []).ToArray()
        });
        var merged = indexedScope.Concat(retained.Where(e => !replaced.Contains(e.Ref))
            .Select(e => e with {
                AncestorRefs = e.AncestorRefs?.Select(r => replacements.GetValueOrDefault(r, r)).ToArray()
            })).ToArray();
        session.IndexTruncated |= merged.Length > MaxIndexedNodes;
        session.SnapshotIndex = merged.Take(MaxIndexedNodes).ToArray();
        var indexedRefs = session.SnapshotIndex.Select(e => e.Ref).ToHashSet();
        foreach (var token in retained.Select(e => e.Ref).Concat(parsed.Elements.Select(e => e.Ref)).Where(r => !indexedRefs.Contains(r)))
            _refs.TryRemove(token, out _);
        // InnerText would enumerate and disclose deeper nodes omitted by provider depth.
        var text = depth < 32 ? "" : Redact(await scope.InnerTextAsync().WaitAsync(ct), secrets, session.ProtectedValues);
        return new(SafeBrowserUrl(session.Page.Url, secrets, session.ProtectedValues),
            Redact(await session.Page.TitleAsync().WaitAsync(ct), secrets, session.ProtectedValues),
            Clip(text, BrowserToolLimits.MaxVisibleTextLength), depth < 32 || text.Length > BrowserToolLimits.MaxVisibleTextLength,
            parsed.Elements, await ClassifyInterventionAsync(session.Page, ct), SnapshotId: session.SnapshotId, TabRef: FindPageId(session),
            Content: Clip(parsed.Content, BrowserToolLimits.MaxSnapshotChars), ContentTruncated: depth < 32 || parsed.Content.Length > BrowserToolLimits.MaxSnapshotChars,
            IndexTruncated: session.IndexTruncated, IndexedCount: session.SnapshotIndex.Count, CapturedNodeCount: parsed.Elements.Count, Scope: scopeRef);
    }
}
