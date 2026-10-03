using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

/// <summary>
/// Bounded wait for observable page quietness. A quiet interval is DOM mutation
/// silence plus no in-flight fetch or XMLHttpRequest activity seen through the
/// page's own wrappers. Other resource loads are not counted.
/// Continuously changing pages stop at the deadline and capture anyway.
/// </summary>
internal static class BrowserPageSettle
{
    /// <summary>Lets asynchronous work start before a quiet interval can succeed.</summary>
    public const int MinimumOpportunityMs = 300;

    /// <summary>Visible DOM and meaningful requests must stay quiet this long.</summary>
    public const int QuietIntervalMs = 450;

    /// <summary>Automatic settle after navigation or a page-changing action.</summary>
    public const int AutomaticDeadlineMs = 1600;

    public const int PollIntervalMs = 50;

    public const string InitScript = """
        (() => {
          if (window.__acSettle) return;
          const state = { generation: 0, inflight: 0 };
          window.__acSettle = state;
          const bump = () => { state.generation += 1; };
          const observer = new MutationObserver(bump);
          const arm = () => {
            const root = document.documentElement;
            if (!root || root.__acSettleObserved) return;
            root.__acSettleObserved = true;
            observer.observe(root, {
              subtree: true,
              childList: true,
              characterData: true,
              attributes: true,
              attributeFilter: ["class", "hidden", "aria-busy", "aria-hidden", "style"]
            });
          };
          if (document.documentElement) arm();
          else document.addEventListener("DOMContentLoaded", arm, { once: true });
          const originalFetch = window.fetch;
          if (typeof originalFetch === "function") {
            window.fetch = function (...args) {
              state.inflight += 1;
              return Promise.resolve(originalFetch.apply(this, args)).finally(() => {
                state.inflight = Math.max(0, state.inflight - 1);
              });
            };
          }
          const originalSend = XMLHttpRequest.prototype.send;
          XMLHttpRequest.prototype.send = function (...args) {
            state.inflight += 1;
            this.addEventListener("loadend", () => {
              state.inflight = Math.max(0, state.inflight - 1);
            }, { once: true });
            return originalSend.apply(this, args);
          };
        })();
        """;

    private const string ReadScript = """
        () => {
          const state = window.__acSettle;
          if (!state) return [0, 0];
          return [state.generation || 0, state.inflight || 0];
        }
        """;

    public static async Task<bool> WaitAsync(IPage page, int timeoutMs, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var deadline = started + Math.Max(0, timeoutMs);
        long? seen = null;
        long quietSince = 0;

        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Environment.TickCount64;
            var elapsed = now - started;
            var (generation, inflight) = await ReadAsync(page, cancellationToken).ConfigureAwait(false);
            var unstable = inflight > 0 || seen is null || generation != seen.Value;
            seen = generation;
            if (unstable || elapsed < MinimumOpportunityMs)
            {
                quietSince = 0;
            }
            else if (quietSince == 0)
            {
                quietSince = now;
            }
            else if (now - quietSince >= QuietIntervalMs)
            {
                return true;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                break;
            }

            await Task.Delay((int)Math.Min(PollIntervalMs, remaining), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<(long Generation, int Inflight)> ReadAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var values = await page.EvaluateAsync<long[]>(ReadScript).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (values is not { Length: >= 2 })
            {
                return (-1, 1);
            }

            var inflight = values[1] < 0 ? 0 : values[1] > int.MaxValue ? int.MaxValue : (int)values[1];
            return (values[0], inflight);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return (-1, 1);
        }
    }
}
