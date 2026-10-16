using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Triggers;

public enum TriggerCommandAuthorizationDecision
{
    Deny, Allow, Ambiguous, ClassifierUnavailable, ClassifierInvalidOutput
}

public enum TriggerAuthorizationFailureReason
{
    MalformedJson, InvalidDecision, TruncatedOutput, IncompleteOutput, InvalidResponseChannel, OversizedOutput, ProviderInvalidResponse, ProviderFailure
}

public sealed record TriggerCommandAuthorizationVerdict(TriggerCommandAuthorizationDecision Decision, TriggerAuthorizationFailureReason? FailureReason = null);

public sealed record AutomationAuthorizationRequest(string CurrentUserText, TriggerCommandAction Action,
    string ToolName, JsonElement ProposedAction, string ActionHash,
    ScheduleConversationContext? ScheduleContext = null, ScheduleDraftContext? ScheduleDraft = null, string? ConversationEvidenceJson = null);

public interface ITriggerCommandAuthorizer
{
    ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeAsync(AutomationAuthorizationRequest proposed, CancellationToken cancellationToken = default);
}

/// <summary>No verifier registration can ever silently authorize a mutation.</summary>
public sealed class UnavailableTriggerCommandAuthorizer : ITriggerCommandAuthorizer
{
    public ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeAsync(AutomationAuthorizationRequest proposed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TriggerCommandAuthorizationVerdict(TriggerCommandAuthorizationDecision.ClassifierUnavailable, TriggerAuthorizationFailureReason.ProviderFailure));
    }
}

public sealed class ModelTriggerCommandAuthorizer(
    ILanguageModel languageModel,
    string? reasoningEffort = null,
    ILogger<ModelTriggerCommandAuthorizer>? logger = null,
    ModelDescriptor? descriptor = null,
    TimeProvider? timeProvider = null) : ITriggerCommandAuthorizer
{
    private const int InitialOutputTokens = 512;
    private const int RecoveryOutputTokens = 1024;
    private static readonly TimeSpan ClassificationTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeAsync(
        AutomationAuthorizationRequest proposed,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(proposed.CurrentUserText)) return new(TriggerCommandAuthorizationDecision.Deny);
        var requestedAction = proposed.Action;
        var request = ContextAssembler.Create(Guid.Empty, new EffectiveContextSnapshot([
            ContextSection.Core(CoreProcedure.OperatingContract), ContextSection.Core(CoreProcedure.TriggerAuthorization),
            ContextSection.Runtime(ContextSectionId.RunTask, ContextSourceKind.AgentRun,
                $"Proposed operation: {requestedAction}; exact action fingerprint: {proposed.ActionHash}.", semantics: ContextSemantics.TrustedFact),
            ContextSection.Evidence(ContextSectionId.History, ContextSourceKind.Session, string.Join('\n',
                (proposed.ScheduleContext?.ToPromptLines() ?? []).Concat(proposed.ScheduleDraft?.ToPromptLines() ?? [])
                    .Append(proposed.ConversationEvidenceJson ?? "[]"))),
            ContextSection.Evidence(ContextSectionId.ToolResults, ContextSourceKind.Tool, proposed.ProposedAction.GetRawText())
        ], purpose: ContextPurpose.Platform), [new(ModelRole.User, proposed.CurrentUserText)], maxOutputTokens: InitialOutputTokens, temperature: 0, reasoningEffort: reasoningEffort);

        // Both attempts share one deadline, further bounded by the calling tool/turn.
        using var timeout = new CancellationTokenSource(ClassificationTimeout, time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = time.GetTimestamp();
            using var activity = RuntimeTelemetry.Activity.StartActivity("automation.authorization.classifier");
            ClassifierRead read;
            TriggerCommandAuthorizationVerdict verdict;
            var valid = false;
            try
            {
                read = await ReadTextAsync(languageModel, request, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (read.Failure is { } failure) verdict = failure;
                else
                {
                    valid = TryParseDecision(read.Text, out var decision, out var reason);
                    verdict = valid ? new(decision) : new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, reason);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                read = new(string.Empty, null, null);
                verdict = new(TriggerCommandAuthorizationDecision.ClassifierUnavailable, TriggerAuthorizationFailureReason.ProviderFailure);
            }
            catch (OperationCanceledException)
            {
                RecordAttempt("Cancelled", null, false);
                throw;
            }
            catch (Exception)
            {
                read = new(string.Empty, null, null, ProviderErrorCode.Unknown);
                verdict = new(TriggerCommandAuthorizationDecision.ClassifierUnavailable, TriggerAuthorizationFailureReason.ProviderFailure);
            }
            var terminal = deadline.IsCancellationRequested ? "DeadlineExceeded"
                : read.ProviderCode?.ToString() ?? read.Terminal?.Reason.ToString() ?? verdict.FailureReason?.ToString() ?? verdict.Decision.ToString();
            RecordAttempt(terminal, read.Terminal?.OutputTokens, valid);
            if (attempt == 2 || verdict.FailureReason != TriggerAuthorizationFailureReason.TruncatedOutput || deadline.IsCancellationRequested)
                return verdict;
            // Never carry over partial text, tools, or a proposed decision.
            request = request with { MaxOutputTokens = RecoveryOutputTokens };

            void RecordAttempt(string stop, int? outputTokens, bool validDecision)
            {
                var elapsed = time.GetElapsedTime(started).TotalMilliseconds;
                activity?.SetTag("action", requestedAction.ToString());
                activity?.SetTag("catalog", SafeMetadata(descriptor?.Key));
                activity?.SetTag("provider", SafeMetadata(descriptor?.ProviderAlias));
                activity?.SetTag("model", SafeMetadata(descriptor?.ModelId));
                activity?.SetTag("transport", descriptor?.Transport.ToString() ?? "Unknown");
                activity?.SetTag("reasoning_effort", SafeMetadata(reasoningEffort));
                activity?.SetTag("attempt", attempt);
                activity?.SetTag("max_attempts", 2);
                activity?.SetTag("max_output_tokens", request.MaxOutputTokens);
                activity?.SetTag("elapsed_ms", elapsed);
                activity?.SetTag("terminal", stop);
                activity?.SetTag("output_tokens", outputTokens);
                activity?.SetTag("valid_decision", validDecision);
                if (!validDecision) activity?.SetStatus(ActivityStatusCode.Error, stop);
                logger?.LogInformation("Automation authorization classifier action={Action} catalog={CatalogKey} provider={ProviderAlias} model={ModelId} transport={Transport} effort={ReasoningEffort} maxOutputTokens={MaxOutputTokens} attempt={Attempt} maxAttempts={MaxAttempts} elapsedMs={ElapsedMs} terminal={Terminal} outputTokens={OutputTokens} validDecision={ValidDecision} traceId={TraceId}",
                    requestedAction, SafeMetadata(descriptor?.Key), SafeMetadata(descriptor?.ProviderAlias), SafeMetadata(descriptor?.ModelId),
                    descriptor?.Transport.ToString() ?? "Unknown", SafeMetadata(reasoningEffort), request.MaxOutputTokens, attempt, 2,
                    elapsed, stop, outputTokens, validDecision, Activity.Current?.TraceId.ToString());
            }
        }
        throw new UnreachableException();
    }

    private static string SafeMetadata(string? value) => value is null ? "Unknown" : new(value.Take(128).Where(c => !char.IsControl(c)).ToArray());

    private static bool TryParseDecision(string raw, out TriggerCommandAuthorizationDecision decision, out TriggerAuthorizationFailureReason reason)
    {
        decision = TriggerCommandAuthorizationDecision.Ambiguous;
        reason = TriggerAuthorizationFailureReason.InvalidDecision;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count() != 1
                || !document.RootElement.TryGetProperty("decision", out var element)
                || element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var token = element.GetString()?.Trim().ToLowerInvariant();
            decision = token switch
            {
                "allow" => TriggerCommandAuthorizationDecision.Allow,
                "deny" => TriggerCommandAuthorizationDecision.Deny,
                "ambiguous" => TriggerCommandAuthorizationDecision.Ambiguous,
                _ => TriggerCommandAuthorizationDecision.Ambiguous
            };
            return token is "allow" or "deny" or "ambiguous";
        }
        catch (JsonException)
        {
            reason = TriggerAuthorizationFailureReason.MalformedJson;
            return false;
        }
    }

    private sealed record ClassifierRead(string Text, TriggerCommandAuthorizationVerdict? Failure, ModelCompleted? Terminal, ProviderErrorCode? ProviderCode = null);

    private static async Task<ClassifierRead> ReadTextAsync(
        ILanguageModel model,
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        ModelCompleted? completed = null;
        await foreach (var item in model.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is ModelFailed failed)
                return new(string.Empty, failed.Failure.Code == ProviderErrorCode.InvalidResponse
                    ? new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.ProviderInvalidResponse)
                    : new(TriggerCommandAuthorizationDecision.ClassifierUnavailable, TriggerAuthorizationFailureReason.ProviderFailure), completed, failed.Failure.Code);
            if (item is ModelToolCallEvent)
                return new(string.Empty, new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.InvalidResponseChannel), completed);
            if (completed is not null)
                return new(string.Empty, new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.InvalidResponseChannel), completed);
            if (item is ModelCompleted terminal)
            {
                if (terminal.Reason != ModelStopReason.Completed)
                    return new(string.Empty, new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput,
                        terminal.Reason == ModelStopReason.LengthLimit ? TriggerAuthorizationFailureReason.TruncatedOutput : TriggerAuthorizationFailureReason.InvalidResponseChannel), terminal);
                completed = terminal;
            }
            if (item is ModelTextDelta delta)
            {
                if (builder.Length + delta.Text.Length > 4096)
                    return new(string.Empty, new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.OversizedOutput), completed);
                builder.Append(delta.Text);
            }
        }

        return new(builder.ToString(), completed is not null ? null : new(TriggerCommandAuthorizationDecision.ClassifierInvalidOutput, TriggerAuthorizationFailureReason.IncompleteOutput), completed);
    }
}
