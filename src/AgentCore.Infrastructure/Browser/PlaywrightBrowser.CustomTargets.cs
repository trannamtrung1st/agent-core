using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class PlaywrightBrowser
{
    // Provider-owned discovery only. The model supplies text/ref, never these selectors or code.
    private const string CustomTargetEligible = """
        el => {
          if (!el || !['SPAN','DIV'].includes(el.tagName) || el.hasAttribute('role')) return false;
          if (el.closest('label,button,a[href],input,textarea,select,iframe,[role=button],[role=link],[role=treeitem],[role=checkbox],[role=radio],[role=tab],[aria-hidden=true],[aria-disabled=true],[inert]')) return false;
          if (el.querySelector('input,textarea,select,iframe')) return false;
          const style = getComputedStyle(el);
          if (!el.getClientRects().length || style.visibility !== 'visible' || style.pointerEvents === 'none') return false;
          const text = (el.textContent || '').replace(/\s+/g,' ').trim();
          if (!text || text.length > 200 || el.children.length) return false;
          return style.cursor === 'pointer' || typeof el.onclick === 'function' || (el.hasAttribute('tabindex') && el.tabIndex >= 0);
        }
        """;

    private const string CustomLeafSelector = "span:not(:has(*)),div:not(:has(*))";

    private async Task AddCustomTargetsAsync(SessionBrowser session, Guid sessionId, ILocator root,
        List<BrowserElement> elements, StringBuilder content, IReadOnlyList<string> secrets, int depth, int limit, CancellationToken ct)
    {
        if (elements.Count >= limit) return;
        // One bounded read, rather than a round-trip per caption. Leaf text and fixed
        // provider selectors give the same order as exact native text Locators.
        var script = "root => { const eligible=(" + CustomTargetEligible + "); const describe=(" + DescribeElement + ");" + """
            const nodes=root.querySelectorAll('span,div'); const counts=new Map(); const targets=[];
            let bytes=0; let truncated=nodes.length>=16384;
            for(let i=-1;i<Math.min(nodes.length,16383);i++) {
              const el=i<0?root:nodes[i]; if(el.children.length) continue;
              const text=(el.textContent||'').replace(/\s+/g,' ').trim();
              if(!text || text.length>200) continue;
              const ordinal=counts.get(text)||0; counts.set(text,ordinal+1);
              if(!eligible(el)) continue;
              const description=describe(el); if(description==='null') continue;
              const ancestors=[];
              for(let p=el.parentElement,level=0;p && level<32;p=p.parentElement,level++) {
                const role=p.getAttribute('role'); const name=p.getAttribute('aria-label');
                if(role) ancestors.unshift({role,name:(name||'').slice(0,200)});
                if(p===root) break;
              }
              const entry={text,ordinal,isRoot:el===root,ancestors};
              bytes+=new TextEncoder().encode(JSON.stringify(entry)).length;
              if(bytes>512*1024 || targets.length>=4096) {truncated=true;continue;}
              targets.push(entry);
            }
            return JSON.stringify({truncated,targets:targets.map(t=>({...t,count:counts.get(t.text)}))}); }
            """;
        var raw = await root.EvaluateAsync<string>(script).WaitAsync(ct);
        using var candidates = JsonDocument.Parse(raw);
        session.IndexTruncated |= candidates.RootElement.GetProperty("truncated").GetBoolean();
        var ancestorIndex = elements.GroupBy(e => (e.Role, e.Name)).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var uniqueRoles = elements.GroupBy(e => e.Role).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        foreach (var candidate in candidates.RootElement.GetProperty("targets").EnumerateArray())
        {
            var name = candidate.GetProperty("text").GetString()!;
            var safeName = Redact(name, secrets, session.ProtectedValues);
            var lineage = candidate.GetProperty("ancestors").EnumerateArray().Select(a =>
                ancestorIndex.GetValueOrDefault((a.GetProperty("role").GetString()!, Redact(a.GetProperty("name").GetString()!, secrets, session.ProtectedValues)))
                    ?? (a.GetProperty("name").GetString()!.Length == 0 ? uniqueRoles.GetValueOrDefault(a.GetProperty("role").GetString()!) : null))
                .Where(e => e is not null).Cast<BrowserElement>().ToArray();
            var isRoot = candidate.GetProperty("isRoot").GetBoolean();
            var level = isRoot ? 0 : lineage.LastOrDefault()?.Depth + 1 ?? 0;
            if (level >= depth) continue;
            if (elements.Count >= limit) { session.IndexTruncated = true; break; }
            var semantic = isRoot ? root : root.GetByText(name, new() { Exact = true }).And(root.Locator(CustomLeafSelector));
            var target = isRoot ? root : semantic.Nth(candidate.GetProperty("ordinal").GetInt32());
            var token = MintToken();
            _refs[token] = new(sessionId, session.Generation, target, ["click"], semantic,
                isRoot ? 1 : candidate.GetProperty("count").GetInt32(), CustomTarget: true);
            var ancestors = Clip(string.Join(" > ", lineage.Select(e => e.Role + " " + e.Name)), 1200);
            elements.Add(new(token, "generic", safeName, ["click"], Ancestors: ancestors, Depth: level,
                AncestorRefs: lineage.Select(e => e.Ref).ToArray(), SearchText: safeName));
            content.Append(' ', level * 2).Append("- generic ").Append(JsonSerializer.Serialize(safeName)).Append(" [ref=").Append(token).AppendLine("]");
        }
    }
}
