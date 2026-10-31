import { useEffect, useMemo, useRef, useState } from 'react';
import { Button, Flex, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { navigateToAppPath } from '../../app/appRoute';
import type { TenantContext } from '../../services/identity';
import { tenantAgentApi, type TenantAgentEditor } from '../../services/tenantAgentConfiguration';
import { tenantActivitySource } from '../../services/tenantActivity';
import { InstanceCommunicationSection } from '../admin/InstanceCommunicationSection';
import { InstanceSessionsSection } from '../admin/InstanceActivitySection';
import { InstanceRunsSection } from '../admin/InstanceRunsSection';
import { AccessError, AccessLoading } from './AccessFeedback';
import { TenantAgentHeading } from './TenantAgentHeading';
import { tenantAccessPath, tenantAgentPath } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';

export function TenantAgentActivity({ context, id, page }: { context: TenantContext; id: string; page: 'activity-sessions' | 'activity-runs' }) {
  const tenant = context.tenant.tenantId;
  const view = useIdentityQuery<TenantAgentEditor>(tenantAgentApi(tenant, id, '/editor'));
  const source = useMemo(() => tenantActivitySource(tenant, id, context.principalId), [tenant, id, context.principalId]);
  const path = tenantAgentPath(tenant, id, page);
  const readRun = () => new URLSearchParams(window.location.search).get('run') ?? undefined;
  const runOpener = useRef<HTMLElement | null>(null);
  const [runId, setRunId] = useState(readRun); const [reload, setReload] = useState(0);
  useEffect(() => { const sync = () => setRunId(readRun()); window.addEventListener('popstate', sync); return () => window.removeEventListener('popstate', sync); }, []);
  const select = (run?: string) => { if (run) runOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null; const params = new URLSearchParams(window.location.search); if (run) params.set('run', run); else params.delete('run'); navigateToAppPath(path + (params.size ? `?${params}` : '')); };
  return <Flex vertical gap="var(--ac-space-section)">
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to Agents</Typography.Link>
      <Button onClick={() => { view.reload(); setReload(value => value + 1); }}>Reload Activity</Button></Flex>
    {view.loading ? <AccessLoading /> : view.error ? <AccessError error={view.error} retry={view.reload} /> : view.data && <>
      <TenantAgentHeading view={view.data} configuration={false} archivedDescription="Activity remains readable. Restore this instance before continuing its conversations." />
      <InstanceCommunicationSection key={`${tenant}:${id}:${context.principalId}`} tenant={tenant} instance={id} />
      {page === 'activity-sessions' ? <InstanceSessionsSection key={`${source.scope}:${reload}`} instanceId={id} open archived={view.data.agent.instance.lifecycle !== 'Active'} source={source} activityPath={path} /> : <>
        <InstanceRunsSection key={`${source.scope}:${reload}`} instanceId={id} open inline onClose={() => undefined} source={source} activityPath={path} onRun={select} />
        <InstanceRunsSection instanceId={id} open={!!runId} selectedAgentRunId={runId} detailsOnly afterClose={() => { if (runOpener.current?.isConnected) runOpener.current.focus(); }} onClose={() => select()} source={source} activityPath={path} />
      </>}
    </>}
  </Flex>;
}
