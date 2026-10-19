import { useId } from 'react';
import { Alert, Descriptions, Flex, Input, Typography } from 'antd';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import type { TenantApproval } from './TenantApprovals';

type Publication = { name: string; type: string; bytes: number } & ({ source: true; path: string; checksum: string; disclosure: string; sourceName?: string } | { source: false; content: string });
export function savedArtifactPublication(changes: TenantApproval['review']['changes']): Publication | null {
  const source = changes.some(c => ['artifactSourcePath', 'artifactChecksum', 'publicationDisclosure'].includes(c.field));
  const attachment = String(changes.find(c => c.field === 'artifactSourcePath')?.after ?? '').startsWith('/attachments/');
  const keys = ['artifactDisplayName', 'artifactContentType', 'artifactByteSize', ...(source ? ['artifactSourcePath', 'artifactChecksum', 'publicationDisclosure', ...(attachment ? ['artifactSourceName'] : [])] : ['artifactContent'])];
  if (changes.length !== keys.length || new Set(changes.map(c => c.field)).size !== keys.length
    || changes.some(c => !keys.includes(c.field) || !c.afterAvailable || c.beforeAvailable || c.restoresDefault)) return null;
  const get = (field: string) => changes.find(c => c.field === field)!.after;
  const name = get('artifactDisplayName'); const type = get('artifactContentType'); const bytes = get('artifactByteSize');
  if (typeof name !== 'string' || !name.trim() || name.length > 200 || typeof type !== 'string' || !type.trim() || type.length > 200
    || typeof bytes !== 'number' || !Number.isSafeInteger(bytes) || bytes < 0 || bytes > (source ? 50 * 1024 * 1024 : 32768)) return null;
  if (!source) { const content = get('artifactContent'); return typeof content === 'string' && new TextEncoder().encode(content).length === bytes ? { source, name, type, bytes, content } : null; }
  const path = get('artifactSourcePath'); const checksum = get('artifactChecksum'); const disclosure = get('publicationDisclosure');
  const original = attachment ? get('artifactSourceName') : null;
  if (attachment && (typeof original !== 'string' || !original.trim() || original.length > 200)) return null;
  return typeof path === 'string' && (
    /^\/(?:home|working)\/.+/.test(path) || /^\/agent\/(?:instance\/)?resources\/.+/.test(path)
    || /^\/attachments\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(path) && !path.endsWith('00000000-0000-0000-0000-000000000000'))
    && !path.split('/').some(part => part === '..' || part === '.')
    && typeof checksum === 'string' && /^[0-9a-f]{64}$/.test(checksum) && typeof disclosure === 'string' && !!disclosure.trim()
    ? { source, name, type, bytes, path, checksum, disclosure, ...(typeof original === 'string' ? { sourceName: original } : {}) } : null;
}
export function ArtifactPublicationPreview({ publication }: { publication: Publication | null }) {
  const layout = useAdminDetailLayout(); const id = useId();
  const literal = (text: string) => <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{text}</Typography.Text>;
  if (!publication) return <Alert type="warning" showIcon title="Saved file publication unavailable" description="Refresh details before deciding. If the saved fields remain unavailable, cancel this request and ask for a fresh exact action." />;
  return <Flex vertical gap="var(--ac-space-default)">
    <Descriptions {...layout} column={1} size="small" items={[
      { key: 'name', label: 'File name', children: literal(publication.name) }, { key: 'type', label: 'File type', children: literal(publication.type) },
      ...(publication.source && publication.sourceName ? [{ key: 'original', label: 'Source file', children: literal(publication.sourceName) }] : []),
      ...(publication.source ? [{ key: 'path', label: 'Exact source path', children: literal(publication.path) }] : []),
      { key: 'bytes', label: publication.source ? 'Stored bytes' : 'UTF-8 bytes', children: publication.bytes }
    ]} />
    {publication.source ? <Typography.Text type="secondary">{publication.disclosure}</Typography.Text> : <Flex vertical gap="var(--ac-space-compact)">
      <label htmlFor={id}>Exact file content</label><Input.TextArea id={id} readOnly value={publication.content} autoSize={{ minRows: 6, maxRows: 12 }} />
      <Typography.Text type="secondary">Untrusted file content is shown as plain text. Publication grants no file read access.</Typography.Text>
    </Flex>}
  </Flex>;
}
