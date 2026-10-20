namespace AgentCore.Infrastructure.Security;

internal static class ReportSourceReadApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL) OR COALESCE(({Shape}),0)";
    internal const string Shape = """
        Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'agent-runs.read' AND ResourceType = 'agent-run' AND ResourceId IS RunId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS RunId
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT SessionId
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.Objective') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ReportSourceRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'))
        AND json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.TriggerKind') = 7
        AND json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.InputBoundarySequence') IS NULL
        AND json_type(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text') IS 'text'
        AND json_valid(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'))
        AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.childSessionId') IS SessionId
        AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.initialAgentRunId') IS RunId
        AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.status') IN ('Completed','Failed','Cancelled')
        AND json_array_length(json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.artifacts')) = 0
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000'
        AND json_type(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000'
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
        AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL
        """;
}
