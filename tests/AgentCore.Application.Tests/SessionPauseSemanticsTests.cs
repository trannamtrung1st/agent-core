using AgentCore.Application.Observability;
using AgentCore.Application.Sessions;
using System.Diagnostics.Metrics;

namespace AgentCore.Application.Tests;

[Collection("telemetry-global")]
public sealed class SessionPauseSemanticsTests
{
    [Theory]
    [InlineData("manual", true)]
    [InlineData("inactivity", true)]
    [InlineData("silentEvaluation", true)]
    [InlineData("initiative", true)]
    [InlineData("persistence", true)]
    [InlineData("disconnected", false)]
    [InlineData("recovered", false)]
    [InlineData(null, true)]
    public void Requires_explicit_resume_only_for_semantic_pauses(string? pauseReason, bool expected) =>
        Assert.Equal(expected, SessionPauseSemantics.RequiresExplicitResume(pauseReason));

    [Theory]
    [InlineData("disconnected", true)]
    [InlineData("recovered", true)]
    [InlineData("inactivity", false)]
    [InlineData("initiative", false)]
    [InlineData("manual", false)]
    public void Transport_resume_is_only_disconnected_or_recovered(string? pauseReason, bool expected) =>
        Assert.Equal(expected, SessionPauseSemantics.IsTransportResumable(pauseReason));

    [Theory]
    [InlineData("user-text", "other")]
    [InlineData("inactivity", "inactivity")]
    [InlineData(null, "other")]
    public void Canonical_reason_stays_in_the_pause_enum(string? pauseReason, string expected) =>
        Assert.Equal(expected, SessionPauseSemantics.CanonicalReason(pauseReason));

    [Fact]
    public void Pause_metrics_use_canonical_reason_labels_only()
    {
        var pauseReasons = new List<string>();
        var autoReasons = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name != RuntimeTelemetry.Name)
            {
                return;
            }

            if (instrument.Name is SessionPauseTelemetry.PauseReasonInstrument or SessionPauseTelemetry.AutoPausedInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                {
                    reason = tag.Value?.ToString();
                }
            }

            Assert.NotNull(reason);
            Assert.DoesNotContain("user-text", reason, StringComparison.Ordinal);
            if (instrument.Name == SessionPauseTelemetry.PauseReasonInstrument)
            {
                pauseReasons.Add(reason!);
            }
            else
            {
                autoReasons.Add(reason!);
            }
        });
        listener.Start();
        SessionPauseTelemetry.Record("inactivity");
        SessionPauseTelemetry.Record("manual");
        SessionPauseTelemetry.Record("please pause because the user said hello");
        Assert.Contains("inactivity", pauseReasons);
        Assert.Contains("manual", pauseReasons);
        Assert.Contains("other", pauseReasons);
        Assert.Contains("inactivity", autoReasons);
        Assert.DoesNotContain("manual", autoReasons);
        Assert.DoesNotContain("other", autoReasons);
    }
}
