using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenSandbox;
using OpenSandbox.Config;
using OpenSandbox.Models;

// An opt-in upstream compatibility probe, excluded from the application solution.
// This does not establish Agent Core authorization, Workspace or security acceptance.
if (args is ["--help"])
{
    Console.WriteLine("Set AGENTCORE_OSB_PROOF_DISPOSABLE=1, AGENTCORE_OSB_PROOF_URL, AGENTCORE_OSB_PROOF_KEY and AGENTCORE_OSB_PROOF_IMAGE, then run without arguments. See README.md.");
    return;
}

if (args.Length != 0 || Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_DISPOSABLE") != "1")
{
    Console.Error.WriteLine("The probe requires an explicitly disposable, isolated test server. See README.md.");
    Environment.ExitCode = 2;
    return;
}

Uri url;
string image;
ConnectionConfig connection;
try
{
    url = new Uri(Required("AGENTCORE_OSB_PROOF_URL"), UriKind.Absolute);
    if (url.UserInfo.Length != 0 || url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0
        || url.Scheme is not ("http" or "https")
        || url.Scheme == "http" && !url.IsLoopback)
        throw new InvalidOperationException("Use loopback HTTP or isolated-runner HTTPS without user info, path, query or fragment.");

    image = Required("AGENTCORE_OSB_PROOF_IMAGE");
    if (!Regex.IsMatch(image, @"^[a-zA-Z0-9./:_-]+@sha256:[a-f0-9]{64}$"))
        throw new InvalidOperationException("The disposable test image must be pinned by SHA-256 digest.");

    connection = new ConnectionConfig(new ConnectionConfigOptions
    {
        Domain = url.Authority,
        Protocol = url.Scheme == "https" ? ConnectionProtocol.Https : ConnectionProtocol.Http,
        ApiKey = Required("AGENTCORE_OSB_PROOF_KEY"),
        UseServerProxy = true,
        RequestTimeoutSeconds = 45,
        DisableMetrics = true
    });
}
catch (Exception)
{
    Console.Error.WriteLine("Invalid proof configuration: set a valid origin, private key and digest-pinned image as described in README.md. Values suppressed.");
    Environment.ExitCode = 2;
    return;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
var ct = deadline.Token;
var owned = new List<Sandbox>();
var failures = new List<string>();
try
{
    // Auth must fail before any sandbox is created.
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    using var unauthenticated = await http.GetAsync(new Uri(url, "/sandboxes"), ct);
    var authenticated = unauthenticated.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    Check(authenticated, "authentication-required");
    if (!authenticated) throw new InvalidOperationException("Unauthenticated lifecycle access must fail closed.");

    var first = await CreateAsync("one");
    owned.Add(first);
    var identity = await RunAsync(first, "id -u");
    var nonRoot = identity.ExitCode == 0 && int.TryParse(Stdout(identity).Trim(), out var uid) && uid > 0;
    Check(nonRoot, "non-root-command");
    if (!nonRoot) throw new InvalidOperationException("The proof requires confirmed non-root execution.");
    Check(identity.Complete is { ExecutionTimeMs: >= 0 }, "command-duration");

    var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    await first.Files.WriteFilesAsync([new WriteEntry { Path = "/working/exact.bin", Data = bytes }], ct);
    var transferred = await first.Files.ReadBytesAsync("/working/exact.bin", cancellationToken: ct);
    Check(bytes.SequenceEqual(transferred), "binary-transfer-exact");
    var execution = await RunAsync(first, "printf 'stdout-marker'; printf 'stderr-marker' >&2; exit 7");
    Check(execution.ExitCode == 7 && Stdout(execution).Contains("stdout-marker", StringComparison.Ordinal)
          && Stderr(execution).Contains("stderr-marker", StringComparison.Ordinal), "nonzero-exit-and-separated-streams");

    await using (var connected = await Sandbox.ConnectAsync(new SandboxConnectOptions
                 { SandboxId = first.Id, ConnectionConfig = connection }, ct))
    {
        var retained = await connected.Files.ReadBytesAsync("/working/exact.bin", cancellationToken: ct);
        Check(bytes.SequenceEqual(retained), "connect-retains-files");
        var argv = await connected.Commands.RunAsync(["python", "-c", "print('argv-marker')"], CommandOptions(), cancellationToken: ct);
        Check(argv.ExitCode == 0 && Stdout(argv).Contains("argv-marker", StringComparison.Ordinal), "native-argv");
    }

    var second = await CreateAsync("two");
    owned.Add(second);
    var isolated = await RunAsync(second, "test ! -e /working/exact.bin");
    Check(isolated.ExitCode == 0, "separate-sandbox-files");

    var background = await first.Commands.RunAsync("sleep 120", new RunCommandOptions
        { WorkingDirectory = "/working", Background = true, TimeoutSeconds = 120 }, cancellationToken: ct);
    var processId = background.Id ?? throw new InvalidOperationException("Background execution did not return an ID.");
    var active = await first.Commands.GetCommandStatusAsync(processId, ct);
    Check(active.Running == true, "background-status");
    await first.Commands.InterruptAsync(processId, ct);
    var stopped = false;
    for (var attempt = 0; attempt < 20 && !stopped; attempt++)
    {
        stopped = (await first.Commands.GetCommandStatusAsync(processId, ct)).Running == false;
        if (!stopped) await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
    }
    Check(stopped, "background-stop-confirmed");

    // Probe only the sandbox's OWN loopback execd. No operator host or external network target.
    // The proposal requires command code to lack control-plane endpoint access. Reachability
    // is an explicit failed gate even if ordinary SDK execution succeeds.
    var localProbe = await RunAsync(first, "python -c \"import socket; s=socket.socket(); s.settimeout(2); r=s.connect_ex(('127.0.0.1',44772)); s.close(); raise SystemExit(1 if r==0 else 0)\"");
    Check(localProbe.ExitCode == 0, "workload-cannot-connect-to-execd-loopback");
}
catch (Exception)
{
    // SDK exception bodies can contain credentials, URLs and private paths. Do not echo them.
    Console.Error.WriteLine("PROBE ERROR: compatibility execution failed; raw SDK details suppressed. Cleanup will be attempted.");
    failures.Add("compatibility-exception");
}
finally
{
    foreach (var sandbox in owned.AsEnumerable().Reverse())
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await sandbox.KillAsync(cleanup.Token); Emit("kill-owned-sandbox", true); }
        catch (Exception) { failures.Add("cleanup-pending"); Emit("kill-owned-sandbox", false); }
        try { await sandbox.DisposeAsync(); }
        catch (Exception) { failures.Add("dispose-failed"); Emit("dispose-owned-client", false); }
    }
}

Console.WriteLine(JsonSerializer.Serialize(new { compatibilityPassed = failures.Count == 0, failures, securityAccepted = false }));
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

Task<Sandbox> CreateAsync(string label) => Sandbox.CreateAsync(new SandboxCreateOptions
{
    ConnectionConfig = connection,
    Image = image,
    TimeoutSeconds = 240,
    ReadyTimeoutSeconds = 45,
    Resource = new Dictionary<string, string> { ["cpu"] = "1", ["memory"] = "512Mi" },
    Metadata = new Dictionary<string, string> { ["agent-core-proof"] = label },
    NetworkPolicy = new NetworkPolicy { DefaultAction = NetworkRuleAction.Deny, Egress = [] }
}, ct);

async Task<Execution> RunAsync(Sandbox sandbox, string command)
{
    long stdoutBytes = 0;
    long stderrBytes = 0;
    var result = await sandbox.Commands.RunAsync(command, CommandOptions(), new ExecutionHandlers
    {
        OnStdout = message => { stdoutBytes += System.Text.Encoding.UTF8.GetByteCount(message.Text); EnforceOutputLimit(stdoutBytes); return Task.CompletedTask; },
        OnStderr = message => { stderrBytes += System.Text.Encoding.UTF8.GetByteCount(message.Text); EnforceOutputLimit(stderrBytes); return Task.CompletedTask; }
    }, ct);
    Check(stdoutBytes <= 65536 && stderrBytes <= 65536, "probe-output-size");
    return result;
}

void Check(bool passed, string scenario)
{
    Emit(scenario, passed);
    if (!passed) failures.Add(scenario);
}

static void Emit(string scenario, bool passed) => Console.WriteLine(JsonSerializer.Serialize(new { scenario, passed }));
static void EnforceOutputLimit(long bytes)
{
    if (bytes > 65536) throw new InvalidOperationException("Proof output limit exceeded.");
}
static RunCommandOptions CommandOptions() => new() { WorkingDirectory = "/working", TimeoutSeconds = 15 };
static string Stdout(Execution result) => string.Concat(result.Logs.Stdout.Select(x => x.Text));
static string Stderr(Execution result) => string.Concat(result.Logs.Stderr.Select(x => x.Text));
static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Required environment variable: {name}.");
