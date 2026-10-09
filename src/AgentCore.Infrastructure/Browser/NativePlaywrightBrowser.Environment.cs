using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser
{
    private BrowserNewContextOptions ContextOptions(IPlaywright driver)
    {
        var environment = _options.Environment;
        BrowserNewContextOptions result;
        if (environment.Device is { } device)
        {
            if (!driver.Devices.TryGetValue(device, out var descriptor)) throw new BrowserLaunchException();
            result = new BrowserNewContextOptions(descriptor);
        }
        else result = new();
        if (environment.ViewportWidth is not null || environment.ViewportHeight is not null)
            result.ViewportSize = new()
            {
                Width = environment.ViewportWidth ?? result.ViewportSize?.Width ?? 1280,
                Height = environment.ViewportHeight ?? result.ViewportSize?.Height ?? 720
            };
        if (result.ViewportSize is { } viewport && (viewport.Width is < 320 or > 1920 || viewport.Height is < 240 or > 1080))
            throw new BrowserLaunchException();
        if (environment.DeviceScaleFactor is < 1 or > 4 || environment.DeviceScaleFactor is { } scale && !float.IsFinite(scale))
            throw new BrowserLaunchException();
        if (environment.Locale is { } locale)
        {
            // The .NET host uses invariant globalization; Chromium owns locale resolution.
            if (locale.Length > 64 || !Regex.IsMatch(locale, "^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant))
                throw new BrowserLaunchException();
            result.Locale = locale;
        }
        if (environment.TimezoneId is { } zone)
        {
            if (zone.Length > 128) throw new BrowserLaunchException();
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(zone); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new BrowserLaunchException(); }
            result.TimezoneId = zone;
        }
        result.IsMobile = environment.IsMobile ?? result.IsMobile;
        result.HasTouch = environment.HasTouch ?? result.HasTouch;
        result.DeviceScaleFactor = environment.DeviceScaleFactor ?? result.DeviceScaleFactor;
        result.AcceptDownloads = true;
        // Native routing cannot inspect requests handled by a service worker.
        result.ServiceWorkers = ServiceWorkerPolicy.Block;
        return result;
    }

    private sealed record BrowserEnvironment(string? Device, string Locale, string TimezoneId,
        bool IsMobile, bool HasTouch, float DeviceScaleFactor);

    private BrowserEnvironment DescribeEnvironment(BrowserNewContextOptions options) => new(
        _options.Environment.Device, options.Locale ?? "en-US", options.TimezoneId ?? TimeZoneInfo.Local.Id,
        options.IsMobile ?? false, options.HasTouch ?? false, options.DeviceScaleFactor ?? 1);

    private BrowserResult Configuration(SessionBrowser? session)
    {
        PageEmulateMediaOptions? media = null;
        if (session is not null) lock (session.PopupGate) session.Media.TryGetValue(session.Page, out media);
        static string? Override<T>(T? value) where T : struct, Enum => value?.ToString() is { } name
            ? name == "Null" ? null : name == "NoPreference" ? "no-preference" : name.ToLowerInvariant() : null;
        return new(null, DataJson: JsonSerializer.Serialize(new
        {
            provider = Provider.ProviderId, engine = Provider.Engine, available = IsAvailable,
            supportedFeatures = Provider.SupportedFeatures.Select(f => f.ToString()).Order(StringComparer.Ordinal),
            contextOpen = session is not null,
            environment = session is null ? null : new
            {
                session.Environment.Device, session.Environment.Locale, session.Environment.TimezoneId,
                session.Environment.IsMobile, session.Environment.HasTouch, session.Environment.DeviceScaleFactor,
                viewport = session.Page.ViewportSize is { } size ? new { width = size.Width, height = size.Height } : null,
                mediaOverrides = new { media = Override(media?.Media), colorScheme = Override(media?.ColorScheme),
                    reducedMotion = Override(media?.ReducedMotion), forcedColors = Override(media?.ForcedColors), contrast = Override(media?.Contrast) },
                online = !session.Offline,
                geolocationGranted = session.GeolocationOrigin is not null
            },
            policy = new
            {
                headless = _policy.Headless, profileMode = _policy.ProfileMode.ToString(), policyMode = _policy.PolicyMode.ToString(),
                interactionMode = _policy.InteractionMode.ToString(), serviceWorkers = "blocked",
                websocketOrigins = "resource origin policy",
                navigationOriginCount = _policy.NavigationOrigins.Count,
                interactionOriginCount = _policy.EffectiveInteractionOrigins.Count, resourceOriginCount = _policy.EffectiveResourceOrigins.Count,
                originRestrictionsApply = true, permissions = "Geolocation requires exact-origin approval. Initial/same-origin set preserves overrides; clear or origin change resets all permission overrides (Playwright limitation).",
                contextSettings = "Device, locale, timezone and touch settings apply at context creation; active contexts are retained."
            },
            limits = new { snapshotBytes = BrowserToolLimits.MaxSnapshotBytes, captureBytes = BrowserToolLimits.MaxCaptureBytes, downloadBytes = BrowserToolLimits.MaxDownloadBytes }
        }, JsonSerializerOptions.Web));
    }

    private void RouteWebSocket(SessionBrowser session, IWebSocketRoute socket)
    {
        // Inspect destination only. Message interception/code execution remains unavailable.
        if (Uri.TryCreate(socket.Url, UriKind.Absolute, out var target) && target.Scheme is "ws" or "wss")
        {
            var httpTarget = new UriBuilder(target) { Scheme = target.Scheme == "wss" ? "https" : "http", Port = target.Port }.Uri;
            if (Allows(session, httpTarget.AbsoluteUri, false))
            {
                socket.ConnectToServer();
                return;
            }
        }
        var close = socket.CloseAsync(new() { Code = 1008, Reason = "Browser target is not allowed." });
        lock (session.PopupGate) session.PopupCloses.Add(close);
        _ = close.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task<BrowserResult> GeolocationAsync(SessionBrowser session, BrowserSetGeolocation args, CancellationToken ct)
    {
        var operation = args.Operation;
        if (operation is not ("set" or "clear") || !Uri.TryCreate(args.Origin, UriKind.Absolute, out var target)
            || target.UserInfo.Length != 0 || target.PathAndQuery != "/" || target.Fragment.Length != 0) return new("invalid");
        var origin = target.GetLeftPart(UriPartial.Authority);
        if (!Uri.TryCreate(session.Page.Url, UriKind.Absolute, out var current)
            || !string.Equals(origin, current.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || !Allows(session, origin, false)) return new("target_denied");
        Geolocation? location = null;
        if (operation == "set")
        {
            if (args.Latitude is not double latitude || args.Longitude is not double longitude
                || !double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180) return new("invalid");
            var accuracy = args.Accuracy ?? 0;
            if (!double.IsFinite(accuracy) || accuracy is < 0 or > 10000) return new("invalid");
            location = new() { Latitude = (float)latitude, Longitude = (float)longitude, Accuracy = (float)accuracy };
        }
        else if (args.Latitude is not null || args.Longitude is not null || args.Accuracy is not null) return new("invalid");

        // A later grant must never finish after cancellation releases the owner gate.
        ct.ThrowIfCancellationRequested();
        // Playwright can only clear all overrides. Avoid this on initial/same-origin updates.
        var permissionsReset = operation == "clear" || session.GeolocationOrigin is { } previous && previous != origin;
        if (permissionsReset)
        {
            await MutateContextAsync(session, session.Context.ClearPermissionsAsync(), ct);
            session.GeolocationOrigin = null;
        }
        await MutateContextAsync(session, session.Context.SetGeolocationAsync(location), ct);
        if (location is not null && !ct.IsCancellationRequested)
        {
            await MutateContextAsync(session, session.Context.GrantPermissionsAsync(["geolocation"], new() { Origin = origin }), ct);
            session.GeolocationOrigin = origin;
        }
        if (ct.IsCancellationRequested)
        {
            await session.Context.ClearPermissionsAsync();
            await session.Context.SetGeolocationAsync(null);
            session.GeolocationOrigin = null;
            ct.ThrowIfCancellationRequested();
        }
        return new(null, DataJson: JsonSerializer.Serialize(new { status = "ok", permissionsReset }));

    }

    private async Task MutateContextAsync(SessionBrowser session, Task mutation, CancellationToken ct)
    {
        try { await mutation.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            // Context changes outlive a page. Close the context before releasing its owner gate.
            await CloseQuietlyAsync(session.Context);
            ForgetClosed(session);
            _ = mutation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
    }

    private async Task EmulateMediaAsync(SessionBrowser session, BrowserEmulateMedia args, CancellationToken ct, Func<Task, Task> action)
    {
        static T? Setting<T>(bool specified, string? value) where T : struct, Enum => !specified ? null
            : value is null ? Enum.Parse<T>("Null") : Enum.Parse<T>(value.Replace("-", ""), true);
        var change = new PageEmulateMediaOptions
        {
            Media = Setting<Media>(args.MediaSpecified, args.Media),
            ColorScheme = Setting<ColorScheme>(args.ColorSchemeSpecified, args.ColorScheme),
            ReducedMotion = Setting<ReducedMotion>(args.ReducedMotionSpecified, args.ReducedMotion),
            ForcedColors = Setting<ForcedColors>(args.ForcedColorsSpecified, args.ForcedColors),
            Contrast = Setting<Contrast>(args.ContrastSpecified, args.Contrast),
        };
        ct.ThrowIfCancellationRequested();
        await action(session.Page.EmulateMediaAsync(change));
        lock (session.PopupGate)
        {
            if (!session.Media.TryGetValue(session.Page, out var effective)) session.Media[session.Page] = effective = new();
            effective.Media = change.Media ?? effective.Media;
            effective.ColorScheme = change.ColorScheme ?? effective.ColorScheme;
            effective.ReducedMotion = change.ReducedMotion ?? effective.ReducedMotion;
            effective.ForcedColors = change.ForcedColors ?? effective.ForcedColors;
            effective.Contrast = change.Contrast ?? effective.Contrast;
        }
        ct.ThrowIfCancellationRequested();
    }
}
