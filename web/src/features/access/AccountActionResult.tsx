import { Descriptions, Flex, Typography } from 'antd';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import type { AccountActionProposal } from './AccountActionProposal';

export type AccountActionRecordedResult = { status: string; draftId: string | null; fileId: string | null; path: string | null;
  byteSize: number | null; sha256: string | null; items: { fileId: string; status: string }[] | null };
const labels: Record<string, string> = { completed: 'Completed', denied: 'Not applied', indeterminate: 'Outcome uncertain', unattempted: 'Not attempted' };
const id = (value: unknown) => typeof value === 'string' && /^[A-Za-z0-9_-]{1,160}$/.test(value);
export function validAccountResult(proposal: AccountActionProposal, value: AccountActionRecordedResult, outcome: string) {
  if (!value || typeof value !== 'object') return false;
  if (proposal.tool === 'drive.trash_batch') {
    if (value.status !== (outcome === 'completed' ? 'completed' : 'incomplete') || value.draftId !== null || value.fileId !== null || value.path !== null || value.byteSize !== null || value.sha256 !== null
      || !Array.isArray(value.items) || value.items.length !== proposal.targets.length || value.items.some((item, index) => !item || item.fileId !== proposal.targets[index].Id || !Object.hasOwn(labels, item.status))) return false;
    const preflight = value.items.every(item => item.status === 'denied' || item.status === 'unattempted'); let stopped = false;
    for (const item of value.items) { if (!preflight && stopped && item.status !== 'unattempted') return false; stopped ||= item.status !== 'completed'; }
    return value.items.filter(item => item.status === 'denied').length <= 1 && (outcome === 'completed' ? !stopped : stopped && (outcome !== 'denied' || preflight));
  }
  const status = proposal.tool === 'email.create_draft' ? 'draft_created' : proposal.tool === 'email.send' ? 'sent' : proposal.tool === 'drive.download' ? 'downloaded' : 'completed';
  if (proposal.tool === 'email.send' && outcome === 'denied' && value.status === 'already_sent') { /* A historical receipt, no additional send. */ }
  else if (outcome !== 'completed' || value.status !== status) return false;
  if (proposal.email) return value.draftId === proposal.email.draft && value.fileId === null && value.path === null && value.byteSize === null && value.sha256 === null && value.items === null;
  if (value.draftId !== null || value.items !== null || !id(value.fileId)) return false;
  if (proposal.tool === 'drive.download') return value.fileId === proposal.targets[0].Id && value.path === proposal.path && value.byteSize === proposal.targets[0].ByteSize && typeof value.sha256 === 'string' && /^[0-9a-f]{64}$/.test(value.sha256);
  return value.path === null && value.byteSize === null && value.sha256 === null && (proposal.targets.length === 0 || value.fileId === proposal.targets[0].Id);
}
export function AccountActionResult({ proposal, result, outcome }: { proposal: AccountActionProposal | null; result?: AccountActionRecordedResult | null; outcome: string }) {
  const layout = useAdminDetailLayout();
  const text = (value: string) => <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{value}</Typography.Text>;
  if (!proposal || !result || !validAccountResult(proposal, result, outcome)) return <Typography.Text type="secondary">Detailed account results are not retained or are unavailable under current access. The recorded operation outcome remains separate; missing details do not permit replay.</Typography.Text>;
  return <Flex vertical gap="var(--ac-space-default)">
    {result.items ? <ol aria-label="Recorded Drive item outcomes" style={{ margin: 0, paddingInlineStart: 'var(--ac-space-section)', display: 'flex', flexDirection: 'column', gap: 'var(--ac-space-default)' }}>{result.items.map((item, index) => <li key={item.fileId}><Descriptions {...layout} column={1} size="small" items={[
      { key: 'name', label: 'File', children: text(proposal.targets[index].Name) }, { key: 'id', label: 'File ID', children: text(item.fileId) }, { key: 'outcome', label: 'Recorded outcome', children: labels[item.status] }
    ]} /></li>)}</ol> : <Descriptions {...layout} column={1} size="small" items={[
      ...(result.draftId ? [{ key: 'draft', label: 'Recorded draft', children: text(result.draftId) }] : []),
      ...(result.fileId ? [{ key: 'file', label: 'Recorded Drive file', children: text(result.fileId) }] : []),
      ...(result.path ? [{ key: 'path', label: 'Saved destination', children: text(result.path) }] : []),
      ...(result.byteSize !== null ? [{ key: 'bytes', label: 'Original bytes saved', children: result.byteSize.toLocaleString() }] : []),
      ...(result.sha256 ? [{ key: 'sha', label: 'Saved checksum (SHA-256)', children: text(result.sha256) }] : [])
    ]} />}
  </Flex>;
}
