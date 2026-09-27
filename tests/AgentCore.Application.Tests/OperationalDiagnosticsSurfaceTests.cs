using System.Diagnostics.Metrics;
using AgentCore.Application.Observability;

namespace AgentCore.Application.Tests;

[Collection("telemetry-global")]
public sealed class OperationalDiagnosticsSurfaceTests
{
    [Fact]
    public void Diagnostics_keep_reason_codes_and_omit_payloads()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        var tags = new List<(string Instrument, string Key, string Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == RuntimeTelemetry.Name
                && instrument.Name is OperationalDiagnostics.DetachInstrument
                    or OperationalDiagnostics.AttachInstrument
                    or OperationalDiagnostics.ApprovalInstrument
                    or OperationalDiagnostics.ApprovalWaitInstrument
                    or OperationalDiagnostics.CompactionInstrument
                    or OperationalDiagnostics.MemoryMutationInstrument
                    or OperationalDiagnostics.AdminInstrument
                    or OperationalDiagnostics.AdminTimingInstrument
                    or OperationalDiagnostics.ToolDenialInstrument
                    or OperationalDiagnostics.ResourceLimitInstrument
                    or OperationalDiagnostics.ModelSelectionInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tagList, _) =>
        {
            foreach (var tag in tagList)
            {
                tags.Add((instrument.Name, tag.Key, tag.Value?.ToString() ?? ""));
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, tagList, _) =>
        {
            foreach (var tag in tagList)
            {
                tags.Add((instrument.Name, tag.Key, tag.Value?.ToString() ?? ""));
            }
        });
        listener.Start();

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        OperationalDiagnostics.RecordDetach("transportOnly", "acceptedWork");
        OperationalDiagnostics.RecordAttach("resumed", "attached");
        OperationalDiagnostics.RecordAttach("refused", "deadline");
        OperationalDiagnostics.RecordApproval("waiting", "waiting", null);
        OperationalDiagnostics.RecordApproval("approved", "approved", 12);
        OperationalDiagnostics.RecordCompactionAccepted();
        OperationalDiagnostics.RecordMemoryMutation("session", "written");
        OperationalDiagnostics.RecordAdmin(
            "resolve",
            "completed",
            "completed",
            started,
            "examiner",
            1,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "resolved");
        OperationalDiagnostics.RecordToolDenial("sandbox.run");
        OperationalDiagnostics.RecordResourceLimit("workspaceStore");
        OperationalDiagnostics.RecordResourceLimit("attachmentItem");
        OperationalDiagnostics.RecordModelSelection("scripted-alpha", "primary-llm");
        OperationalDiagnostics.RecordModelSelection("sk-secret-key", "primary-llm");
        listener.Dispose();

        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.DetachInstrument && tag.Key == "reason" && tag.Value == "acceptedWork");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.AttachInstrument && tag.Key == "phase" && tag.Value == "resumed");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.AttachInstrument && tag.Key == "reason" && tag.Value == "deadline");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.ApprovalInstrument && tag.Key == "state" && tag.Value == "approved");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.CompactionInstrument && tag.Key == "outcome" && tag.Value == "accepted");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.MemoryMutationInstrument && tag.Key == "scope" && tag.Value == "session");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.MemoryMutationInstrument && tag.Key == "result" && tag.Value == "written");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.AdminInstrument && tag.Key == "operation" && tag.Value == "resolve");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.ToolDenialInstrument && tag.Key == "reason" && tag.Value == "forbidden");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.ResourceLimitInstrument && tag.Key == "reason" && tag.Value == "workspaceStore");
        Assert.Contains(tags, tag => tag.Instrument == OperationalDiagnostics.ModelSelectionInstrument && tag.Key == "result" && tag.Value == "selected");
        Assert.DoesNotContain(tags, tag => tag.Key is "definitionId" or "instanceId" or "catalogKey");
        Assert.All(tags, tag => Assert.DoesNotContain("sk-secret", tag.Value, StringComparison.OrdinalIgnoreCase));

        var events = RuntimeTelemetry.SnapshotTimeline();
        Assert.Contains(events, item => item.Stage == OperationalDiagnostics.CompactionInstrument && item.Detail == "accepted");
        Assert.Contains(events, item => item.Stage == OperationalDiagnostics.MemoryMutationInstrument && item.Detail == "session:written");
        Assert.Contains(events, item =>
            item.Stage == OperationalDiagnostics.ModelSelectionInstrument
            && item.Detail == "catalogKey=scripted-alpha;providerAlias=primary-llm");
        Assert.Contains(events, item =>
            item.Stage == OperationalDiagnostics.AdminInstrument
            && item.Detail is not null
            && item.Detail.Contains("definitionId=examiner", StringComparison.Ordinal)
            && item.Detail.Contains("state=resolved", StringComparison.Ordinal));
        Assert.Contains(events, item => item.Stage == OperationalDiagnostics.ToolDenialInstrument && item.Detail == "sandbox.run");
        Assert.All(events, item => Assert.DoesNotContain("sk-secret", item.Detail ?? "", StringComparison.OrdinalIgnoreCase));
    }
}
