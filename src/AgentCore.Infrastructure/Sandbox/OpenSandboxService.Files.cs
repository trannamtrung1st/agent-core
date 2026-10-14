using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using OpenSandbox.Models;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private static async Task<string> InternalCommandAsync(SdkSandbox sdk, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var output = new BoundedText(1024 * 1024);
        var execution = await sdk.Commands.RunAsync(argv, new RunCommandOptions { WorkingDirectory = "/working", TimeoutSeconds = 30 },
            new ExecutionHandlers { SkipAccumulation = true, OnStdout = m => { output.Append(m.Text); return Task.CompletedTask; } }, ct);
        if (execution.ExitCode != 0 || output.Truncated) throw AgentCoreErrors.Validation("Sandbox filesystem/process validation failed. Reset before another command.");
        return output.Text;
    }

    private static async Task HydrateAsync(SdkSandbox sdk, WorkspaceTransfer transfer, CancellationToken ct)
    {
        // Never mount the authoritative store. Remove only sandbox-local scratch.
        await InternalCommandAsync(sdk, ["python", "-I", "-c", "import os,shutil,stat; p='/working'; assert stat.S_ISDIR(os.lstat(p).st_mode); [shutil.rmtree(p+'/'+n) if stat.S_ISDIR(os.lstat(p+'/'+n).st_mode) else os.unlink(p+'/'+n) for n in os.listdir(p)]"], ct);
        foreach (var e in transfer.Entries.Skip(1).OrderBy(e => e.RelativePath.Count(c => c == '/')))
        {
            var path = "/working/" + e.RelativePath;
            if (e.Directory) await sdk.Files.CreateDirectoriesAsync([new CreateDirectoryEntry { Path = path }], ct);
            else await sdk.Files.WriteFilesAsync([new WriteEntry { Path = path, Data = e.Bytes }], ct);
        }
    }

    // Python -I uses image-owned stdlib, ignoring workload cwd, PYTHONPATH and user site packages.
    // lstat + O_NOFOLLOW + fstat + link-count/hash checks reject every special file before download.
    private const string ManifestScript = """
        import os,stat,json,hashlib
        root='/working'
        assert stat.S_ISDIR(os.lstat(root).st_mode)
        result=[]; total=0; files=0; paths=set()
        def walk(folder,depth):
            global total,files
            assert depth<=8
            for name in sorted(os.listdir(folder)):
                path=folder+'/'+name; rel=os.path.relpath(path,root); key=rel.upper()
                assert key not in paths and len(rel)<=480 and '\\' not in rel
                paths.add(key); assert len(paths)<=8191
                assert not name.lower().startswith('.env') and not name.lower().endswith('.sock')
                info=os.lstat(path); mode=info.st_mode
                if stat.S_ISDIR(mode):
                    result.append(dict(path=rel,directory=True,size=0,sha256='')); walk(path,depth+1)
                else:
                    assert stat.S_ISREG(mode) and info.st_nlink==1 and info.st_size<=50*1024*1024
                    total+=info.st_size; files+=1; assert total<=250*1024*1024 and files<=4096
                    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW); h=hashlib.sha256()
                    with os.fdopen(fd,'rb') as stream:
                        opened=os.fstat(stream.fileno()); assert opened.st_ino==info.st_ino and opened.st_nlink==1
                        while True:
                            chunk=stream.read(65536)
                            if not chunk: break
                            h.update(chunk)
                        end=os.fstat(stream.fileno()); assert (end.st_size,end.st_mtime_ns,end.st_ino)==(info.st_size,info.st_mtime_ns,info.st_ino)
                    result.append(dict(path=rel,directory=False,size=info.st_size,sha256=h.hexdigest()))
        walk(root,0)
        print(json.dumps(result,separators=(',',':')))
        """;
    private sealed record ManifestEntry(string Path, bool Directory, long Size, string Sha256);
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);
    private static async Task<WorkspaceTransfer> CollectAsync(SdkSandbox sdk, SandboxProfile profile, CancellationToken ct)
    {
        var manifest = await InternalCommandAsync(sdk, ["python", "-I", "-c", ManifestScript], ct);
        var entries = JsonSerializer.Deserialize<ManifestEntry[]>(manifest, ManifestJson) ?? throw AgentCoreErrors.Validation("Invalid sandbox tree.");
        if (entries.Sum(e => e.Size) > profile.StorageBytes) throw AgentCoreErrors.WorkspaceQuotaExceeded();
        var result = new List<WorkspaceTransferEntry> { new("", true, "inode/directory", []) };
        foreach (var e in entries)
        {
            if (e.Directory) result.Add(new(e.Path, true, "inode/directory", []));
            else
            {
                var bytes = await sdk.Files.ReadBytesAsync("/working/" + e.Path, cancellationToken: ct);
                if (bytes.LongLength != e.Size || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != e.Sha256)
                    throw AgentCoreErrors.Conflict("Sandbox files changed during collection.");
                result.Add(new(e.Path, false, "application/octet-stream", bytes));
            }
        }
        if (await InternalCommandAsync(sdk, ["python", "-I", "-c", ManifestScript], ct) != manifest)
            throw AgentCoreErrors.Conflict("Sandbox tree changed during collection.");
        return new(result);
    }
    private const string ProcessScript = """
        import os,json
        ignored=set(); p=os.getpid()
        while p>0:
            ignored.add(p)
            try: p=int(open('/proc/'+str(p)+'/stat').read().rsplit(')',1)[1].split()[1])
            except (FileNotFoundError,ProcessLookupError): break
        result=[]
        for name in os.listdir('/proc'):
            if name.isdigit() and int(name) not in ignored:
                try:
                    fields=open('/proc/'+name+'/stat').read().rsplit(')',1)[1].split()
                    if fields[0]!='Z': result.append(name+':'+fields[19])
                except (FileNotFoundError,ProcessLookupError): pass
        print(json.dumps(sorted(result)))
        """;
    private static async Task<string[]> ProcessIdentitiesAsync(SdkSandbox sdk, CancellationToken ct) =>
        JsonSerializer.Deserialize<string[]>(await InternalCommandAsync(sdk, ["python", "-I", "-c", ProcessScript], ct)) ?? [];
    private static async Task ConfirmQuiescentAsync(SdkSandbox sdk, SandboxLease lease, CancellationToken ct)
    {
        var current = await ProcessIdentitiesAsync(sdk, ct);
        if (current.Except(lease.BaselineProcesses ?? [], StringComparer.Ordinal).Any())
            throw AgentCoreErrors.Conflict("Untracked child processes remain. No scratch was committed; reset the environment.");
    }
}
