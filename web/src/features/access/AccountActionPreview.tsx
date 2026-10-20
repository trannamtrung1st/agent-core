import { Alert, Descriptions, Flex, Typography } from 'antd';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { SavedApprovalText } from './SavedApprovalText';
import type { AccountActionProposal, RemoteAccountFile } from './AccountActionProposal';

export function AccountActionPreview({ proposal }: { proposal: AccountActionProposal | null }) {
  const layout = useAdminDetailLayout();
  const text = (value: string) => <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{value || <Typography.Text type="secondary" aria-label="Not provided">Not provided</Typography.Text>}</Typography.Text>;
  const file = (value: RemoteAccountFile) => <Descriptions {...layout} column={1} size="small" items={[
    { key: 'name', label: 'Name', children: text(value.Name) }, { key: 'id', label: 'File ID', children: text(value.Id) },
    { key: 'version', label: 'Expected version', children: text(value.Version) }, { key: 'type', label: 'File type', children: text(value.MimeType) },
    { key: 'bytes', label: 'Stored bytes', children: value.ByteSize.toLocaleString() }, { key: 'parents', label: 'Current parents', children: text(value.Parents.join('\n')) }
  ]} />;
  if (!proposal) return <Alert type="warning" showIcon title="Saved account action unavailable" description="Refresh details before deciding. If the exact saved content remains unavailable, cancel this request and ask for a fresh action." />;
  return <Flex vertical gap="var(--ac-space-section)">
    <Typography.Text strong>{proposal.title}</Typography.Text>
    <Descriptions {...layout} column={1} size="small" items={[
      { key: 'account', label: 'Account', children: text(proposal.name) }, { key: 'purpose', label: 'Account type', children: text(proposal.purpose) }, { key: 'reference', label: 'Bound reference', children: text(proposal.reference) },
      ...(proposal.email ? [
        { key: 'sender', label: 'From', children: text(proposal.email.sender) }, { key: 'to', label: 'To', children: text(proposal.email.to) },
        { key: 'cc', label: 'Cc', children: text(proposal.email.cc) }, { key: 'bcc', label: 'Bcc', children: text(proposal.email.bcc) },
        { key: 'subject', label: 'Subject', children: text(proposal.email.subject) }, { key: 'draft', label: 'Exact draft', children: text(proposal.email.draft) }
      ] : []),
      ...(proposal.newName ? [{ key: 'new-name', label: 'New name', children: text(proposal.newName) }] : []),
      ...(proposal.sourceParent ? [{ key: 'source-parent', label: 'Move from', children: text(proposal.sourceParent) }] : []),
      ...(proposal.destinationParent ? [{ key: 'destination-parent', label: 'Drive destination', children: text(proposal.destinationParent === 'root' ? 'My Drive (root)' : proposal.destinationParent) }] : []),
      ...(proposal.path ? [{ key: 'path', label: proposal.tool === 'drive.upload' ? 'Original source path' : 'New destination path', children: text(proposal.path) }] : []),
      ...(proposal.sourceBytes !== undefined ? [{ key: 'source-bytes', label: 'Original bytes', children: proposal.sourceBytes.toLocaleString() }] : []),
      ...(proposal.sourceHash ? [{ key: 'source-hash', label: 'Source checksum (SHA-256)', children: text(proposal.sourceHash) }] : [])
    ]} />
    {proposal.email && <SavedApprovalText label="Exact email body" text={proposal.email.body} />}
    {proposal.targets.length > 0 && <section aria-label="Exact Drive targets"><Typography.Title level={5}>Exact Drive targets</Typography.Title><ol style={{ margin: 0, paddingInlineStart: 'var(--ac-space-section)', display: 'flex', flexDirection: 'column', gap: 'var(--ac-space-section)' }}>{proposal.targets.map(target => <li key={target.Id}>{file(target)}</li>)}</ol></section>}
    {proposal.folders.map(folder => <section key={folder.Id} aria-label="Saved destination folder"><Typography.Title level={5}>Saved destination folder</Typography.Title>{file(folder)}</section>)}
    {proposal.tool.startsWith('drive.trash') && <Typography.Text>Move to recoverable Trash. This does not permanently delete the file.</Typography.Text>}
    {proposal.tool === 'drive.trash_batch' && <Alert type="warning" showIcon title="Earlier changes can remain" description="Files are attempted in the saved order. A refusal or uncertain result stops later attempts. Completed changes are retained; uncertain work will not run again." />}
    <Typography.Text>{proposal.disclosure}</Typography.Text>
    <Typography.Text type="secondary">{proposal.boundary}</Typography.Text>
  </Flex>;
}
