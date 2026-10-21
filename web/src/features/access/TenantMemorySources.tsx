import { useState } from 'react';
import { Alert, Button, Descriptions, Flex, Tag, Typography } from 'antd';
import { tenantAgentApi } from '../../services/tenantAgentConfiguration';
import type { MemoryScope, TenantMemoryItem, TenantMemoryPage } from '../../services/tenantMemory';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';

export function TenantMemorySources({ tenant, agent, scope, session, item }: { tenant: string; agent: string; scope: MemoryScope; session?: string; item: TenantMemoryItem }) {
  const [open, setOpen] = useState(false); const ids = item.sourceMemoryIds?.length ? item.sourceMemoryIds : item.supersedesMemoryId ? [item.supersedesMemoryId] : [];
  if (!ids.length) return null;
  if (ids.length > 8 || new Set(ids).size !== ids.length) return <Alert type="warning" showIcon title="Source list unavailable" description="Reload Memory before inspecting this lineage." />;
  return <Flex vertical gap="var(--ac-space-default)">
    <Flex><Button onClick={() => setOpen(value => !value)}>{open ? 'Hide sources' : 'View sources'}</Button></Flex>
    {open && ids.map(id => <MemorySource key={id} tenant={tenant} agent={agent} scope={scope} session={session} id={id} />)}
  </Flex>;
}
function MemorySource({ tenant, agent, scope, session, id }: { tenant: string; agent: string; scope: MemoryScope; session?: string; id: string }) {
  const params = new URLSearchParams({ scope }); if (session) params.set('sessionId', session);
  const source = useIdentityQuery<TenantMemoryPage>(tenantAgentApi(tenant, agent, '/memory/' + encodeURIComponent(id)) + '?' + params);
  const layout = useAdminDetailLayout(); const item = source.data?.items.find(i => i.memoryId === id);
  return <Flex vertical gap="var(--ac-space-compact)">
    <Typography.Text copyable>{id}</Typography.Text>
    {source.loading ? <AccessLoading /> : source.error ? <AccessError error={source.error} retry={source.reload} /> : !item ? <Alert type="warning" showIcon title="Source unavailable" description="The exact readable source could not be verified." />
      : <Descriptions {...layout} column={1} size="small" items={[
        { key: 'status', label: 'Source status', children: <Tag>{item.status}</Tag> },
        { key: 'subject', label: 'Subject', children: item.status === 'Deleted' ? 'Forgotten fact' : item.subject },
        { key: 'content', label: 'Content', children: item.status === 'Deleted' ? 'Content removed' : <Typography.Paragraph style={{ whiteSpace: 'pre-wrap' }}>{item.content}</Typography.Paragraph> }
      ]} />}
  </Flex>;
}
