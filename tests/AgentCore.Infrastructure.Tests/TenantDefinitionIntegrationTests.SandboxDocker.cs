using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [OpenSandboxFact(Timeout = 600000)]
    public async Task Tenant_Docker_Sandbox_verifies_owned_source_executes_exact_review_once_and_retires_only_its_generation()
    {
        foreach (var sqlite in new[] { false, true })
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_MOUNT_ROOT")!, Guid.NewGuid().ToString("N"));
            await using var f = await Fixture.Create(sqlite);
            var options = new OpenSandboxOptions { Enabled = true, AllowLocalImageIds = true,
                Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_URL")!, ApiKey = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_KEY"),
                CanonicalWorkspaceRoot = root, WorkspaceDockerHostRoot = root };
            var p = SetupProfiles(f, options);
            var verifier = new OpenSandboxService(options, f.Sandbox, null!, f.SessionStore, TimeProvider.System,
                imageAuthority: p.Images, tenantVerifications: (ITenantSandboxVerificationStore)f.Iam,
                verificationAuthority: new(f.Iam, (ITenantSandboxProfileStore)f.Iam, f.Identity, f.Authorizer, SetupResolver(f)));
            RuntimeSandboxSetup? s = null; OpenSandboxService? sdk = null; var retired = false;
            try
            {
                s = await PrepareRuntimeSandbox(f, options: options, initialization: "printf once >> initialization-count.txt", template: true,
                    imageReference: Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_IMAGE"), verifier: verifier);
                Assert.True(s.Lease.Profile.AdmittedForExecution);
                var storage = (ITenantSandboxRuntimeStore)f.Iam;
                var services = SandboxApprovalServices(f, s, options: options);
                var request = (await services.Approvals.PrepareSandboxAsync(new(s.Tenant, s.Run.AgentRunId), s.Lease.Profile.Id,
                    new("printf canonical-tenant >> output.txt; cat output.txt", TimeoutSeconds: 30), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
                Assert.True(request.Snapshot!.Sandbox!.WillInitialize);
                var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, SandboxHuman(s));
                await services.Approvals.DecideAsync(s.Tenant, request.RequestId, SandboxHuman(s), true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
                var frame = await ResumeSandboxApproval(f, s, services.Approvals);
                var operations = new TenantOperationAuthorizer(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer,
                    new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, TimeProvider.System));
                var target = new TenantSandboxOperationTarget((ITenantSessionStore)f.Iam, (ITenantSandboxProfileStore)f.Iam, storage,
                    s.Resolver, f.Authorizer, f.Iam, p.Images, p.Ready, TimeProvider.System, p.Host);
                var authority = new TenantSandboxExecutionAuthority(operations, storage, (ITenantApprovalStore)f.Iam,
                    (ITenantApprovedOperationStore)f.Iam, (ITenantRunStore)f.Iam, target, f.Authorizer, TimeProvider.System);
                var workspace = new FileSessionWorkspace(root, root + "-templates", publicationResources: new DefinitionPublicationResourceReader(f.Resources, f.Content),
                    sessions: f.SessionStore, instances: f.Instances, sandboxes: f.Sandbox, tenantAuthority: authority);
                var generation = new TenantSandboxGenerationAuthority(f.Iam, (ITenantRunStore)f.Iam, (ITenantApprovalStore)f.Iam,
                    (ITenantApprovedOperationStore)f.Iam, target, f.Authorizer, TimeProvider.System);
                sdk = new(options, f.Sandbox, workspace, f.SessionStore, TimeProvider.System, imageAuthority: p.Images,
                    tenantRuntime: storage, approvedSandbox: services.Execution, tenantExecutionAuthority: authority,
                    tenantRuns: (ITenantRunStore)f.Iam, tenantGenerationAuthority: generation);
                var result = await sdk.ExecuteApprovedAsync(frame, request.RequestId);
                Assert.True(result.Status == "Completed", JsonSerializer.Serialize(result)); Assert.Equal(0, result.ExitCode);
                Assert.Equal("canonical-tenant", result.Stdout);
                var working = AgentWorkspacePhysicalPaths.WorkingDirectory(root, s.Session.AgentInstanceId, s.Session.SessionId);
                Assert.Equal("canonical-tenant", await File.ReadAllTextAsync(Path.Combine(working, "output.txt")));
                Assert.Equal("once", await File.ReadAllTextAsync(Path.Combine(working, "initialization-count.txt")));
                Assert.Equal("immutable admitted template", await File.ReadAllTextAsync(Path.Combine(working, "welcome.txt")));
                var effect = await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId);
                Assert.Equal(TenantOperationOutcome.Completed, effect!.Outcome);
                var replay = await sdk.ExecuteApprovedAsync(frame, request.RequestId); Assert.Equal(result.ExecutionId, replay.ExecutionId);
                Assert.Equal("canonical-tenant", await File.ReadAllTextAsync(Path.Combine(working, "output.txt")));
                Assert.Equal("once", await File.ReadAllTextAsync(Path.Combine(working, "initialization-count.txt")));
                Assert.Null(await f.Sandbox.GetLeaseAsync(frame.SessionId)); Assert.Null(await f.SessionStore.LoadMetadataAsync(frame.SessionId));
                await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
                await sdk.ReconcileAsync();
                var saved = (await storage.ReadTenantSandboxExecutionFactsAsync(s.Tenant, frame.SessionId, result.ExecutionId))!;
                Assert.Equal(SandboxLeaseState.Terminated, saved.Lease!.State); Assert.True(saved.Execution.RemoteTerminationConfirmed);
                Assert.Equal("canonical-tenant", await File.ReadAllTextAsync(Path.Combine(working, "output.txt"))); retired = true;
            }
            finally
            {
                if (!retired && s is not null && sdk is not null)
                {
                    var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
                    if (state.Assignments.Any(a => a.AssignmentId == s.ExecutionGrant))
                        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, s.ExecutionGrant, state.Tenant.Revision);
                    await sdk.ReconcileAsync();
                }
                if (retired && Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
