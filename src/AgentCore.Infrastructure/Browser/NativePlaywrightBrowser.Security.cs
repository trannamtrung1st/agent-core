using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using SkiaSharp;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser
{
    // Fixed target-local classification only: native Locators own discovery, naming and actionability.
    private const string DescribeElement = """
        el => {
          const source = el.control || el;
          const tag = source.tagName.toLowerCase(), type = (source.getAttribute('type') || '').toLowerCase();
          const bounded = value => value.length > 65536 ? '[redacted oversized value]' : value;
          const name = (source.getAttribute('aria-label') || Array.from(source.labels || []).map(l=>l.textContent).join(' ') || source.textContent || '').trim();
          const metadata = [source.id, source.getAttribute('name'), source.getAttribute('autocomplete'), name].filter(Boolean).join(' ');
          const words = metadata.replace(/([a-z])([A-Z])/g,'$1 $2').replace(/[-_]+/g,' ').toLowerCase();
          const sensitive = type === 'hidden' || /password|passwd|passcode|secret|token|api ?key|access ?key|private ?key|authorization|one time code|otp/i.test(metadata) || /password|passwd|passcode|secret|token|api ?key|access ?key|private ?key|authorization|one time code|otp/i.test(words);
          if (type !== 'password' && sensitive) return 'null';
          const role = source.getAttribute('role') || (type === 'password' || tag === 'textarea' || tag === 'input' && !['checkbox','radio','file','submit','button'].includes(type) ? 'textbox' : tag === 'button' ? 'button' : tag === 'a' ? 'link' : tag === 'select' ? 'combobox' : /^h[1-6]$/.test(tag) ? 'heading' : type === 'checkbox' || type === 'radio' ? type : 'generic');
          const actions = type === 'password' ? ['fill_credential'] : type === 'file' ? ['upload'] : tag === 'select' ? ['select'] : ['checkbox','radio','switch'].includes(role) ? ['check','uncheck','click'] : role === 'textbox' || source.isContentEditable ? ['fill','press'] : ['heading','tree','grid','group','region','main','status'].includes(role) ? [] : ['click'];
          const state = {};
          if (type !== 'password' && !sensitive) {
            if (tag === 'select') state.selectedText = Array.from(source.selectedOptions).map(o=>o.label).join(', ');
            else if (type === 'checkbox' || type === 'radio') state.checked = source.checked;
            else if (['checkbox','radio','switch'].includes(role)) state.checked = source.getAttribute('aria-checked') === 'true';
            else if (role === 'textbox') state.value = String(source.value || '');
          }
          if (state.value !== undefined) state.value = bounded(state.value);
          if (state.selectedText !== undefined) state.selectedText = bounded(state.selectedText);
          return JSON.stringify({ role, name: bounded(name), actions, state });
        }
        """;
    private const string OrdinaryElement = "el => { if (!el || el.matches('iframe')) return false; if (el===el.ownerDocument.body || el===el.ownerDocument.documentElement) return true; const d=(" + DescribeElement + ")(el);return d!=='null' && !JSON.parse(d).actions.includes('fill_credential'); }";

    private const string ReadSecrets = """
        () => {
          const items = [];
          const push = (kind, key, value) => items.push({ kind, key: key || "", value: value || "" });
          try {
            for (let i = 0; i < localStorage.length; i++) {
              const key = localStorage.key(i);
              push("storage", key, localStorage.getItem(key));
            }
          } catch { }
          try {
            for (let i = 0; i < sessionStorage.length; i++) {
              const key = sessionStorage.key(i);
              push("storage", key, sessionStorage.getItem(key));
            }
          } catch { }
          document.querySelectorAll('input,textarea,select,[contenteditable=true]').forEach(el => {
            const metadata=[el.type,el.id,el.name,el.getAttribute('autocomplete'),el.getAttribute('aria-label'),...Array.from(el.labels||[]).map(l=>l.textContent)].join(' ');
            const words=metadata.replace(/([a-z])([A-Z])/g,'$1 $2').replace(/[-_]+/g,' ');
            if(/password|passwd|passcode|secret|token|api.?key|private.?key|access.?key|one.?time.?code|otp/i.test(metadata+' '+words))
                push("password", "", el.value || el.textContent || "");
          });
          return JSON.stringify(items);
        }
        """;

    public async ValueTask<BrowserResult> FillCredentialAsync(Guid sessionId, BrowserTarget target,
        Func<string, CancellationToken, ValueTask<string>> resolve, CancellationToken ct = default)
    {
        await using var held = await EnterInteractiveAsync(sessionId, ct);
        if (!IsAvailable || !_sessions.TryGetValue(sessionId, out var session)) return Result("provider_unavailable");
        await session.Gate.WaitAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(OperationTimeout);
        var token = deadline.Token;
        Task? active = null;
        var attempted = false;
        var confirmed = false;
        BrowserResult Outcome(BrowserResult result) => result with { EffectAttempted = attempted, EffectConfirmedBySdk = confirmed };
        try
        {
            if (session.Dialog is not null) return Result("dialog_pending");
            var locator = await ResolveTargetAsync(session, target, true, token);
            if (!await locator.EvaluateAsync<bool>("el => el.matches('input[type=password]')").WaitAsync(token)) return Result("unsupported_operation");
            if (await ClassifyInterventionAsync(session.Page, token) != BrowserInterventionKind.None) return Result("user_intervention_required");
            var pageUrl = session.Page.Url;
            var generation = session.Generation;
            var frameUrl = await locator.EvaluateAsync<string>("el => el.ownerDocument.location.href").WaitAsync(token);
            if (!Uri.TryCreate(frameUrl, UriKind.Absolute, out var uri)) return Result("target_denied");
            var value = await resolve(uri.GetLeftPart(UriPartial.Authority), token);
            if (!session.ProtectedValues.TryRegister(value)) return Result("user_intervention_required");
            // Secret resolution may await an external owner; recheck current semantics and exact frame before dispatch.
            locator = await ResolveTargetAsync(session, target, true, token);
            if (session.Page.Url != pageUrl || generation != session.Generation
                || await locator.EvaluateAsync<string>("el => el.ownerDocument.location.href").WaitAsync(token) != frameUrl)
                return Result("action_not_confirmed");
            if (!await locator.EvaluateAsync<bool>("el => el.matches('input[type=password]')").WaitAsync(token)
                || await ClassifyInterventionAsync(session.Page, token) != BrowserInterventionKind.None) return Result("user_intervention_required");
            attempted = true;
            active = locator.FillAsync(value, new() { Timeout = TimeoutMs() });
            await active.WaitAsync(token);
            confirmed = true;
            active = null;
            return (await CaptureWithRetryAsync(session, sessionId, "credential", BrowserSnapshotSettle.None, null, token))
                with { EffectAttempted = true, EffectConfirmedBySdk = true };
        }
        catch (BrowserTargetException ex) { return Outcome(Result(ex.Code)); }
        catch (BrowserTargetDeniedException) { return Outcome(Result("target_denied")); }
        catch (OperationCanceledException)
        {
            if (active is { IsCompleted: false }) await FencePageAsync(session, sessionId);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return Outcome(Result("timeout"));
        }
        catch (PlaywrightException ex) { return Outcome(Result(BrowserFailureClassifier.Classify(ex.Message).Code)); }
        finally { session.Gate.Release(); }
    }

    private async Task<IReadOnlyList<string>> CollectSecretsAsync(SessionBrowser session, CancellationToken cancellationToken, IPage? page = null)
    {
        var secrets = new List<string>(session.ProtectedValues);
        var cookies = await session.Context.CookiesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var cookie in cookies)
        {
            ConsiderSecret(secrets, "cookie", cookie.Name, cookie.Value);
        }

        foreach (var frame in (page ?? session.Page).Frames)
        {
            if (frame != session.Page.MainFrame && !Allows(session, frame.Url, true)) continue;
            var storedJson = await frame.EvaluateAsync<string>(ReadSecrets).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(storedJson)) continue;
            foreach (var item in JsonSerializer.Deserialize<List<BrowserSecretItem>>(storedJson, SecretJson) ?? [])
                ConsiderSecret(secrets, item.Kind, item.Key, item.Value);
        }

        secrets.Sort(static (left, right) => right.Length.CompareTo(left.Length));
        return secrets;
    }

    private static readonly JsonSerializerOptions SecretJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static void ConsiderSecret(List<string> secrets, string? kind, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var credential = string.Equals(kind, "password", StringComparison.OrdinalIgnoreCase) || IsCredentialKey(key);
        if (!credential && value.Trim().Length < 4)
        {
            return;
        }

        AddSecret(secrets, value);
    }

    private static bool IsCredentialKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var name = key.ToLowerInvariant();
        return name.Contains("token", StringComparison.Ordinal)
            || name.Contains("auth", StringComparison.Ordinal)
            || name.Contains("secret", StringComparison.Ordinal)
            || name.Contains("password", StringComparison.Ordinal)
            || name.Contains("session", StringComparison.Ordinal)
            || name.Contains("jwt", StringComparison.Ordinal)
            || name.Contains("credential", StringComparison.Ordinal);
    }

    private static void AddSecret(List<string> secrets, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || secrets.Contains(value, StringComparer.Ordinal))
        {
            return;
        }

        secrets.Add(value);
    }

    private static BrowserControlState? ReadControlState(string described, string name, IReadOnlyList<string> secrets, IReadOnlyList<string> protectedValues)
    {
        if (SensitiveControl(name))
        {
            return null;
        }

        using var document = JsonDocument.Parse(described);
        if (!document.RootElement.TryGetProperty("state", out var state)
            || state.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? value = null;
        bool? checkedState = null;
        string? selected = null;
        if (state.TryGetProperty("value", out var valueProperty) && valueProperty.ValueKind == JsonValueKind.String)
        {
            value = Clip(Redact(valueProperty.GetString(), secrets, protectedValues), BrowserToolLimits.MaxFillLength);
        }

        if (state.TryGetProperty("checked", out var checkedProperty)
            && (checkedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            checkedState = checkedProperty.GetBoolean();
        }

        if (state.TryGetProperty("selectedText", out var selectedProperty) && selectedProperty.ValueKind == JsonValueKind.String)
        {
            selected = Clip(Redact(selectedProperty.GetString(), secrets, protectedValues), BrowserToolLimits.MaxFillLength);
        }

        if (value is null && checkedState is null && selected is null)
        {
            return null;
        }

        return new BrowserControlState(value, checkedState, selected);
    }

    private static bool SensitiveControl(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var haystack = name.ToLowerInvariant();
        string[] terms =
        [
            "password",
            "passwd",
            "passcode",
            "secret",
            "token",
            "api key",
            "apikey",
            "access key",
            "private key",
            "client secret",
            "authorization",
            "one-time-code",
            "otp"
        ];
        return terms.Any(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    private static string Redact(string? text, IReadOnlyList<string> secrets, IReadOnlyList<string>? protectedValues = null)
    {
        var current = text ?? string.Empty;
        foreach (var value in protectedValues ?? [])
            if (value.Length > 0) current = current.Replace(value, "[redacted]", StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            current = secret.Length >= 4
                ? current.Replace(secret, "[redacted]", StringComparison.Ordinal)
                : RedactBounded(current, secret);
        }

        return current;
    }

    private static string RedactBounded(string text, string secret)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(secret, index, StringComparison.Ordinal);
            if (found < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var beforeOk = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            var after = found + secret.Length;
            var afterOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);
            builder.Append(text, index, found - index);
            builder.Append(beforeOk && afterOk ? "[redacted]" : secret);
            index = after;
        }

        return builder.ToString();
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private const string MaskSensitiveScript = """
        () => {
          const sensitiveTerms = ["password", "passwd", "passcode", "secret", "token", "api key", "apikey", "access key", "private key", "client secret", "authorization", "one-time-code", "otp"];
          const nodes = document.querySelectorAll("input, textarea, select, [data-sensitive]");
          let count = 0;
          for (const node of nodes) {
            const sourceType = (node.getAttribute("type") || "").toLowerCase();
            const autocomplete = (node.getAttribute("autocomplete") || "").toLowerCase();
            const haystack = [node.id, node.getAttribute("name"), autocomplete, node.getAttribute("aria-label"), node.getAttribute("placeholder")]
              .filter(Boolean).join(" ").toLowerCase();
            const sensitive = node.hasAttribute("data-sensitive")
              || sourceType === "password"
              || autocomplete.includes("one-time-code")
              || autocomplete === "username"
              || autocomplete === "current-password"
              || autocomplete.startsWith("cc-")
              || sensitiveTerms.some(term => haystack.includes(term));
            if (!sensitive) continue;
            const rect = node.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) continue;
            const mask = document.createElement("div");
            mask.setAttribute("data-agent-mask", "1");
            mask.style.position = "absolute";
            mask.style.left = (rect.left + scrollX) + "px";
            mask.style.top = (rect.top + scrollY) + "px";
            mask.style.width = rect.width + "px";
            mask.style.height = rect.height + "px";
            mask.style.background = "#111111";
            mask.style.zIndex = "2147483647";
            document.documentElement.appendChild(mask);
            count += 1;
          }
          return count;
        }
        """;

    private const string ClearMaskScript = """
        () => { for (const node of document.querySelectorAll("[data-agent-mask]")) node.remove(); }
        """;

    private static string SafeBrowserUrl(string url, IReadOnlyList<string> secrets, IReadOnlyList<string> protectedValues)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(url, @"(?<=://)[^/@]+@", "[redacted]@");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"([?&#])([^=&#]+)=([^&#]*)", match =>
        {
            var key = Uri.UnescapeDataString(match.Groups[2].Value).Replace("+", " ");
            var words = System.Text.RegularExpressions.Regex.Replace(key, "([a-z])([A-Z])", "$1 $2").Replace('_', ' ').Replace('-', ' ');
            return IsCredentialKey(key) || SensitiveControl(key) || SensitiveControl(words)
                || key.Equals("code", StringComparison.OrdinalIgnoreCase) || key.Equals("key", StringComparison.OrdinalIgnoreCase)
                || key.Equals("sig", StringComparison.OrdinalIgnoreCase) || words.Contains("signature", StringComparison.OrdinalIgnoreCase)
                ? match.Groups[1].Value + match.Groups[2].Value + "=[redacted]" : match.Value;
        });
        return Redact(safe, secrets.Select(Uri.EscapeDataString).Concat(secrets).ToArray(), protectedValues);
    }

    private sealed record BrowserSecretItem(string? Kind, string? Key, string? Value);
}
