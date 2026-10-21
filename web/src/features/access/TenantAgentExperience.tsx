import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Flex, Switch, Table, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { followAppLink } from '../../app/appLink';
import { identityRequest, type TenantContext } from '../../services/identity';
import { tenantAgentApi } from '../../services/tenantAgentConfiguration';
import { tenantExperienceApi, type ExperienceContent, type ExperienceVisibility, type TenantExperienceItem, type TenantExperiencePage } from '../../services/tenantExperience';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantAgentPath } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

export function TenantAgentExperience({ context, id }: { context: TenantContext; id: string }) {
  const [page, setPage] = useState(1); const [historical, setHistorical] = useState(true); const [epoch, setEpoch] = useState(0); const [busy, setBusy] = useState(false);
  return <AgentConfigurationPanel title="Retained Experience" label="Retained Experience" bodyGap="default"
    description="Derived observations stay separate from learned Memory and instructions. Inspect your private observations and readable Instance observations, including when model recall is off.">
    <Flex wrap align="center" justify="space-between" gap="var(--ac-space-compact)">
      <Flex align="center" gap="var(--ac-space-compact)"><Switch id="experience-history" aria-label="Include Experience history" checked={historical} disabled={busy} onChange={value => { setHistorical(value); setPage(1); }} /><label htmlFor="experience-history">Include superseded and deleted records</label></Flex>
      <Typography.Link href={tenantAgentPath(context.tenant.tenantId, id, 'state-isolation')} onClick={followAppLink}>Experience recall settings</Typography.Link>
    </Flex>
    <ExperienceRows key={`${context.tenant.tenantId}:${context.principalId}:${id}:${page}:${historical}:${epoch}`} context={context} id={id} page={page} historical={historical}
      onPage={setPage} onBusy={setBusy} reload={() => setEpoch(value => value + 1)} />
  </AgentConfigurationPanel>;
}
function ExperienceRows({ context, id, page, historical, onPage, onBusy, reload }: { context: TenantContext; id: string; page: number; historical: boolean; onPage: (value: number) => void; onBusy: (value: boolean) => void; reload: () => void }) {
  const tenant = context.tenant.tenantId; const view = useIdentityQuery<TenantExperiencePage>(tenantExperienceApi(tenant, id, page, historical)); const command = useTenantCommand();
  const [notice, setNotice] = useState<string>(); const [invalidated, setInvalidated] = useState(false); const { modal } = App.useApp(); const detail = useAdminDetailLayout();
  const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  useEffect(() => () => { confirmation.current?.destroy(); confirmation.current = null; }, []);
  useEffect(() => { onBusy(command.busy); return () => onBusy(false); }, [command.busy]);
  useEffect(() => { if (command.error !== undefined) setInvalidated(true); }, [command.error]);
  const change = (visibility: ExperienceVisibility, item?: TenantExperienceItem) => {
    if (!view.data || command.busy || invalidated || confirmation.current) return; const saved = view.data;
    const action = visibility === 'Deleted' ? item ? 'Delete observation' : 'Reset readable Experience' : visibility === 'Suppressed' ? 'Suppress observation' : 'Restore to recall';
    confirmation.current = confirmAction(modal, { title: `${action}?`, danger: visibility === 'Deleted',
      content: item ? `${context.tenant.name} · ${saved.agentName}: ${item.content?.goal ?? 'Retained observation'}. ${visibility === 'Deleted' ? 'This clears this record’s content and retains provenance. Source conversations, other observations, learned Memory, Profile and files remain separately retained.' : visibility === 'Suppressed' ? 'This excludes this observation from model recall. Its content remains available here.' : 'This makes the observation eligible for recall when the agent’s Experience settings and current access permit it.'}`
        : `${context.tenant.name} · ${saved.agentName}: Clear content from ${saved.retainedCount} readable retained observations, including suppressed and superseded records. Other Users’ private records are excluded. Provenance, source history, learned Memory, Profile and files remain.`,
      okText: action, cancelText: 'Keep current state', afterClose: () => { confirmation.current = null; },
      onOk: () => command.run(signal => identityRequest<{ changed: number }>(tenantAgentApi(tenant, id, item ? '/experience/visibility' : '/experience/reset'), { method: 'POST', signal, body: {
        expectedRevision: saved.securityRevision, ...(item ? { experienceId: item.experienceId, expectedExperienceRevision: item.revision, visibility } : { expectedRetainedCount: saved.retainedCount })
      } }), result => { setInvalidated(true); setNotice(`${result.changed} ${result.changed === 1 ? 'observation changed' : 'observations changed'}. Reload Experience to inspect the saved state.`); }) });
  };
  const block = command.busy || invalidated || view.loading;
  return <Flex vertical gap="var(--ac-space-default)">
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={reload}>Reload Experience</Button><Button danger disabled={block || !view.data?.canReset} onClick={() => change('Deleted')}>Reset readable Experience</Button></Flex>
    {notice && <Alert showIcon role="status" type="success" title={notice} />}
    {command.error !== undefined && <><AccessError error={command.error} /><Alert showIcon type="warning" title="Experience needs a fresh read" description="The request is not repeated. Reload Experience to inspect current facts and access before deciding on another change." /></>}
    {!invalidated && (view.loading ? <AccessLoading /> : view.error ? <AccessError error={view.error} retry={reload} /> : view.data && <>
      <Typography.Text strong>Experience for {view.data.agentName}</Typography.Text>
      <Typography.Text type="secondary">{view.data.retainedCount} readable retained observations. Suppressed records stay inspectable; superseded records retain lineage. Deleted records contain provenance only.</Typography.Text>
      <Table<TenantExperienceItem> className="admin-collection-table" size="small" rowKey="experienceId" dataSource={view.data.items} scroll={{ x: 580 }} locale={{ emptyText: 'No readable Experience observations.' }}
        pagination={{ current: view.data.page, pageSize: view.data.pageSize, total: view.data.total, showSizeChanger: false, onChange: onPage, disabled: command.busy }} columns={[
          { title: 'Goal', key: 'goal', ellipsis: true, render: (_, item) => item.content?.goal ?? 'Content removed' },
          { title: 'Owner', key: 'owner', render: (_, item) => item.scope === 'AgentUser' ? 'You and this agent' : 'Instance' },
          { title: 'Visibility', key: 'visibility', render: (_, item) => <Tag>{item.visibility}</Tag> },
          { title: 'Actions', key: 'actions', render: (_, item) => <Flex wrap gap="var(--ac-space-compact)">
            {(item.visibility === 'Eligible' || item.visibility === 'Suppressed') && <Button size="small" disabled={block || !item.canManage} onClick={() => change(item.visibility === 'Eligible' ? 'Suppressed' : 'Eligible', item)}
              aria-label={`${item.visibility === 'Eligible' ? 'Suppress' : 'Restore'} ${item.content?.goal ?? 'observation'}`}>{item.visibility === 'Eligible' ? 'Suppress' : 'Restore to recall'}</Button>}
            <Button danger size="small" disabled={block || !item.canManage || item.visibility === 'Deleted'} onClick={() => change('Deleted', item)} aria-label={`Delete ${item.content?.goal ?? 'observation'}`}>Delete</Button>
          </Flex> }
        ]} expandable={{ expandedRowRender: item => <Descriptions className="admin-expanded-record" {...detail} size="small" column={1} items={[
          { key: 'id', label: 'Experience ID', children: <Typography.Text copyable>{item.experienceId}</Typography.Text> },
          { key: 'goal', label: 'Goal', children: item.content?.goal ?? 'Content removed' },
          ...(['attempts', 'decisions', 'outcomes', 'corrections', 'unresolved', 'difficulties', 'lessons'] as const).map(key => ({ key, label: key[0].toUpperCase() + key.slice(1), children: <ContentLines content={item.content} field={key} /> })),
          { key: 'origin', label: 'Source', children: <Flex vertical gap="var(--ac-space-tight)"><Typography.Text>{item.sourceKind}</Typography.Text><Typography.Text copyable>{item.sourceId}</Typography.Text></Flex> },
          { key: 'cursor', label: 'Observed through', children: item.throughCursor || 'Consolidated observation' },
          { key: 'parents', label: 'Immediate source lineage', children: item.sourceExperienceIds.length ? <Flex vertical gap="var(--ac-space-tight)">{item.sourceExperienceIds.map(source => <Typography.Text key={source} copyable>{source}</Typography.Text>)}</Flex> : 'None' },
          { key: 'revision', label: 'Revision', children: item.revision }, { key: 'updated', label: 'Updated', children: new Date(item.updatedAt).toLocaleString() }
        ]} /> }} />
    </>)}
  </Flex>;
}
function ContentLines({ content, field }: { content: ExperienceContent | null; field: Exclude<keyof ExperienceContent, 'goal'> }) {
  return !content ? <>Content removed</> : content[field].length ? <Typography.Paragraph style={{ whiteSpace: 'pre-wrap' }}>{content[field].join('\n')}</Typography.Paragraph> : <>None recorded</>;
}
