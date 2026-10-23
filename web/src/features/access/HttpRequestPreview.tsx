import { Alert, Descriptions, Flex, Typography } from 'antd';
import { SavedApprovalText } from './SavedApprovalText';
import type { TenantApproval } from './TenantApprovals';

type Proposal = { method: string; url: string; headers: string; body: string | null; hash: string; disclosure: string };
export function savedHttpRequest(changes: TenantApproval['review']['changes']): Proposal | null {
  const keys = ['httpMethod', 'httpUrl', 'httpHeaders', 'httpBody', 'httpRequestHash', 'httpDisclosure'];
  if (changes.length !== keys.length || !keys.every(key => changes.filter(c => c.field === key && c.afterAvailable).length === 1)) return null;
  const get = (key: string) => changes.find(c => c.field === key)!.after;
  const method = get('httpMethod'), url = get('httpUrl'), headers = get('httpHeaders'), body = get('httpBody'), hash = get('httpRequestHash'), disclosure = get('httpDisclosure');
  if (typeof method !== 'string' || !['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE'].includes(method) || typeof url !== 'string' || url.length > 4096
    || typeof headers !== 'string' || (body !== null && typeof body !== 'string') || typeof hash !== 'string' || !/^[a-f0-9]{64}$/.test(hash) || typeof disclosure !== 'string' || !disclosure.trim()) return null;
  try {
    const target = new URL(url);
    if (!['http:', 'https:'].includes(target.protocol) || target.username || target.password || target.hash) return null;
    const values: unknown = JSON.parse(headers);
    if (!Array.isArray(values) || values.length > 3 || values.some(h => !h || typeof h !== 'object' || Object.keys(h).sort().join(',') !== 'Name,Value'
      || !['Accept', 'Content-Type', 'Accept-Language'].includes(h.Name) || typeof h.Value !== 'string' || h.Value.length > 512)
      || new Set(values.map(h => h.Name)).size !== values.length) return null;
    if (body !== null && (new TextEncoder().encode(body).length > 65536 || body.length > 0 && ['GET', 'HEAD'].includes(method))) return null;
    return { method, url, headers: values.length ? values.map(h => `${h.Name}: ${h.Value}`).join('\n') : 'No additional headers', body, hash, disclosure };
  } catch { return null; }
}
export function HttpRequestPreview({ proposal }: { proposal: Proposal | null }) {
  if (!proposal) return <Alert type="error" showIcon title="Saved HTTP request unavailable" description="Refresh the exact saved request before deciding." />;
  return <Flex vertical gap="var(--ac-space-default)">
    <Descriptions column={1} size="small" items={[
      { key: 'method', label: 'Method', children: proposal.method },
      { key: 'url', label: 'Public URL', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{proposal.url}</Typography.Text> }
    ]} />
    <SavedApprovalText label="Exact headers" text={proposal.headers} />
    {proposal.body === null || proposal.body.length === 0 ? <Typography.Text type="secondary">No request body</Typography.Text> : <SavedApprovalText label="Exact request body" text={proposal.body} />}
    <Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>Request checksum (SHA-256): {proposal.hash}</Typography.Text>
    <Alert type="info" showIcon title="One exact public request" description={proposal.disclosure} />
  </Flex>;
}
