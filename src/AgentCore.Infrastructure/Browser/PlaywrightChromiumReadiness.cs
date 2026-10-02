using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

internal static class PlaywrightChromiumReadiness
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, bool> InstalledByTarget = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<bool> InstalledAsync(string? channel, CancellationToken cancellationToken)
    {
        var key = string.IsNullOrWhiteSpace(channel) ? string.Empty : channel.Trim();
        if (InstalledByTarget.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (InstalledByTarget.TryGetValue(key, out cached))
            {
                return cached;
            }

            var installed = await ProbeAsync(key, cancellationToken).ConfigureAwait(false);
            InstalledByTarget[key] = installed;
            return installed;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> ProbeAsync(string channelKey, CancellationToken cancellationToken)
    {
        IPlaywright? playwright = null;
        IBrowser? browser = null;
        try
        {
            playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(channelKey))
            {
                var path = playwright.Chromium.ExecutablePath;
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }

            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Channel = channelKey
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is PlaywrightException or TimeoutException or IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    await browser.CloseAsync().ConfigureAwait(false);
                }
                catch (PlaywrightException)
                {
                }
            }

            playwright?.Dispose();
        }
    }
}
