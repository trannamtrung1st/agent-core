using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;

namespace AgentCore.Application.Tests;

public sealed class WorkspaceTreePlannerTests
{
    private static IReadOnlyList<WorkspacePlannedOperation> Plan(WorkspaceTreeEntry[] entries,
        params WorkspaceStructuralOperation[] operations) => WorkspaceTreePlanner.Plan(entries, operations, "/home", 100, 100);

    [Fact]
    public void Nested_mkdir_is_idempotent_and_empty_directories_are_real_entries()
    {
        var result = Plan([], new("mkdir", Path: "/home/projects/a"), new("mkdir", Path: "/home/projects/a"),
            new("copy", Source: "/home/projects", Destination: "/home/backup"));
        Assert.Equal(new[] { "/home/projects", "/home/projects/a" }, result[0].ParentsToCreate);
        Assert.Equal("alreadyExists", result[1].Outcome.Status);
        Assert.Equal(2, result[2].SourceTree.Count); Assert.All(result[2].SourceTree, e => Assert.True(e.Directory));
    }

    [Fact]
    public void Ordered_restructure_preflights_mkdir_moves_copy_and_explicit_recursive_delete()
    {
        WorkspaceTreeEntry[] initial = [new("/home/report.bin", false, 30), new("/home/notes.md", false, 20)];
        var result = Plan(initial, new("mkdir", Path: "/home/archive"),
            new("move", Source: "/home/report.bin", Destination: "/home/archive/report.bin"),
            new("move", Source: "/home/notes.md", Destination: "/home/archive/notes.md"),
            new("copy", Source: "/home/archive", Destination: "/home/project/template"),
            new("delete", Path: "/home/archive", Recursive: true));
        Assert.Equal(5, result.Count); Assert.Equal(2, result[3].Outcome.FilesAffected);
        Assert.Equal(50, result[3].Outcome.BytesAffected); Assert.Equal(2, result[4].Outcome.FilesAffected);
        Assert.Equal("/home/report.bin", initial[0].Path); // Preflight never mutates the provided snapshot.
    }

    [Fact]
    public void Quota_accounts_for_earlier_deletions_and_directory_copies_before_execution()
    {
        WorkspaceTreeEntry[] initial = [new("/home/a", true, 0), new("/home/a/data", false, 60), new("/home/old", false, 30)];
        Assert.Throws<AgentCoreException>(() => Plan(initial, new WorkspaceStructuralOperation("copy", Source: "/home/a", Destination: "/home/b")));
        var result = Plan(initial, new("delete", Path: "/home/old"), new("move", Source: "/home/a", Destination: "/home/b"));
        Assert.Equal(2, result.Count); Assert.Equal(60, result[1].Outcome.BytesAffected);
    }

    [Theory]
    [InlineData("/home")]
    [InlineData("/home/../other")]
    [InlineData("/home/.env")]
    [InlineData("/home/runtime/data")]
    [InlineData("/home/*.txt")]
    [InlineData("/home/**/data")]
    [InlineData("/agent/definition.json")]
    [InlineData("/attachments/input")]
    [InlineData("/workspace/working/other")]
    [InlineData("/private/tmp/host")]
    public void Protected_traversal_glob_and_cross_scope_destinations_are_rejected(string path) =>
        Assert.Throws<AgentCoreException>(() => Plan([], new WorkspaceStructuralOperation("mkdir", Path: path)));

    [Fact]
    public void Portable_case_collisions_parent_files_and_existing_destinations_are_rejected()
    {
        WorkspaceTreeEntry[] initial = [new("/home/Reports/a", false, 10), new("/home/b", false, 10)];
        foreach (var op in new[] { new WorkspaceStructuralOperation("mkdir", Path: "/home/reports/new"),
            new("mkdir", Path: "/home/b/nested"), new("copy", Source: "/home/b", Destination: "/home/Reports/a"),
            new("move", Source: "/home/b", Destination: "/home/B") })
            Assert.Throws<AgentCoreException>(() => Plan(initial, op));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("move")]
    public void Directory_self_and_descendant_cycles_are_denied(string operation)
    {
        WorkspaceTreeEntry[] initial = [new("/home/a", true, 0), new("/home/a/data", false, 1)];
        foreach (var dest in new[] { "/home/a", "/home/a/inside" })
            Assert.Throws<AgentCoreException>(() => Plan(initial, new WorkspaceStructuralOperation(operation, Source: "/home/a", Destination: dest)));
    }

    [Fact]
    public void Delete_needs_explicit_recursive_intent_and_missing_sources_are_clear()
    {
        WorkspaceTreeEntry[] initial = [new("/home/a", true, 0), new("/home/a/empty", true, 0)];
        Assert.Throws<AgentCoreException>(() => Plan(initial, new WorkspaceStructuralOperation("delete", Path: "/home/a")));
        Assert.Equal(1, Plan(initial, new WorkspaceStructuralOperation("delete", Path: "/home/a/empty"))[0].Outcome.DirectoriesAffected);
        Assert.Equal("NotFound", Assert.Throws<AgentCoreException>(() => Plan([], new WorkspaceStructuralOperation("delete", Path: "/home/missing"))).Code);
    }

    [Fact]
    public void Entire_batch_rejects_bad_later_operation_limits_and_conflicts()
    {
        Assert.Throws<AgentCoreException>(() => Plan([], new("mkdir", Path: "/home/a"), new("write", Path: "/home/b")));
        Assert.Throws<AgentCoreException>(() => Plan([], []));
        Assert.Throws<AgentCoreException>(() => Plan([], Enumerable.Repeat(new WorkspaceStructuralOperation("mkdir", Path: "/home/a"), 17).ToArray()));
        Assert.Throws<AgentCoreException>(() => Plan([new("/home/file", false, 10)],
            new("mkdir", Path: "/home/a"), new("copy", Source: "/home/file", Destination: "/home/a/file"),
            new("copy", Source: "/home/file", Destination: "/home/a/file")));
    }

    [Fact]
    public void Scratch_supports_only_working_and_protects_structural_roots()
    {
        var result = WorkspaceTreePlanner.Plan([new("/workspace/working/a", false, 1)],
            [new("move", Source: "/workspace/working/a", Destination: "/workspace/working/nested/a")], "/workspace", 100, 100);
        Assert.Equal("/workspace/working/nested", Assert.Single(result).ParentsToCreate.Single());
        foreach (var root in new[] { "/workspace", "/workspace/working", "/workspace/artifacts", "/workspace/state" })
            Assert.Throws<AgentCoreException>(() => WorkspaceTreePlanner.Plan([], [new("delete", Path: root, Recursive: true)], "/workspace", 100, 100));
    }
}
