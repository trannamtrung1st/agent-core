namespace AgentCore.Infrastructure.Security;

internal static class CompletionReadApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL) OR COALESCE(({Shape}),0)";
    internal const string Shape = """
        Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'agent-runs.read' AND ResourceType = 'agent-run'
        AND ResourceId IS json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId')
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.CompletionRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.CompletionRead.ParentSessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.CompletionRead.SourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.CompletionRead.Objective') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ObservationJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 20000
        AND json_valid(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.untrusted') = 1
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.tenantCompletionRead') = 1
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.backgroundSessionId') IS json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.parentSessionId') IS SessionId
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.agentRunId') IS json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId')
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.sourceAgentRunId') IS ResourceId
        AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.status') IN ('Completed','Failed','Cancelled')
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') AS BLOB)) <= 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) <= 3
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_type(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT SessionId
        AND json_type(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT SessionId
        AND json_type(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT SessionId
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL
        """;
}
