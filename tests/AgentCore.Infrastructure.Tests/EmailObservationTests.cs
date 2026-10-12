using System.Diagnostics;
using AgentCore.Application.Observability;
using AgentCore.Application.Testing;
using AgentCore.Infrastructure.Email;

namespace AgentCore.Infrastructure.Tests;

public sealed class EmailObservationTests
{
    [Fact]
    public void A_known_failure_is_not_hidden_by_subsequent_cancellation_or_scope_success()
    {
        using var listener = DiagnosticActivity.Listen();
        using var metrics = RuntimeMetricProbe.Start();
        using var cancellation = new CancellationTokenSource();
        var observation = new EmailOperationObservation("send", false, TimeProvider.System, Guid.NewGuid(), cancellation.Token);
        var activity = Activity.Current!;
        HandledFailureLog.Warning(new DiagnosticLogCapture<EmailObservationTests>(), new IOException("private response"),
            "email.send", "email_send_rejected");
        cancellation.Cancel(); observation.Complete("failed"); observation.Dispose();
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Contains(metrics.Tags, t => t.Instrument == "email.operations" && t.Key == "outcome" && Equals(t.Value, "failed"));
        // An outer recovery observation must also retain a failure already marked by the shared logger.
        var recovery = new EmailOperationObservation("oauth.check", false, TimeProvider.System, Guid.NewGuid());
        var recoveredActivity = Activity.Current!;
        HandledFailureLog.Warning(null, new IOException("private response"), "email.oauth.check", "email_oauth_temporary_failure");
        recovery.Complete(); recovery.Dispose();
        Assert.Equal(ActivityStatusCode.Error, recoveredActivity.Status);
    }
}
