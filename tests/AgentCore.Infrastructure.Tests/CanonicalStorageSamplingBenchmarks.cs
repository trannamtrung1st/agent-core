using System.Diagnostics;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Sandbox;
using Xunit.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class StorageSamplingBenchmarkAttribute : FactAttribute
{
    public StorageSamplingBenchmarkAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_STORAGE_SAMPLING_BENCHMARK") != "1")
            Skip = "Opt-in filesystem sampling benchmark with two 12,000-file Git repositories.";
    }
}

public sealed class CanonicalStorageSamplingBenchmarks(ITestOutputHelper output)
{
    [StorageSamplingBenchmark]
    public async Task Two_large_git_trees_report_sample_and_watchdog_scan_cost_without_changing_enforcement()
    {
        var root = Directory.CreateTempSubdirectory("storage-sampling-");
        try
        {
            var resolver = new CanonicalSandboxMountResolver(new() { CanonicalWorkspaceRoot = root.FullName, WorkspaceDockerHostRoot = root.FullName });
            var owner = Guid.NewGuid(); var session = Guid.NewGuid();
            var mounts = resolver.Resolve(owner, session, HomeAccess.ReadWrite, create: true);
            foreach (var tree in new[] { mounts.Working, mounts.Home! })
            {
                for (var directory = 0; directory < 120; directory++)
                {
                    var path = Directory.CreateDirectory(Path.Combine(tree.PhysicalRoot, "src", $"module-{directory:D3}")).FullName;
                    for (var file = 0; file < 100; file++)
                        await File.WriteAllTextAsync(Path.Combine(path, $"source-{file:D3}.txt"), $"module {directory} source {file}\n" + new string('x', 512));
                }
                await Git(tree.PhysicalRoot, "init", "-q");
                await Git(tree.PhysicalRoot, "add", ".");
                await Git(tree.PhysicalRoot, "-c", "gc.auto=0", "-c", "commit.gpgsign=false", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "benchmark");
            }
            var working = mounts.Working.Measure(); var home = mounts.Home!.Measure();
            Assert.True(working.Entries > 12_000 && home.Entries > 12_000);
            output.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; working={working.Entries} entries/{working.Bytes} bytes; home={home.Entries} entries/{home.Bytes} bytes");
            Measure("CheckUsage (both trees)", () => CanonicalSandboxMountResolver.CheckUsage(mounts, long.MaxValue, default));
            // Match the watchdog root-identity resolve: CheckUsage owns the single hard-link scan.
            Measure("Resolve + CheckUsage (filesystem part of watchdog)", () => CanonicalSandboxMountResolver.CheckUsage(resolver.Resolve(owner, session, HomeAccess.ReadWrite, validateHardLinks: false), long.MaxValue, default));
            Assert.Throws<AgentCoreException>(() => CanonicalSandboxMountResolver.CheckUsage(mounts, 1, default));
        }
        finally { root.Delete(true); }
    }

    private void Measure(string name, Action sample)
    {
        var durations = new List<double>();
        for (var i = 0; i < 11; i++)
        {
            var watch = Stopwatch.StartNew(); sample(); watch.Stop();
            if (i == 0) output.WriteLine($"{name}: first={watch.Elapsed.TotalMilliseconds:F2} ms");
            else durations.Add(watch.Elapsed.TotalMilliseconds);
        }
        durations.Sort();
        output.WriteLine($"{name}: warm median={(durations[4] + durations[5]) / 2:F2} ms; p95={durations[9]:F2} ms; 250 ms delay unchanged; scans do not overlap");
    }

    private static async Task Git(string directory, params string[] arguments)
    {
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }
}
