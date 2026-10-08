using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;
namespace AgentCore.Infrastructure.Browser;

public sealed partial class PlaywrightBrowser
{
    private sealed record BrowserRouteRule(string Id, string Url, string Action, int Status, string Body);
    private async Task<BrowserCommandResult> StateCommandAsync(SessionBrowser session, BrowserCommand command, CancellationToken ct)
    {
        var args = command.Arguments;
        BrowserCommandResult Data(object value) => new(null, DataJson: JsonSerializer.Serialize(value));
        var origin = new Uri(session.Page.Url).GetLeftPart(UriPartial.Authority);
        var operation = String(args, "operation");
        if (command.Tool == "browser.route")
        {
            var url = String(args, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Allows(session, uri.AbsoluteUri, false)) return new("target_denied");
            if (session.Rules.Count >= 16) return new("invalid");
            var rule = new BrowserRouteRule("route_" + Guid.NewGuid().ToString("N"), uri.AbsoluteUri, String(args, "action")!, Int(args, "status", 200), String(args, "body") ?? "");
            lock (session.PopupGate) session.Rules.Add(rule);
            return Data(new { ruleRef = rule.Id });
        }
        if (command.Tool == "browser.routes") { lock (session.PopupGate) return Data(new { rules = session.Rules.Select(r => new { ruleRef = r.Id, origin = SafeNetworkUrl(r.Url), action = r.Action }) }); }
        if (command.Tool == "browser.unroute") { lock (session.PopupGate) { var removed = session.Rules.RemoveAll(r => r.Id == String(args, "ruleRef")); return removed == 0 ? new("invalid") : Data(new { status = "ok" }); } }
        if (command.Tool == "browser.network_state") { await session.Context.SetOfflineAsync(args.GetProperty("online").GetBoolean() == false).WaitAsync(ct); return Data(new { status = "ok" }); }
        if (command.Tool == "browser.cookies")
        {
            var cookies = await session.Context.CookiesAsync([origin]).WaitAsync(ct);
            if (operation == "list") return Data(new { cookies = cookies.Take(50).Select(c => new { name = Clip(c.Name, 128), domain = Clip(c.Domain, 128), path = Clip(c.Path, 128), expires = c.Expires, httpOnly = c.HttpOnly, secure = c.Secure }) });
            if (operation == "delete")
            { var name = String(args, "name"); if (name is null) return new("invalid"); await session.Context.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Name = name, Domain = new Uri(origin).Host }).WaitAsync(ct); }
            else if (operation == "clear") await session.Context.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Domain = new Uri(origin).Host }).WaitAsync(ct);
            else return new("invalid");
            return Data(new { status = "ok" });
        }
        if (command.Tool is "browser.local_storage" or "browser.session_storage")
        {
            if (operation is not ("list" or "get" or "set" or "delete" or "clear")) return new("invalid");
            var key = String(args, "key"); var value = String(args, "value");
            if (operation is "get" or "set" or "delete" && key is null) return new("invalid");
            if (operation == "set" && (value is null || Regex.IsMatch(key!, "password|token|secret|auth|cookie|key|credential", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return new("forbidden");
            var secrets = await CollectSecretsAsync(session, ct);
            var result = await session.Page.EvaluateAsync<JsonElement>("""
                a=>{const s=a.session?sessionStorage:localStorage;
                 if(a.operation==='set')s.setItem(a.key,a.value);
                 else if(a.operation==='delete')s.removeItem(a.key);
                 else if(a.operation==='clear')s.clear();
                 const read=k=>/password|token|secret|auth|cookie|key|credential/i.test(k)?'[redacted]':(s.getItem(k)||'').slice(0,256);
                 if(a.operation==='get')return {key:a.key,value:read(a.key)};
                 if(a.operation==='list')return {items:Object.keys(s).slice(0,20).map(key=>({key:key.slice(0,128),value:read(key)}))};
                 return {status:'ok'};}
                """, new { session = command.Tool == "browser.session_storage", operation, key, value }).WaitAsync(ct);
            return new(null, DataJson: Redact(result.GetRawText(), secrets, session.ProtectedValues));
        }
        return new("unsupported_operation");
    }
}
