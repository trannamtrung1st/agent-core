import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { App } from 'antd';
import { savedAccountAction } from './AccountActionProposal';
import { AccountActionPreview } from './AccountActionPreview';
import { AccountActionResult, validAccountResult } from './AccountActionResult';
import type { TenantApproval } from './TenantApprovals';

const field = (name: string, after: string | number): TenantApproval['review']['changes'][number] => ({ field: name, after, before: null, beforeAvailable: false, afterAvailable: true, restoresDefault: false });
const remote = (Id = 'exact-file', Version = '7', MimeType = 'application/octet-stream') => ({ Id, Name: 'Original <script>inert</script>.bin', MimeType, ByteSize: 7, Version, Md5Checksum: null, Parents: ['before'] });
const base = (tool: string, args: Record<string, unknown>) => [field('accountName', 'Operations — connected account'), field('accountPurpose', tool.startsWith('email.') ? 'Email' : 'Google Drive'),
  field('accountReference', 'work'), field('accountActionTool', tool), field('accountActionArguments', JSON.stringify({ accountRef: 'work', ...args })),
  field('accountActionDisclosure', 'Apply this exact saved operation once; current source and account access are checked again.'), field('accountActionLimit', 'No role or future action authority is granted.')];
function email(tool = 'email.create_draft') {
  const content = { to: ['friend@example.test'], cc: [], bcc: ['private@example.test'], subject: '<script>Exact subject</script>', body: '<script>window.shouldNotRun = true</script>\nLast exact line' };
  return [...base(tool, tool === 'email.send' ? { draftId: 'exact-draft' } : content), field('emailSender', 'sender@example.test'), field('emailTo', content.to.join('\n')), field('emailCc', ''), field('emailBcc', content.bcc.join('\n')),
    field('emailSubject', content.subject), field('emailBody', content.body), field('emailDraftId', 'exact-draft')];
}
function drive(tool: string) {
  const args = tool === 'drive.create_folder' ? { authEpoch: 1, name: 'Exact folder', parentId: 'root' }
    : tool === 'drive.upload' ? { authEpoch: 1, path: '/working/Original.bin', name: 'Original.bin', folderId: 'root' }
    : tool === 'drive.download' ? { fileId: 'exact-file', expectedVersion: '7', path: '/working/Downloaded.bin' }
    : tool === 'drive.rename' ? { authEpoch: 1, fileId: 'exact-file', expectedVersion: '7', name: 'Exact new name.bin' }
    : tool === 'drive.move' ? { authEpoch: 1, fileId: 'exact-file', expectedVersion: '7', sourceParentId: 'before', destinationParentId: 'after' }
    : tool === 'drive.trash_batch' ? { authEpoch: 1, files: [{ fileId: 'exact-file', expectedVersion: '7' }, { fileId: 'second-file', expectedVersion: '7' }] }
    : { authEpoch: 1, fileId: 'exact-file', expectedVersion: '7' };
  const fields = base(tool, args);
  if (!['drive.create_folder', 'drive.upload'].includes(tool)) fields.push(field('accountRemoteSource', JSON.stringify(remote())));
  if (tool === 'drive.move') fields.push(field('accountRemoteSource', JSON.stringify(remote('after', '1', 'application/vnd.google-apps.folder'))));
  if (tool === 'drive.trash_batch') fields.push(field('accountRemoteSource', JSON.stringify(remote('second-file'))));
  if (tool === 'drive.upload') fields.push(field('accountSourcePath', '/working/Original.bin'), field('accountSourceBytes', 7), field('accountSourceChecksum', 'c'.repeat(64)));
  return fields;
}
afterEach(cleanup);
describe('exact account action presentation', () => {
  it.each(['email.create_draft', 'email.send', 'drive.create_folder', 'drive.upload', 'drive.download', 'drive.rename', 'drive.move', 'drive.trash', 'drive.trash_batch'])('validates and renders the complete saved %s action', tool => {
    const proposal = savedAccountAction(tool.startsWith('email.') ? email(tool) : drive(tool)); expect(proposal).not.toBeNull();
    const { container } = render(<App><AccountActionPreview proposal={proposal} /></App>);
    expect(screen.getByText('Account type')).toBeVisible(); expect(screen.getByText(tool.startsWith('email.') ? 'Email' : 'Google Drive')).toBeVisible(); expect(screen.getByText(proposal!.title)).toBeVisible(); expect(screen.getByText('Operations — connected account')).toBeVisible();
    expect(container.querySelector('script')).toBeNull();
    if (proposal!.email) {
      expect(screen.getByText('private@example.test')).toBeVisible(); expect(screen.getByRole('textbox', { name: 'Exact email body' })).toHaveValue(proposal!.email!.body);
      expect(screen.getByRole('textbox', { name: 'Exact email body' })).toHaveAttribute('readonly'); expect(screen.getByLabelText('Not provided')).toBeVisible();
    }
    if (proposal!.targets.length) expect(screen.getByRole('region', { name: 'Exact Drive targets' })).toHaveTextContent('exact-file');
    if (tool === 'drive.move') expect(screen.getByRole('region', { name: 'Saved destination folder' })).toHaveTextContent('after');
    if (tool === 'drive.trash_batch') expect(screen.getByText('Earlier changes can remain')).toBeVisible();
  });
  it.each(['missing-body', 'changed-body', 'duplicate', 'partial', 'unexpected', 'wrong-sender'])('refuses malformed email evidence: %s', mode => {
    const fields = email();
    if (mode === 'missing-body') fields.splice(fields.findIndex(f => f.field === 'emailBody'), 1);
    if (mode === 'changed-body') fields.find(f => f.field === 'emailBody')!.after = 'Substituted body';
    if (mode === 'duplicate') fields.push(field('emailSubject', 'Other'));
    if (mode === 'partial') fields[0].afterAvailable = false;
    if (mode === 'unexpected') fields.push(field('serverPrincipalId', 'forged'));
    if (mode === 'wrong-sender') fields.find(f => f.field === 'emailSender')!.after = 'Sender\r\nBcc:other@example.test';
    expect(savedAccountAction(fields)).toBeNull();
  });
  it.each(['wrong-version', 'duplicate-target', 'unknown-target', 'missing-source', 'folder-trash'])('refuses contradictory remote evidence: %s', mode => {
    const fields = drive('drive.trash_batch');
    if (mode === 'wrong-version') fields.find(f => f.field === 'accountRemoteSource')!.after = JSON.stringify(remote('exact-file', '8'));
    if (mode === 'duplicate-target') fields.push(field('accountRemoteSource', JSON.stringify(remote())));
    if (mode === 'unknown-target') fields.push(field('accountRemoteSource', JSON.stringify(remote('unrelated'))));
    if (mode === 'missing-source') fields.splice(fields.findIndex(f => f.field === 'accountRemoteSource'), 1);
    if (mode === 'folder-trash') fields.find(f => f.field === 'accountRemoteSource')!.after = JSON.stringify(remote('exact-file', '7', 'application/vnd.google-apps.folder'));
    expect(savedAccountAction(fields)).toBeNull();
  });
  it('keeps a twenty-item manifest in saved order without pagination or hidden targets', () => {
    const files = Array.from({ length: 20 }, (_, index) => ({ fileId: `file-${index}`, expectedVersion: '7' }));
    const proposal = savedAccountAction([...base('drive.trash_batch', { authEpoch: 1, files }), ...files.map(f => field('accountRemoteSource', JSON.stringify(remote(f.fileId))))]);
    expect(proposal?.targets.map(f => f.Id)).toEqual(files.map(f => f.fileId));
    render(<App><AccountActionPreview proposal={proposal} /></App>);
    expect(within(screen.getByRole('region', { name: 'Exact Drive targets' })).getAllByRole('listitem')).toHaveLength(20);
    expect(screen.getByText('file-19')).toBeVisible();
  });
  it('shows the recorded completed prefix, refused item and unattempted suffix without inferring rollback', () => {
    const fields = drive('drive.trash_batch');
    const args = JSON.parse(fields.find(f => f.field === 'accountActionArguments')!.after as string); args.files.push({ fileId: 'third-file', expectedVersion: '7' });
    fields.find(f => f.field === 'accountActionArguments')!.after = JSON.stringify(args); fields.push(field('accountRemoteSource', JSON.stringify(remote('third-file'))));
    const proposal = savedAccountAction(fields)!;
    const result = { status: 'incomplete', draftId: null, fileId: null, path: null, byteSize: null, sha256: null,
      items: [{ fileId: 'exact-file', status: 'completed' }, { fileId: 'second-file', status: 'denied' }, { fileId: 'third-file', status: 'unattempted' }] };
    expect(validAccountResult(proposal, result, 'indeterminate')).toBe(true);
    render(<App><AccountActionResult proposal={proposal} result={result} outcome="indeterminate" /></App>);
    expect(screen.getByRole('list', { name: 'Recorded Drive item outcomes' })).toHaveTextContent('Not attempted');
    expect(validAccountResult(proposal, { ...result, items: [...result.items].reverse() }, 'indeterminate')).toBe(false);
    expect(validAccountResult(proposal, result, 'completed')).toBe(false);
  });
});
