import { useEffect, useState } from 'react';
import { Alert, Button, Descriptions, Flex, Spin, Tag, Typography, theme } from 'antd';
import { getRuntimeState, type RuntimeState } from '../../services/runtimeState';
import { followAppLink } from '../../app/appLink';
import { instancePath } from './adminNavigation';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';

const count = (value: number | null | undefined) => value ?? 'Unknown';
const availability = (value: string) => ({ available: 'Available', partial: 'Partial', unavailable: 'Unavailable', unknown: 'Unknown' })[value] ?? 'Unknown';
const sharing = (value: string) => ({ isolated: 'Session isolated', sharedCoordinated: 'Shared profile', exclusive: 'Exclusive' })[value] ?? 'Unknown';
const issue = (code: string) => ({ provider_unavailable: 'Provider unavailable', resource_busy: 'Observation busy; retry when the current operation settles',
  policy_transition_pending: 'Browser transition pending; waiting for shared-context work to settle',
  reconciliation_required: 'Resource reconciliation required', observation_timeout: 'Observation timed out', observation_limit: 'Observation limit reached',
  observation_unavailable: 'Observation unavailable' })[code] ?? 'Observation unavailable';

export function InstanceStateSection({ instanceId, active }: { instanceId: string; active: boolean }) {
  const { token } = theme.useToken();
  const [result, setResult] = useState<RuntimeState>();
  const [failure, setFailure] = useState<{ owner: string; message: string }>();
  const [loading, setLoading] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!active) return;
    let controller: AbortController | undefined;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let stopped = false;
    const refresh = async () => {
      if (stopped || document.visibilityState === 'hidden') return;
      controller?.abort();
      const request = new AbortController(); controller = request; setLoading(true);
      try {
        const next = await getRuntimeState(instanceId, request.signal);
        if (!stopped && !request.signal.aborted && next.agentInstanceId.toLowerCase() === instanceId.toLowerCase()) {
          setResult(next); setFailure(undefined); setNow(Date.now());
        }
      } catch {
        if (!stopped && !request.signal.aborted) setFailure({ owner: instanceId, message: 'State could not be refreshed. The last observation is stale.' });
      } finally {
        if (!stopped && !request.signal.aborted) {
          setLoading(false); timer = setTimeout(() => void refresh(), 10_000);
        }
      }
    };
    const visibility = () => {
      clearTimeout(timer); controller?.abort(); setLoading(false); setNow(Date.now());
      if (document.visibilityState !== 'hidden') void refresh();
    };
    const age = setInterval(() => setNow(Date.now()), 1_000);
    document.addEventListener('visibilitychange', visibility);
    void refresh();
    return () => { stopped = true; controller?.abort(); clearTimeout(timer); clearInterval(age); document.removeEventListener('visibilitychange', visibility); };
  }, [instanceId, active, attempt]);
  const data = result?.agentInstanceId.toLowerCase() === instanceId.toLowerCase() ? result : undefined;
  const error = failure?.owner === instanceId ? failure.message : undefined;
  const stale = !!data && (!!error || Date.parse(data.freshThroughUtc) <= now);
  const refresh = <Button aria-label={error ? 'Retry state' : 'Refresh state'} loading={loading} disabled={!active} onClick={() => setAttempt(value => value + 1)}>{error ? 'Retry state' : 'Refresh state'}</Button>;
  return <Flex vertical gap={token.padding}>
    <AgentConfigurationPanel title="Current observation" label="Runtime state" bodyGap="section" extra={refresh}
      description="Inspection reads existing state. Visiting this page does not start a browser or Sandbox.">
      {error && <Alert type="warning" showIcon title={error} />}
      {!data && loading && <Spin aria-label="Loading runtime state" />}
      {data && <>
        <Flex wrap align="center" gap={token.paddingXS} aria-live="polite">
          <Tag color={stale || data.availability !== 'available' ? 'warning' : undefined}>{stale ? 'Stale' : availability(data.availability)}</Tag>
          <Typography.Text type="secondary">Observed <time dateTime={data.observedAtUtc}>{new Date(data.observedAtUtc).toLocaleString()}</time></Typography.Text>
        </Flex>
        <Descriptions column={{ xs: 1, sm: 2, lg: 3 }} size="small" items={[
          { key: 'hosted', label: 'Hosted Sessions', children: count(data.hostedSessions?.runtimes) },
          { key: 'attached', label: 'Attached connections', children: count(data.hostedSessions?.attached) },
          { key: 'open', label: 'Durable open Sessions', children: count(data.sessions?.open) },
          { key: 'paused', label: 'Durable paused Sessions', children: count(data.sessions?.paused) },
          { key: 'terminal', label: 'Terminal Sessions', children: count(data.sessions?.terminal) },
          { key: 'executing', label: 'Executing Runs', children: count(data.runs?.executing) },
          { key: 'approval', label: 'Runs awaiting approval', children: count(data.runs?.waitingApproval) },
          { key: 'signal', label: 'Suspended Runs', children: count(data.runs?.waitingSignal) },
          { key: 'queued', label: 'Queued Runs', children: count(data.runs?.queued) },
          { key: 'retry', label: 'Retrying Runs', children: count(data.runs?.retrying) },
          { key: 'completed', label: 'Terminal Runs', children: count(data.runs?.terminal) },
        ]} />
        <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Hosted Sessions are in memory; durable Sessions may remain open while detached. Executing counts claimed Runs, including claims awaiting recovery. Each Run appears in one status bucket. Archived and deleted Sessions are excluded.</Typography.Paragraph>
        <Flex wrap gap={token.paddingSM}><a href={instancePath(instanceId, 'sessions')} onClick={followAppLink}>Inspect Sessions</a><a href={instancePath(instanceId, 'runs')} onClick={followAppLink}>Inspect Runs</a></Flex>
      </>}
    </AgentConfigurationPanel>
    {data?.resources.map(resource => <AgentConfigurationPanel key={resource.kind} title={resource.kind === 'browser' ? 'Browser' : 'Sandbox'} label={`${resource.kind} state`} bodyGap="default">
      <Flex wrap gap={token.paddingXS}><Tag>{availability(resource.availability)}</Tag><Typography.Text>{sharing(resource.sharing)}</Typography.Text></Flex>
      {resource.code && <Typography.Text type="secondary">{issue(resource.code)}</Typography.Text>}
      <Descriptions column={{ xs: 1, sm: 2 }} size="small" items={resource.kind === 'browser' ? [
        { key: 'contexts', label: 'Live contexts', children: count(resource.liveContexts) }, { key: 'pages', label: 'Open pages', children: count(resource.openPages) },
      ] : [
        { key: 'generations', label: 'Ready or executing generations', children: count(resource.activeGenerations) },
        { key: 'commands', label: 'Commands with active receipts', children: count(resource.activeCommands) },
        { key: 'cleanup', label: 'Cleanup pending', children: count(resource.cleanupPending) },
      ]} />
      {resource.sharing === 'sharedCoordinated' && <Typography.Text type="secondary">Shared profile storage intentionally retains login state across Sessions. Closing a browser context does not log out of a site.</Typography.Text>}
    </AgentConfigurationPanel>)}
    {data && data.issues.filter(i => i.kind !== 'browser' && i.kind !== 'sandbox').map(i => <Alert key={`${i.kind}:${i.code}`} type="warning" showIcon title={`${i.kind}: ${issue(i.code)}`} />)}
  </Flex>;
}
