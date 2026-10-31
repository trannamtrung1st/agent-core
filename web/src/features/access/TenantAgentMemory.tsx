import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Empty, Flex, Input, Select, Table, Tag, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { confirmAction } from '../../app/confirmAction';
import { identityRequest, type TenantContext } from '../../services/identity';
import { tenantAgentApi } from '../../services/tenantAgentConfiguration';
import { tenantMemoryApi, type MemoryScope, type TenantMemoryItem, type TenantMemoryPage } from '../../services/tenantMemory';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantAgentPath } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';
import { TenantMemorySources } from './TenantMemorySources';
import { TenantAgentExperience } from './TenantAgentExperience';
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function TenantAgentMemory({ context, id }: { context: TenantContext; id: string }) {
  const [scope, setScope] = useState<MemoryScope>('identityUser'); const [session, setSession] = useState(''); const [requestedSession, setRequestedSession] = useState('');
  const [page, setPage] = useState(1); const [epoch, setEpoch] = useState(0); const [busy, setBusy] = useState(false);
  return <Flex vertical gap="var(--ac-space-section)">
    <Typography.Link href={tenantAgentPath(context.tenant.tenantId, id, 'activity-sessions')} onClick={followAppLink}>Inspect readable Sessions</Typography.Link>
    <AgentConfigurationPanel title="Your learned Memory" label="Your learned Memory" bodyGap="default"
      description="Inspect facts owned by your organization membership. Independent agent work has separate Session memory. Forgetting learned facts leaves source conversations, Profile preferences, Experience and files independently retained.">
      <Flex wrap align="center" gap="var(--ac-space-compact)"><Select aria-label="Memory scope" value={scope} disabled={busy} onChange={value => { setScope(value); setPage(1); }} options={[
        { value: 'identityUser', label: 'You and this agent' }, { value: 'user', label: 'You across agents' }, { value: 'session', label: 'This Session' }]} />
        {scope === 'session' && <><Input aria-label="Session ID" value={session} disabled={busy} onChange={e => { setSession(e.target.value); setRequestedSession(''); }} placeholder="Session ID" className="access-bounded-form" />
          <Button disabled={busy || !uuid.test(session)} onClick={() => { setRequestedSession(session.toLowerCase()); setPage(1); setEpoch(e => e + 1); }}>Load Session memory</Button></>}
      </Flex>
      {scope === 'session' && !requestedSession ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Choose a readable Session from Activity, then enter its ID." />
        : <MemoryRows key={`${context.tenant.tenantId}:${context.principalId}:${id}:${scope}:${requestedSession}:${page}:${epoch}`} context={context} id={id} scope={scope} session={scope === 'session' ? requestedSession : undefined} page={page}
          onPage={setPage} onBusy={setBusy} reload={() => setEpoch(e => e + 1)} />}
    </AgentConfigurationPanel>
    <TenantAgentExperience context={context} id={id} />
  </Flex>;
}
function MemoryRows({ context, id, scope, session, page, onPage, onBusy, reload }: { context: TenantContext; id: string; scope: MemoryScope; session?: string; page: number; onPage: (v: number) => void; onBusy: (v: boolean) => void; reload: () => void }) {
  const tenant = context.tenant.tenantId; const view = useIdentityQuery<TenantMemoryPage>(tenantMemoryApi(tenant, id, scope, session, page)); const command = useTenantCommand();
  const [notice, setNotice] = useState<string>(); const [invalidated, setInvalidated] = useState(false); const { modal } = App.useApp(); const detail = useAdminDetailLayout();
  const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  useEffect(() => () => { confirmation.current?.destroy(); confirmation.current = null; }, []);
  useEffect(() => { onBusy(command.busy); return () => onBusy(false); }, [command.busy]);
  useEffect(() => { if (command.error !== undefined) setInvalidated(true); }, [command.error]);
  const forget = (item?: TenantMemoryItem) => {
    if (!view.data || command.busy || invalidated || confirmation.current) return; const saved = view.data;
    confirmation.current = confirmAction(modal, { title: item ? 'Forget this learned-memory item?' : 'Forget all readable Memory in this scope?', danger: true,
      content: item ? `${context.tenant.name} · ${saved.agentName}: ${item.subject}. This removes only this learned fact. Source conversations, other learned copies and retained data remain.`
        : `${context.tenant.name} · ${saved.agentName}: Forget ${saved.retainedCount} readable retained facts in this scope, including resolved and superseded facts. Other Users, unreadable origins and other scopes remain independently owned. Source conversations, Experience and files remain.`,
      okText: item ? 'Forget item' : 'Forget scope', cancelText: 'Keep Memory', afterClose: () => { confirmation.current = null; },
      onOk: () => command.run(signal => identityRequest<{ itemsForgotten: number }>(tenantAgentApi(tenant, id, '/memory/forget'), { method: 'POST', signal, body: {
        scope, ...(session ? { sessionId: session } : {}), expectedRevision: saved.securityRevision,
        ...(item ? { memoryId: item.memoryId, expectedMemoryRevision: item.revision } : { expectedRetainedCount: saved.retainedCount })
      } }), result => { setInvalidated(true); setNotice(`${result.itemsForgotten} learned ${result.itemsForgotten === 1 ? 'fact forgotten' : 'facts forgotten'}. Reload Memory to inspect current facts.`); }) });
  };
  const block = command.busy || invalidated || view.loading;
  return <Flex vertical gap="var(--ac-space-default)">
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={reload}>Reload Memory</Button><Button danger disabled={block || !view.data?.canReset} onClick={() => forget()}>Forget readable scope</Button></Flex>
    {notice && <Alert showIcon role="status" type="success" title={notice} />}
    {command.error !== undefined && <><AccessError error={command.error} /><Alert showIcon type="warning" title="Memory needs a fresh read" description="The request is not repeated. Reload Memory to inspect current facts and access before deciding on another change." /></>}
    {!invalidated && (view.loading ? <AccessLoading /> : view.error ? <AccessError error={view.error} retry={reload} /> : view.data && <>
      <Typography.Text strong>Memory for {view.data.agentName}</Typography.Text>
      <Typography.Text type="secondary">{view.data.retainedCount} readable retained facts. Counts include resolved and superseded facts; deleted rows retain only provenance. Retained Memory remains inspectable when recall is disabled.</Typography.Text>
      <>{view.data.total === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No readable learned facts in this scope." /> : <Table<TenantMemoryItem> className="admin-collection-table" size="small" rowKey="memoryId" dataSource={view.data.items} scroll={{ x: 580 }} locale={{ emptyText: 'No readable learned facts in this scope.' }}
        pagination={{ hideOnSinglePage: true, current: view.data.page, pageSize: view.data.pageSize, total: view.data.total, showSizeChanger: false, onChange: onPage, disabled: command.busy }} columns={[
          { title: 'Subject', key: 'subject', render: (_, item) => <Typography.Text>{item.status === 'Deleted' ? 'Forgotten fact' : item.subject}</Typography.Text> },
          { title: 'Kind', dataIndex: 'kind' }, { title: 'Status', key: 'status', render: (_, item) => <Tag>{item.status}</Tag> },
          { title: 'Action', key: 'action', render: (_, item) => <Button danger size="small" disabled={block || !item.canForget} onClick={() => forget(item)} aria-label={`Forget ${item.subject || 'fact'}`}>Forget</Button> }
        ]} expandable={{ expandedRowRender: item => <Flex className="admin-expanded-record" vertical gap="var(--ac-space-default)"><Descriptions {...detail} size="small" column={1} items={[
          { key: 'content', label: 'Content', children: item.status === 'Deleted' ? 'Content removed' : <Typography.Paragraph style={{ whiteSpace: 'pre-wrap' }}>{item.content}</Typography.Paragraph> },
          { key: 'id', label: 'Memory ID', children: <Typography.Text copyable>{item.memoryId}</Typography.Text> },
          { key: 'revision', label: 'Revision', children: item.revision },
          { key: 'origin', label: 'Origin', children: item.sourceMemoryIds?.length ? item.source === 'userExplicit' ? 'Consolidated from explicit user information' : 'Agent consolidation' : item.source === 'userExplicit' ? 'Explicit user information' : 'Agent inferred' },
          { key: 'agent', label: 'Origin agent', children: <Typography.Text copyable>{item.originAgentId}</Typography.Text> },
          { key: 'parent', label: 'Source lineage', children: item.sourceMemoryIds?.length ? `${item.sourceMemoryIds.length} retained sources` : item.supersedesMemoryId ?? 'None' },
          { key: 'time', label: 'Updated', children: new Date(item.updatedAt).toLocaleString() }
        ]} /><TenantMemorySources tenant={tenant} agent={id} scope={scope} session={session} item={item} /></Flex> }} />}</>
    </>)}
  </Flex>;
}
