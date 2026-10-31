import { useEffect, useState } from 'react';
import { Alert, Button, Descriptions, Flex, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import type { TenantContext } from '../../services/identity';
import { tenantAgentApi } from '../../services/tenantAgentConfiguration';
import type { TenantRuntimeState } from '../../services/tenantRuntimeState';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantAccessPath, tenantAgentPath } from './accessRoute';
import { TenantAgentHeading } from './TenantAgentHeading';
import { useIdentityQuery } from './useIdentityQuery';

const count = (value: number | null | undefined) => value == null ? 'Unknown' : value.toLocaleString();
const reasons: Record<string, string> = {
  read_restricted: 'This source requires additional read access. Its counts are unknown.',
  observation_timeout: 'This source did not respond within the observation window. Refresh to try again.',
  observation_unavailable: 'This source could not be observed. Refresh to try again.',
  provider_unavailable: 'Live provider state is unavailable. Counts do not confirm native resources are running.',
  resource_busy: 'This resource is busy. Its current counts are unknown.',
  cleanup_pending: 'Resource retirement is still pending. Counts do not confirm cleanup.',
  policy_transition_pending: 'A resource transition is still pending. Refresh after it settles.',
  reconciliation_required: 'Stored resource state needs reconciliation. Running counts are unknown.',
  observation_limit: 'The observation limit was reached. Complete resource counts are unknown.'
};
export function TenantAgentState({ context, id }: { context: TenantContext; id: string }) {
  return <TenantStateObservation key={`${context.tenant.tenantId}:${id}`} context={context} id={id} />;
}
function TenantStateObservation({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const state = useIdentityQuery<TenantRuntimeState>(tenantAgentApi(tenant, id, '/runtime-state'));
  const detail = useAdminDetailLayout(); const [now, setNow] = useState(Date.now);
  useEffect(() => { const interval = window.setInterval(() => setNow(Date.now()), 1000); return () => window.clearInterval(interval); }, []);
  useEffect(() => { if (state.data) setNow(Date.now()); }, [state.data]);
  const view = state.data; const observed = view?.observation;
  const expired = !!observed && Date.parse(observed.freshThroughUtc) <= now;
  const issue = (kind: string) => observed?.issues.find(item => item.kind === kind)?.code;
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to Agents</Typography.Link><Button disabled={state.loading} onClick={state.reload}>Refresh state</Button></Flex>
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : view && observed && <>
      <TenantAgentHeading view={{ agent: view.agent, canUpdate: false }} configuration={false} archivedDescription="Runtime observations remain read-only for this archived agent." />
      <Typography.Paragraph type="secondary">Only resources you can read contribute. Counts grant no execution authority and do not prove login, logout or remote cleanup. Refresh observes existing resources without starting work.</Typography.Paragraph>
      <Typography.Text type="secondary">Observed {new Date(observed.observedAtUtc).toLocaleString()} · Valid through {new Date(observed.freshThroughUtc).toLocaleString()}</Typography.Text>
      {expired ? <Alert type="warning" showIcon title="This observation expired" description="Refresh state to read current counts and permissions." /> : <>
        {observed.availability !== 'available' && <Alert type="info" showIcon title="Some state is unknown" description="Restricted, busy or unavailable sources keep their counts unknown. Other readable observations remain available." />}
        <AgentConfigurationPanel title="Sessions" label="Visible Session state" description="Visible, unarchived Sessions for this agent. No total for hidden Sessions is disclosed." bodyGap="default">
          {issue('sessions') && <Typography.Text type="secondary">{reasons[issue('sessions')!] ?? reasons.observation_unavailable}</Typography.Text>}
          <Descriptions {...detail} column={1} size="small" items={[
            { key: 'open', label: 'Open Sessions', children: count(observed.sessions?.open) },
            { key: 'paused', label: 'Paused Sessions', children: count(observed.sessions?.paused) },
            { key: 'terminal', label: 'Terminal Sessions', children: count(observed.sessions?.terminal) },
            { key: 'runtime', label: 'Hosted Sessions', children: count(observed.hostedSessions?.runtimes) },
            { key: 'attached', label: 'Sessions with connected recipients', children: count(observed.hostedSessions?.attached) }
          ]} />
          {issue('hostedSessions') && <Typography.Text type="secondary">Hosted state: {reasons[issue('hostedSessions')!] ?? reasons.observation_unavailable}</Typography.Text>}
          <Typography.Link href={tenantAgentPath(tenant, id, 'activity-sessions')} onClick={followAppLink}>Inspect readable Sessions in Activity</Typography.Link>
        </AgentConfigurationPanel>
        <AgentConfigurationPanel title="Runs" label="Visible Run state" description="Runs require both Session and Run read access. Counts cover only that readable scope." bodyGap="default">
          {issue('runs') && <Typography.Text type="secondary">{reasons[issue('runs')!] ?? reasons.observation_unavailable}</Typography.Text>}
          <Descriptions {...detail} column={1} size="small" items={[
            ['queued', 'Queued'], ['executing', 'Executing'], ['waitingApproval', 'Waiting for approval'], ['waitingSignal', 'Waiting for signal'], ['retrying', 'Retrying'], ['terminal', 'Terminal']
          ].map(([key, label]) => ({ key, label, children: count(observed.runs?.[key as keyof NonNullable<typeof observed.runs>]) }))} />
          <Typography.Link href={tenantAgentPath(tenant, id, 'activity-runs')} onClick={followAppLink}>Inspect readable Runs in Activity</Typography.Link>
        </AgentConfigurationPanel>
        {observed.resources.map(resource => <AgentConfigurationPanel key={resource.kind} title={resource.kind === 'browser' ? 'Browser' : 'Sandbox'} label={resource.kind === 'browser' ? 'Readable Browser state' : 'Readable Sandbox state'} bodyGap="default"
          description={resource.kind === 'browser' ? 'Existing context counts only. Shared contexts are counted once.' : 'Stored, readable lease metadata. This observation does not contact the provider or inspect command output.'}>
          {resource.code && <Typography.Text type="secondary">{reasons[resource.code] ?? reasons.observation_unavailable}</Typography.Text>}
          <Descriptions {...detail} column={1} size="small" items={(resource.kind === 'browser'
            ? [['contexts', 'Live contexts', resource.liveContexts], ['pages', 'Open pages', resource.openPages]]
            : [['generations', 'Active generations', resource.activeGenerations], ['commands', 'Active commands', resource.activeCommands], ['cleanup', 'Cleanup pending', resource.cleanupPending]])
            .map(([key, label, value]) => ({ key: String(key), label: String(label), children: count(value as number | null | undefined) }))} />
        </AgentConfigurationPanel>)}
      </>}
    </>}
  </Flex>;
}
