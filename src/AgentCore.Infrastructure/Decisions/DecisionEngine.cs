using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Application.Decisions;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Decisions;

public sealed class DecisionEngine(IDecisionBackend backend, DecisionIntelligenceOptions options) : IDecisionEngine, IDisposable
{
    public DecisionExecutionPolicy Policy => new(options.ShadowMode, options.RequestTimeoutMs, backend.Id, backend.Version);
    private readonly SemaphoreSlim slots = new(options.MaxConcurrentRequests);
    private static readonly Meter Meter = new("AgentCore.Runtime");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("decision.duration", "ms");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("decision.outcomes");

    public async ValueTask<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        DecisionResult Fail(DecisionStatus status) => DecisionResult.Failure(status, backend.Id, backend.Version);
        if (!options.Enabled || options.Backend == DecisionBackendKind.Disabled) return Fail(DecisionStatus.Disabled);
        if (!DecisionValidation.ValidRequest(request, options.MaxStateCharacters)) return Fail(DecisionStatus.InvalidRequest);
        if (request.Scope.Invocation is null && !backend.PreAdmissionSupported) return Fail(DecisionStatus.UnsupportedScope);
        // No waiting queue: callers get an explicit bounded saturation outcome.
        if (!await slots.WaitAsync(0, ct).ConfigureAwait(false)) return Fail(DecisionStatus.Saturated);
        var status = DecisionStatus.Unavailable;
        Task<DecisionResult>? pending = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(options.RequestTimeoutMs);
            var frozen = request with { Candidates = request.Candidates.ToArray(), Signals = new Dictionary<string, string>(request.Signals, StringComparer.Ordinal) };
            pending = backend.EvaluateAsync(frozen, deadline.Token).AsTask();
            // All shipped transports cooperate with cancellation. WaitAsync also bounds a faulty adapter.
            var result = await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            result = DecisionValidation.Validate(frozen, result, backend.Id, backend.Version);
            status = result.Status;
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status = DecisionStatus.Timeout; return Fail(status); }
        catch (AccessDeniedException) { status = DecisionStatus.StaleAuthority; return Fail(status); }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException) { return Fail(status); }
        finally
        {
            if (pending is { IsCompleted: false })
            {
                // Retain the slot until even a non-cooperative backend settles; observe late faults.
                _ = pending.ContinueWith(done => { _ = done.Exception; slots.Release(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else slots.Release();
            Duration.Record(watch.Elapsed.TotalMilliseconds, new("purpose", request.Purpose.ToString()), new("engine", backend.Id));
            Outcomes.Add(1, new("purpose", request.Purpose.ToString()), new("status", status.ToString()));
        }
    }

    public async ValueTask<DecisionEngineStatus> StatusAsync(CancellationToken ct = default)
    {
        if (!options.Enabled || options.Backend == DecisionBackendKind.Disabled)
            return new(DecisionStatus.Disabled, backend.Id, backend.Version, backend.PreAdmissionSupported, options.ShadowMode);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.RequestTimeoutMs);
        DecisionStatus status;
        try { status = await backend.ReadinessAsync(deadline.Token).AsTask().WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status = DecisionStatus.Timeout; }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException) { status = DecisionStatus.Unavailable; }
        return new(status, backend.Id, backend.Version, backend.PreAdmissionSupported, options.ShadowMode);
    }

    public void Dispose() { /* No kernel wait handle is allocated; in-flight completions retain their semaphore safely. */ }
}
