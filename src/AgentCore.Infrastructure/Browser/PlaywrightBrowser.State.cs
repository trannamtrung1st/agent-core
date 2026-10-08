using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;
namespace AgentCore.Infrastructure.Browser;

public sealed partial class PlaywrightBrowser
{
    private sealed record BrowserRouteRule(string Id, string Url, string Action, int Status, string Body);
    private async Task<BrowserCommandResult> StateCommandAsync(SessionBrowser session, BrowserCommand command, CancellationToken ct, Func<Task, Task> action)
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
        if (command.Tool == "browser.network_state") { ct.ThrowIfCancellationRequested(); await MutateContextAsync(session, session.Context.SetOfflineAsync(args.GetProperty("online").GetBoolean() == false), ct); session.Offline = !args.GetProperty("online").GetBoolean(); ct.ThrowIfCancellationRequested(); return Data(new { status = "ok" }); }
        if (command.Tool == "browser.cookies")
        {
            var host = new Uri(origin).Host;
            var cookies = (await session.Context.CookiesAsync().WaitAsync(ct)).Where(c =>
                string.Equals(host, c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                || (c.Domain.StartsWith('.') && host.EndsWith(c.Domain, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (operation == "list")
            {
                var secrets = await CollectSecretsAsync(session, ct);
                string Safe(string text) => Clip(Redact(text, secrets, session.ProtectedValues), 128);
                return Data(new { cookies = cookies.Take(50).Select(c => new { name = Safe(c.Name), domain = Safe(c.Domain), path = Safe(c.Path), expires = c.Expires, httpOnly = c.HttpOnly, secure = c.Secure }) });
            }
            if (operation is not ("delete" or "clear")) return new("invalid");
            var name = String(args, "name");
            if (operation == "delete" && name is null) return new("invalid");
            foreach (var domain in cookies.Where(c => operation == "clear" || c.Name == name).Select(c => c.Domain).Distinct(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                await MutateContextAsync(session, session.Context.ClearCookiesAsync(new BrowserContextClearCookiesOptions { Name = operation == "delete" ? name : null, Domain = domain }), ct);
            }
            return Data(new { status = "ok" });
        }
        if (command.Tool is "browser.local_storage" or "browser.session_storage")
        {
            if (operation is not ("list" or "get" or "set" or "delete" or "clear")) return new("invalid");
            var key = String(args, "key"); var value = String(args, "value");
            if (operation is "get" or "set" or "delete" && key is null) return new("invalid");
            if (operation == "set" && (value is null || Regex.IsMatch(key!, "password|token|secret|auth|cookie|key|credential", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return new("forbidden");
            var secrets = await CollectSecretsAsync(session, ct);
            ct.ThrowIfCancellationRequested();
            var evaluation = session.Page.EvaluateAsync<JsonElement>("""
                a=>{const s=a.session?sessionStorage:localStorage;
                 if(a.operation==='set')s.setItem(a.key,a.value);
                 else if(a.operation==='delete')s.removeItem(a.key);
                 else if(a.operation==='clear')s.clear();
                 const read=k=>/password|token|secret|auth|cookie|key|credential/i.test(k)?'[redacted]':(s.getItem(k)||'');
                 if(a.operation==='get')return {key:a.key,value:read(a.key)};
                 if(a.operation==='list')return {items:Object.keys(s).slice(0,20).map(key=>({key,value:read(key)}))};
                 return {status:'ok'};}
                """, new { session = command.Tool == "browser.session_storage", operation, key, value });
            if (operation is "set" or "delete" or "clear") await action(evaluation);
            var result = await evaluation.WaitAsync(ct);
            // Redact decoded strings before clipping and JSON escaping can hide a protected match.
            object SafeEntry(JsonElement entry) => new
            {
                key = Clip(Redact(entry.GetProperty("key").GetString() ?? "", secrets, session.ProtectedValues), 128),
                value = Clip(Redact(entry.GetProperty("value").GetString() ?? "", secrets, session.ProtectedValues), 256)
            };
            return operation switch
            {
                "get" => Data(SafeEntry(result)),
                "list" => Data(new { items = result.GetProperty("items").EnumerateArray().Select(SafeEntry) }),
                _ => Data(new { status = "ok" })
            };
        }
        return new("unsupported_operation");
    }
}
