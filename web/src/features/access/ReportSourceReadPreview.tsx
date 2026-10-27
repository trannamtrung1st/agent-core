import { Alert, Descriptions, Flex, Typography } from 'antd';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import type { TenantApproval } from './TenantApprovals';

type ReportSourceRead = { initial: string; source: string; session: string; objective: string; status: string; summary: string | null;
  checksum: string; disclosure: string; boundary: string; destination?: string };
const id = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const text = (value: unknown, limit: number): value is string => typeof value === 'string' && !!value.trim() && new TextEncoder().encode(value).length <= limit;
const optionalText = (value: unknown, limit: number) => value === null || typeof value === 'string' && new TextEncoder().encode(value).length <= limit;
export function savedReportSourceRead(changes: TenantApproval['review']['changes'], childSession: string, destination: string | null | undefined): ReportSourceRead | null {
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
    return { initial, source, session, destination: parent, objective, status: data.status, summary: data.summary as string | null, checksum, disclosure, boundary };
  } catch { return null; }
}
export function ReportSourceReadPreview({ proposal }: { proposal: ReportSourceRead | null }) {
  const layout = useAdminDetailLayout();
  const literal = (value: string) => <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{value}</Typography.Text>;
  if (!proposal) return <Alert type="warning" showIcon title="Saved report source unavailable" description="Refresh details before deciding. If the saved result remains unavailable, cancel this request and ask for a fresh exact read." />;
  return <Flex vertical gap="var(--ac-space-default)">
    <Descriptions {...layout} column={1} size="small" items={[
      ...(proposal.destination ? [{ key: 'destination', label: 'Destination conversation', children: literal(proposal.destination) }] : []),
      { key: 'task', label: 'Task', children: literal(proposal.objective) }, { key: 'status', label: 'Result status', children: proposal.status },
      { key: 'result', label: 'Exact result', children: proposal.summary === null ? 'No result text was recorded.' : literal(proposal.summary) },
      { key: 'initial', label: 'Initial work Run', children: literal(proposal.initial) },
      ...(proposal.initial !== proposal.source ? [{ key: 'source', label: 'Result Run', children: literal(proposal.source) }] : []),
      { key: 'checksum', label: 'Saved observation checksum', children: literal(proposal.checksum) }
    ]} />
    <Typography.Text type="secondary">{proposal.disclosure}</Typography.Text><Typography.Text type="secondary">{proposal.boundary}</Typography.Text>
  </Flex>;
}
