using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

internal static class PlaywrightChromiumReadiness
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool? _installed;

    public static async Task<bool> InstalledAsync(CancellationToken cancellationToken)
    {
        if (_installed is bool cached)
        {
            return cached;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_installed is bool cachedAgain)
            {
                return cachedAgain;
            }

            var installed = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            _installed = installed;
            return installed;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        IPlaywright? playwright = null;
        try
        {
            playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var path = playwright.Chromium.ExecutablePath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is PlaywrightException or TimeoutException or IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            playwright?.Dispose();
        }
    }
}
