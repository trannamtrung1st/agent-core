using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;

namespace AgentCore.Infrastructure.Tests;

public sealed class OpenSandboxFactAttribute : FactAttribute
{
    public OpenSandboxFactAttribute(bool requiresOrdinaryImage = false, bool requiresAlternateImage = false, bool requiresForeignMountRoot = false)
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_OPENSANDBOX_TESTS") != "1") Skip = "Opt-in real local OpenSandbox/Docker acceptance.";
        else if (requiresForeignMountRoot && Environment.GetEnvironmentVariable("AGENTCORE_OSB_EXPECT_FOREIGN_MOUNT_ROOT") != "1")
            Skip = "Opt-in reproduction on a host whose translated mount roots have foreign ownership.";
        else if (requiresOrdinaryImage && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCORE_OSB_ORDINARY_IMAGE")))
            Skip = "Set AGENTCORE_OSB_ORDINARY_IMAGE to an installed identity-compatible .NET SDK image for the focused compatibility regression.";
        else if (requiresAlternateImage && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCORE_OSB_ALTERNATE_IMAGE")))
            Skip = "Set AGENTCORE_OSB_ALTERNATE_IMAGE to a third installed identity-compatible Linux image.";
    }
}

public sealed class OpenSandboxIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [OpenSandboxFact(requiresForeignMountRoot: true)]
    public async Task Trusted_translated_mount_root_passes_before_initialization_and_disposable_cleanup_is_confirmed()
    {
        await using var f = await CreateFixture("printf initialization-passed");
        var verification = await f.Service.VerifyProfileAsync(f.Profile, CancellationToken.None);
        Assert.True(verification.Passed, f.FailureDiagnostic(verification));
        Assert.Null(verification.FailureCode);
        Assert.Contains("initialization-passed", verification.Stdout);
        Assert.False(Directory.Exists(Path.Combine(f.WorkspaceRoot, ".verification"))
            && Directory.EnumerateDirectories(Path.Combine(f.WorkspaceRoot, ".verification")).Any());
        output.WriteLine(verification.Message);
    }


    [OpenSandboxFact]
    public async Task Verification_executes_initialization_rejects_broken_scripts_and_does_not_change_profile_configuration()
    {
        await using var f = await CreateFixture("printf harmless > initialization.txt; exit 7");
        var broken = await f.Service.VerifyProfileAsync(f.Profile, CancellationToken.None);
        Assert.False(broken.Passed); Assert.Equal("initialization", broken.FailureCode); Assert.Equal(7, broken.ExitCode);
        await f.Store.SaveVerificationAsync(broken);
        Assert.Equal(f.Profile, await f.Store.GetProfileAsync(f.Profile.Id, f.Profile.Revision));
        var passed = await f.Service.VerifyProfileAsync(f.Profile with { InitializationScript = "printf initialized > initialization.txt" }, CancellationToken.None);
        Assert.True(passed.Passed, f.FailureDiagnostic(passed));
        var invalid = await f.Service.VerifyProfileAsync(f.Profile with { InitializationScript = "touch .env" }, CancellationToken.None);
        Assert.True(invalid.Passed, f.FailureDiagnostic(invalid));
        var privateOutput = await f.Service.VerifyProfileAsync(f.Profile with { InitializationScript = "printf secret-sentinel >&2; exit 9" }, CancellationToken.None);
        Assert.False(privateOutput.Passed); Assert.DoesNotContain("secret-sentinel", privateOutput.Message);
        Assert.Empty(await f.Store.ListLeasesAsync());
    }

    private static Task<Fixture> CreateFixture(string? initializationScript = null) => Fixture.Create(initializationScript);
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "agent-core-osb-verification-" + Guid.NewGuid().ToString("N"));
        public string WorkspaceRoot { get; private set; } = "";
        public OpenSandboxService Service { get; private set; } = null!;
        public SandboxProfile Profile { get; private set; } = null!;
        public ISandboxStore Store { get; } = new InMemorySandboxStore();
        private OpenSandboxVerificationLogs Logs { get; } = new();
        public string FailureDiagnostic(SandboxProfileVerification verification) => verification.Message + Environment.NewLine + Logs.Diagnostic;
        public static async Task<Fixture> Create(string? script)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            f.WorkspaceRoot = Path.Combine(Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_MOUNT_ROOT") ?? Path.Combine(f.Root, "mounts"), Guid.NewGuid().ToString("N"));
            var options = new OpenSandboxOptions { Enabled = true, AllowLocalImageIds = true,
                Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_URL") ?? "http://127.0.0.1:18080",
                ApiKey = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_KEY"),
                CanonicalWorkspaceRoot = f.WorkspaceRoot, WorkspaceDockerHostRoot = f.WorkspaceRoot };
            f.Profile = new("general-workbench", 1, "Workbench", "Native verification fixture", true,
                Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_IMAGE")!, InitializationScript: script);
            await f.Store.SaveProfileAsync(f.Profile, 0);
            f.Service = new(options, f.Store, null!, null!, TimeProvider.System, logger: f.Logs); return f;
        }
        public ValueTask DisposeAsync()
        {
            // Verification owns disposable probe files. Pending cleanup retains
            // those files as evidence; no Run, Session or command facade is seeded.
            var probes = Path.Combine(WorkspaceRoot, ".verification");
            if (!Directory.Exists(probes) || !Directory.EnumerateDirectories(probes).Any())
            {
                if (Directory.Exists(WorkspaceRoot)) Directory.Delete(WorkspaceRoot, true);
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            return ValueTask.CompletedTask;
        }
    }
}
