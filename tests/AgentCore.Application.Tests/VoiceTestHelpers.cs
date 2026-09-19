using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;

namespace AgentCore.Application.Tests;

internal static class VoiceTestHelpers
{
    public static string WithExplicitSpeech(string speech, string? display = null) =>
        $"[[speech:{speech}]]\n{display ?? speech}";

    public static async Task WaitForActiveResponseAsync(SessionRuntime runtime, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await runtime.WaitUntilMailboxDrainedAsync();
            if (runtime.ActiveResponseId is not null)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for an active response.");
    }
}
