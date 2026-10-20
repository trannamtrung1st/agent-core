import type { TenantApproval } from './TenantApprovals';

export type RemoteAccountFile = { Id: string; Name: string; MimeType: string; ByteSize: number; Version: string; Md5Checksum: string | null; Parents: string[] };
export type AccountActionProposal = { tool: string; name: string; purpose: string; reference: string; title: string; disclosure: string; boundary: string;
  email?: { sender: string; to: string; cc: string; bcc: string; subject: string; body: string; draft: string };
  targets: RemoteAccountFile[]; folders: RemoteAccountFile[]; path?: string; sourceBytes?: number; sourceHash?: string;
  newName?: string; sourceParent?: string; destinationParent?: string };
const object = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const keys = (v: Record<string, unknown>, fields: string[]) => Object.keys(v).length === fields.length && fields.every(f => Object.hasOwn(v, f));
const text = (v: unknown, max: number, empty = false): v is string => typeof v === 'string' && (empty || v.length > 0) && v.length <= max;
const control = /[\u0000-\u001f\u007f-\u009f]/;
const id = (v: unknown): v is string => text(v, 160) && /^[a-zA-Z0-9_-]+$/.test(v);
const version = (v: unknown): v is string => text(v, 64) && !control.test(v);
const path = (v: unknown): v is string => text(v, 512) && /^\/(home|working)\/.+/.test(v) && !control.test(v) && !/[?*\[\]\\]/.test(v) && !v.endsWith('/') && !v.includes('//') && !v.split('/').some(p => p === '.' || p === '..');
const address = (v: unknown): v is string => text(v, 254) && !control.test(v) && !/[<>\s]/.test(v) && /^[^@]+@[^@]+$/.test(v);
const addresses = (v: unknown): v is string[] => Array.isArray(v) && v.length <= 20 && v.every(address);
const names: Record<string, string> = { 'email.create_draft': 'Save email draft', 'email.send': 'Send email', 'drive.create_folder': 'Create Drive folder',
  'drive.upload': 'Upload original file', 'drive.download': 'Download original file', 'drive.rename': 'Rename Drive file', 'drive.move': 'Move Drive file', 'drive.trash': 'Move Drive file to Trash', 'drive.trash_batch': 'Move Drive files to Trash' };
export function savedAccountAction(changes: TenantApproval['review']['changes']): AccountActionProposal | null {
  try {
    const common = ['accountName', 'accountPurpose', 'accountReference', 'accountActionTool', 'accountActionArguments', 'accountActionDisclosure', 'accountActionLimit'];
    const emailFields = ['emailSender', 'emailTo', 'emailCc', 'emailBcc', 'emailSubject', 'emailBody', 'emailDraftId'];
    const uploadFields = ['accountSourcePath', 'accountSourceBytes', 'accountSourceChecksum'];
    if (changes.length > 31 || changes.some(c => !c.afterAvailable || c.beforeAvailable || c.restoresDefault)) return null;
    for (const field of new Set(changes.map(c => c.field))) if (field !== 'accountRemoteSource' && changes.filter(c => c.field === field).length !== 1) return null;
    const get = (field: string) => changes.find(c => c.field === field)?.after;
    const tool = get('accountActionTool'), name = get('accountName'), reference = get('accountReference'), raw = get('accountActionArguments');
    const disclosure = get('accountActionDisclosure'), boundary = get('accountActionLimit');
    if (!text(tool, 64) || !Object.hasOwn(names, tool) || !text(name, 120) || control.test(name) || !text(reference, 120)
      || !text(raw, 49152) || new TextEncoder().encode(raw).length > 49152 || !text(disclosure, 2000) || !text(boundary, 2000)) return null;
    const args: unknown = JSON.parse(raw); if (!object(args) || args.accountRef !== reference) return null;
    const email = tool.startsWith('email.');
    const allowed = [...common, ...(email ? emailFields : ['accountRemoteSource', ...(tool === 'drive.upload' ? uploadFields : [])])];
    if (changes.some(c => !allowed.includes(c.field)) || common.some(f => changes.filter(c => c.field === f).length !== 1)
      || get('accountPurpose') !== (email ? 'Email' : 'Google Drive')) return null;
    const proposal: AccountActionProposal = { tool, name, purpose: email ? 'Email' : 'Google Drive', reference, title: names[tool], disclosure, boundary, targets: [], folders: [] };
    if (email) {
      const sender = get('emailSender'), to = get('emailTo'), cc = get('emailCc'), bcc = get('emailBcc'), subject = get('emailSubject'), body = get('emailBody'), draft = get('emailDraftId');
      if (changes.length !== common.length + emailFields.length || !address(sender) || !text(to, 5100) || !text(cc, 5100, true) || !text(bcc, 5100, true)
        || ![to, cc, bcc].every(list => !list || addresses(list.split('\n'))) || !text(subject, 500, true) || control.test(subject)
        || !text(body, 32000, true) || /[\u0000]/.test(body) || !text(draft, 200) || control.test(draft)) return null;
      if (tool === 'email.send') { if (!keys(args, ['accountRef', 'draftId']) || args.draftId !== draft) return null; }
      else if (!keys(args, ['accountRef', 'to', 'cc', 'bcc', 'subject', 'body']) || !addresses(args.to) || args.to.length < 1 || !addresses(args.cc) || !addresses(args.bcc)
        || args.to.join('\n') !== to || args.cc.join('\n') !== cc || args.bcc.join('\n') !== bcc || args.subject !== subject || args.body !== body) return null;
      return { ...proposal, email: { sender, to, cc, bcc, subject, body, draft } };
    }
    const fields = tool === 'drive.create_folder' ? ['accountRef', 'authEpoch', 'name', 'parentId']
      : tool === 'drive.upload' ? ['accountRef', 'authEpoch', 'path', 'name', 'folderId']
      : tool === 'drive.download' ? ['accountRef', 'fileId', 'expectedVersion', 'path']
      : tool === 'drive.rename' ? ['accountRef', 'authEpoch', 'fileId', 'expectedVersion', 'name']
      : tool === 'drive.move' ? ['accountRef', 'authEpoch', 'fileId', 'expectedVersion', 'sourceParentId', 'destinationParentId']
      : tool === 'drive.trash_batch' ? ['accountRef', 'authEpoch', 'files'] : ['accountRef', 'authEpoch', 'fileId', 'expectedVersion'];
    if (!keys(args, fields) || tool !== 'drive.download' && (typeof args.authEpoch !== 'number' || !Number.isSafeInteger(args.authEpoch) || args.authEpoch < 1)) return null;
    const remote: RemoteAccountFile[] = [];
    for (const change of changes.filter(c => c.field === 'accountRemoteSource')) {
      if (!text(change.after, 32768)) return null; const file: unknown = JSON.parse(change.after);
      if (!object(file) || !keys(file, ['Id', 'Name', 'MimeType', 'ByteSize', 'Version', 'Md5Checksum', 'Parents']) || !id(file.Id) || !text(file.Name, 255) || control.test(file.Name)
        || !text(file.MimeType, 255) || control.test(file.MimeType) || !version(file.Version) || typeof file.ByteSize !== 'number' || !Number.isSafeInteger(file.ByteSize) || file.ByteSize < 0
        || !(file.Md5Checksum === null || typeof file.Md5Checksum === 'string' && /^[0-9a-fA-F]{32}$/.test(file.Md5Checksum)) || !Array.isArray(file.Parents) || file.Parents.length > 2 || !file.Parents.every(id)) return null;
      remote.push(file as RemoteAccountFile);
    }
    if (remote.length > 21 || new Set(remote.map(f => f.Id)).size !== remote.length) return null;
    const used = new Set<string>();
    const target = (fileId: unknown, expected: unknown) => {
      if (!id(fileId) || !version(expected)) throw new Error('Saved target unavailable');
      const file = remote.find(f => f.Id === fileId); if (!file || file.Version !== expected) throw new Error('Saved target changed');
      if (tool === 'drive.download' && file.MimeType.startsWith('application/vnd.google-apps.') || tool.startsWith('drive.trash') && file.MimeType === 'application/vnd.google-apps.folder') throw new Error('Unsupported target');
      proposal.targets.push(file); used.add(file.Id);
    };
    if (tool === 'drive.trash_batch') {
      if (!Array.isArray(args.files) || args.files.length < 1 || args.files.length > 20 || !args.files.every(f => object(f) && keys(f, ['fileId', 'expectedVersion']))) return null;
      for (const file of args.files) target(file.fileId, file.expectedVersion);
      if (new Set(proposal.targets.map(f => f.Id)).size !== proposal.targets.length) return null;
      proposal.title = `Move ${proposal.targets.length} Drive ${proposal.targets.length === 1 ? 'file' : 'files'} to Trash`;
    } else if (Object.hasOwn(args, 'fileId')) target(args.fileId, args.expectedVersion);
    if (Object.hasOwn(args, 'name')) { if (!text(args.name, 255) || control.test(args.name) || /[/\\]/.test(args.name)) return null; proposal.newName = args.name; }
    const destination = tool === 'drive.create_folder' ? args.parentId : tool === 'drive.upload' ? args.folderId : tool === 'drive.move' ? args.destinationParentId : undefined;
    if (destination !== undefined) {
      if (!id(destination)) return null; proposal.destinationParent = destination;
      if (destination !== 'root') { const folder = remote.find(f => f.Id === destination); if (!folder || folder.MimeType !== 'application/vnd.google-apps.folder') return null; proposal.folders.push(folder); used.add(folder.Id); }
    }
    if (tool === 'drive.move') { if (!id(args.sourceParentId) || args.sourceParentId === destination || destination === 'root' || !proposal.targets[0].Parents.includes(args.sourceParentId)) return null; proposal.sourceParent = args.sourceParentId; }
    if (tool === 'drive.upload' || tool === 'drive.download') { if (!path(args.path)) return null; proposal.path = args.path; }
    if (tool === 'drive.upload') {
      const bytes = get('accountSourceBytes'), hash = get('accountSourceChecksum');
      if (get('accountSourcePath') !== args.path || typeof bytes !== 'number' || !Number.isSafeInteger(bytes) || bytes < 0 || bytes > 50 * 1024 * 1024 || typeof hash !== 'string' || !/^[0-9a-f]{64}$/.test(hash)) return null;
      proposal.sourceBytes = bytes; proposal.sourceHash = hash;
    }
    if (tool === 'drive.download' && proposal.targets[0].ByteSize > 50 * 1024 * 1024 || remote.some(f => !used.has(f.Id))) return null;
    return proposal;
  } catch { return null; }
}
