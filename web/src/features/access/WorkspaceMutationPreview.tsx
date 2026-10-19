import { useId } from 'react';
import { Alert, Descriptions, Flex, Input, Typography } from 'antd';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';

type Operation = { op: string; path?: string; source?: string; destination?: string; recursive?: boolean };
type Proposal = { tool: string; path?: string; content?: string; edits?: { oldText: string; newText: string }[]; operations?: Operation[] };
const names: Record<string, string> = { write: 'Replace file text', patch: 'Apply exact text edits', mkdir: 'Create directory', copy: 'Copy file or directory', move: 'Move file or directory', delete: 'Delete file or directory', batch: 'Ordered workspace changes' };
const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const exactPath = (value: unknown): value is string => typeof value === 'string' && (value.startsWith('/home/') || value.startsWith('/working/')) && value.length > 6;
const keys = (value: Record<string, unknown>, allowed: string[]) => Object.keys(value).every(key => allowed.includes(key));
function structural(value: unknown, op?: string): Operation | null {
  if (!object(value) || !keys(value, op ? ['path', 'source', 'destination', 'recursive'] : ['op', 'path', 'source', 'destination', 'recursive'])) return null;
  const name = op ?? value.op;
  if (name === 'mkdir' || name === 'delete') {
    if (!exactPath(value.path) || value.source !== undefined || value.destination !== undefined || (name === 'mkdir' ? value.recursive !== undefined : value.recursive !== undefined && typeof value.recursive !== 'boolean')) return null;
    return { op: name, path: value.path, recursive: name === 'delete' ? value.recursive === true : undefined };
  }
  return (name === 'copy' || name === 'move') && exactPath(value.source) && exactPath(value.destination) && value.path === undefined && value.recursive === undefined
    ? { op: name, source: value.source, destination: value.destination } : null;
}
export function savedWorkspaceMutation(tool: unknown, json: unknown): Proposal | null {
  if (typeof tool !== 'string' || !tool.startsWith('workspace.') || typeof json !== 'string') return null;
  try {
    const args: unknown = JSON.parse(json); if (!object(args)) return null;
    if (tool === 'workspace.write') return keys(args, ['path', 'content']) && exactPath(args.path) && typeof args.content === 'string' ? { tool, path: args.path, content: args.content } : null;
    if (tool === 'workspace.patch') {
      if (!keys(args, ['path', 'edits']) || !exactPath(args.path) || !Array.isArray(args.edits) || args.edits.length < 1 || args.edits.length > 32
        || !args.edits.every(edit => object(edit) && keys(edit, ['oldText', 'newText']) && typeof edit.oldText === 'string' && edit.oldText.length > 0 && typeof edit.newText === 'string')) return null;
      return { tool, path: args.path, edits: args.edits };
    }
    if (tool === 'workspace.batch') {
      if (!keys(args, ['operations']) || !Array.isArray(args.operations) || args.operations.length < 1 || args.operations.length > 16) return null;
      const operations = args.operations.map(item => structural(item));
      return operations.every((item): item is Operation => item !== null) ? { tool, operations } : null;
    }
    const item = structural(args, tool.slice('workspace.'.length)); return item ? { tool, operations: [item] } : null;
  } catch { return null; }
}
function Literal({ label, text }: { label: string; text: string }) {
  const id = useId();
  return <Flex vertical gap="var(--ac-space-compact)"><label htmlFor={id}>{label}</label><Input.TextArea id={id} readOnly value={text} autoSize={{ minRows: 3, maxRows: 10 }} />{text.length === 0 && <Typography.Text type="secondary">Empty text</Typography.Text>}</Flex>;
}
export function WorkspaceMutationPreview({ proposal }: { proposal: Proposal | null }) {
  const layout = useAdminDetailLayout();
  const path = (text: string) => <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{text}</Typography.Text>;
  if (!proposal) return <Alert type="warning" showIcon title="Saved workspace changes unavailable" description="Refresh details before deciding. If the saved parameters remain unavailable, cancel this request and ask for a fresh exact action." />;
  return <Flex vertical gap="var(--ac-space-section)">
    <Typography.Text strong>{names[proposal.tool.slice('workspace.'.length)]}</Typography.Text>
    {proposal.path && <Descriptions {...layout} column={1} size="small" items={[{ key: 'path', label: 'Exact path', children: path(proposal.path) }]} />}
    {proposal.content !== undefined && <Literal label="Exact replacement text" text={proposal.content} />}
    {proposal.edits?.map((edit, index) => <Flex key={index} vertical gap="var(--ac-space-compact)"><Typography.Text strong>Edit {index + 1}</Typography.Text><Literal label={`Find exact text, edit ${index + 1}`} text={edit.oldText} /><Literal label={`Replacement text, edit ${index + 1}`} text={edit.newText} /></Flex>)}
    {proposal.operations && <ol style={{ margin: 0, paddingInlineStart: 'var(--ac-space-section)', display: 'flex', flexDirection: 'column', gap: 'var(--ac-space-section)' }}>{proposal.operations.map((operation, index) => <li key={index}><Flex vertical gap="var(--ac-space-compact)">
      <Typography.Text strong>{names[operation.op]}</Typography.Text>
      <Descriptions {...layout} column={1} size="small" items={[
        ...(operation.path ? [{ key: 'path', label: 'Exact path', children: path(operation.path) }] : []),
        ...(operation.source ? [{ key: 'source', label: 'Source', children: path(operation.source) }] : []),
        ...(operation.destination ? [{ key: 'destination', label: 'Destination', children: path(operation.destination) }] : []),
        ...(operation.op === 'delete' ? [{ key: 'recursive', label: 'Include directory contents', children: operation.recursive ? 'Yes — recursive deletion' : 'No — file or empty directory only' }] : [])
      ]} />
    </Flex></li>)}</ol>}
    <Typography.Text type="secondary">Supplied text and paths are untrusted plain text. Current private file contents are not shown. These changes grant no file read or recipient download access.</Typography.Text>
    {(proposal.tool === 'workspace.copy' || proposal.tool === 'workspace.batch') && <Alert type="warning" showIcon title="Partial effects can remain" description="Copies and ordered changes may leave earlier or partial effects if a step fails. There is no atomic rollback. An uncertain operation will not run again." />}
  </Flex>;
}
