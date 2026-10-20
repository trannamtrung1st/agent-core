import { Alert, Descriptions, Flex, Typography } from 'antd';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import type { TenantApproval } from './TenantApprovals';

type FileMetadata = { id: string; name: string; type: string; bytes: number };
type CompletionRead = { initial: string; source: string; session: string; objective: string; status: string; summary: string | null;
  files: FileMetadata[]; checksum: string; disclosure: string; boundary: string; destination?: string };
const id = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const text = (value: unknown, limit: number): value is string => typeof value === 'string' && !!value.trim() && new TextEncoder().encode(value).length <= limit;
const optionalText = (value: unknown, limit: number) => value === null || typeof value === 'string' && new TextEncoder().encode(value).length <= limit;
export function savedCompletionRead(changes: TenantApproval['review']['changes'], parentSession: string): CompletionRead | null {
  const fields = ['completionInitialRunId', 'completionSourceRunId', 'completionSessionId', 'completionObjective', 'completionObservation', 'completionObservationHash', 'completionMetadata', 'readDisclosure', 'readLimit'];
  if (changes.length !== fields.length || new Set(changes.map(c => c.field)).size !== fields.length
    || changes.some(c => !fields.includes(c.field) || !c.afterAvailable || c.beforeAvailable || c.restoresDefault)) return null;
  const get = (field: string) => changes.find(c => c.field === field)!.after;
  const initial = get('completionInitialRunId'); const source = get('completionSourceRunId'); const session = get('completionSessionId');
  const objective = get('completionObjective'); const raw = get('completionObservation'); const metadata = get('completionMetadata');
  const checksum = get('completionObservationHash'); const disclosure = get('readDisclosure'); const boundary = get('readLimit');
  if (!id(initial) || !id(source) || !id(session) || session === parentSession || !id(parentSession) || !text(objective, 4000)
    || typeof raw !== 'string' || new TextEncoder().encode(raw).length > 20000 || typeof metadata !== 'string' || new TextEncoder().encode(metadata).length > 8192
    || typeof checksum !== 'string' || !/^[0-9a-f]{64}$/.test(checksum) || !text(disclosure, 2000) || !text(boundary, 2000)) return null;
  try {
    const data: unknown = JSON.parse(raw); const savedFiles: unknown = JSON.parse(metadata);
    const observedFields = ['untrusted', 'tenantCompletionRead', 'backgroundSessionId', 'parentSessionId', 'agentRunId', 'sourceAgentRunId', 'status', 'outcome', 'summary', 'updatedAtUtc', 'progress', 'attentionRequired', 'failureCode', 'artifacts', 'artifactsHasMore'];
    if (!object(data) || Object.keys(data).length !== observedFields.length || Object.keys(data).some(key => !observedFields.includes(key)) || data.untrusted !== true || data.tenantCompletionRead !== true || data.backgroundSessionId !== session
      || data.parentSessionId !== parentSession || data.agentRunId !== initial || data.sourceAgentRunId !== source
      || typeof data.status !== 'string' || !['Completed', 'Failed', 'Cancelled'].includes(data.status)
      || !optionalText(data.summary, 2400) || !optionalText(data.progress, 320) || typeof data.attentionRequired !== 'boolean'
      || !(data.outcome === null || ['Response', 'NoAction', 'NeedsAttention'].includes(String(data.outcome)))
      || typeof data.updatedAtUtc !== 'string' || !Number.isFinite(Date.parse(data.updatedAtUtc)) || typeof data.artifactsHasMore !== 'boolean'
      || !Array.isArray(savedFiles) || savedFiles.length > 3 || !Array.isArray(data.artifacts) || data.artifacts.length !== Math.min(savedFiles.length, 2)
      || data.artifactsHasMore !== (savedFiles.length > 2)) return null;
    const files: FileMetadata[] = [];
    for (const file of savedFiles) {
      if (!object(file) || Object.keys(file).length !== 6 || !id(file.ArtifactId) || file.SessionId !== session || !text(file.DisplayName, 800)
        || String(file.DisplayName).length > 200 || !text(file.ContentType, 800) || String(file.ContentType).length > 200 || typeof file.ByteSize !== 'number'
        || !Number.isSafeInteger(file.ByteSize) || file.ByteSize < 0 || typeof file.Sha256Hex !== 'string' || !/^[0-9a-f]{64}$/.test(file.Sha256Hex)) return null;
      files.push({ id: file.ArtifactId, name: file.DisplayName, type: file.ContentType, bytes: file.ByteSize });
    }
    if (new Set(files.map(f => f.id)).size !== files.length || data.artifacts.some((file: unknown, index: number) => !object(file) || file.artifactId !== files[index].id)) return null;
    return { initial, source, session, objective, status: data.status, summary: data.summary as string | null, files, checksum, disclosure, boundary };
  } catch { return null; }
}
export function savedReportSourceRead(changes: TenantApproval['review']['changes'], childSession: string, destination: string | null | undefined): CompletionRead | null {
  const fields = ['reportSourceParentSessionId', 'reportSourceInitialRunId', 'reportSourceRunId', 'reportSourceSessionId', 'reportSourceObjective', 'reportSourceObservation', 'reportSourceHash', 'readDisclosure', 'readLimit'];
  if (changes.length !== fields.length || new Set(changes.map(c => c.field)).size !== fields.length
    || changes.some(c => !fields.includes(c.field) || !c.afterAvailable || c.beforeAvailable || c.restoresDefault)) return null;
  const get = (field: string) => changes.find(c => c.field === field)!.after;
  const parent = get('reportSourceParentSessionId'), initial = get('reportSourceInitialRunId'), source = get('reportSourceRunId'), session = get('reportSourceSessionId');
  const objective = get('reportSourceObjective'), raw = get('reportSourceObservation'), checksum = get('reportSourceHash'), disclosure = get('readDisclosure'), boundary = get('readLimit');
  if (!id(parent) || parent !== destination || !id(initial) || !id(source) || !id(session) || session !== childSession || parent === session
    || !text(objective, 4000) || !text(raw, 8192) || typeof checksum !== 'string' || !/^[0-9a-f]{64}$/.test(checksum)
    || !text(disclosure, 2000) || !text(boundary, 2000)) return null;
  try {
    const signal: unknown = JSON.parse(raw);
    if (!object(signal) || Object.keys(signal).length !== 3 || signal.TriggerKind !== 7 || signal.EnvironmentKind !== null || typeof signal.Text !== 'string') return null;
    const data: unknown = JSON.parse(signal.Text);
    const keys = ['childSessionId', 'initialAgentRunId', 'objective', 'status', 'summary', 'attentionRequired', 'artifacts'];
    if (!object(data) || Object.keys(data).length !== keys.length || Object.keys(data).some(key => !keys.includes(key))
      || data.childSessionId !== session || data.initialAgentRunId !== source || !text(data.objective, 1200) || !optionalText(data.summary, 2400)
      || typeof data.status !== 'string' || !['Completed', 'Failed', 'Cancelled'].includes(data.status) || typeof data.attentionRequired !== 'boolean'
      || !Array.isArray(data.artifacts) || data.artifacts.length !== 0) return null;
    return { initial, source, session, destination: parent, objective, status: data.status, summary: data.summary as string | null, files: [], checksum, disclosure, boundary };
  } catch { return null; }
}
export function CompletionReadPreview({ proposal }: { proposal: CompletionRead | null }) {
  const layout = useAdminDetailLayout();
  const literal = (value: string) => <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{value}</Typography.Text>;
  if (!proposal) return <Alert type="warning" showIcon title="Saved completion read unavailable" description="Refresh details before deciding. If the saved result remains unavailable, cancel this request and ask for a fresh exact read." />;
  return <Flex vertical gap="var(--ac-space-default)">
    <Descriptions {...layout} column={1} size="small" items={[
      ...(proposal.destination ? [{ key: 'destination', label: 'Destination conversation', children: literal(proposal.destination) }] : []),
      { key: 'task', label: 'Task', children: literal(proposal.objective) }, { key: 'status', label: 'Result status', children: proposal.status },
      { key: 'result', label: 'Exact result', children: proposal.summary === null ? 'No result text was recorded.' : literal(proposal.summary) },
      { key: 'initial', label: 'Initial work Run', children: literal(proposal.initial) },
      ...(proposal.initial !== proposal.source ? [{ key: 'source', label: 'Result Run', children: literal(proposal.source) }] : []),
      { key: 'checksum', label: 'Saved observation checksum', children: literal(proposal.checksum) }
    ]} />
    {proposal.files.length > 0 && <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>Related file metadata</Typography.Text>
      <Typography.Text type="secondary">Up to two file links accompany the result. These exact files determine its metadata and more-files indicator. File contents and download access remain separate.</Typography.Text>
      {proposal.files.map(file => <Descriptions key={file.id} {...layout} column={1} size="small" items={[
        { key: 'name', label: 'File name', children: literal(file.name) }, { key: 'type', label: 'File type', children: literal(file.type) },
        { key: 'size', label: 'Stored bytes', children: file.bytes }
      ]} />)}
    </Flex>}
    <Typography.Text type="secondary">{proposal.disclosure}</Typography.Text><Typography.Text type="secondary">{proposal.boundary}{!proposal.destination && ' Taking or acknowledging the result does not mark it handled; the task must save a successful final answer.'}</Typography.Text>
  </Flex>;
}
