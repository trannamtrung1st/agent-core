import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Flex, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { IdentityError, type TenantContext } from '../../services/identity';
import { changeAgentLifecycle, readAgentLifecycle, type AgentLifecycle, type LifecycleAction, type LifecycleResult } from '../../services/tenantAgentLifecycle';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessLoading, accessErrorMessage } from './AccessFeedback';

const blockers = { system_managed_instance: 'System-managed instance. Archive and decommission are unavailable; use Visible in Chat to change new-chat selection.', archive_required: 'Archive this instance before permanent retirement.', agent_in_use: 'Retained Sessions, learned memory, Automations or execution history still reference this instance. Archive keeps it inactive without breaking history.',
  browser_profile_busy: 'A Browser profile reset is still pending. Read its saved outcome in Connections before retiring this instance.', agent_decommissioned: 'This identity has been permanently retired.' };
const actionName = { archive: 'Archive instance', restore: 'Restore instance', decommission: 'Decommission instance' };
export function TenantAgentLifecycle({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const { modal, message } = App.useApp(); const detail = useAdminDetailLayout();
  const [view, setView] = useState<AgentLifecycle>(); const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(); const [blocked, setBlocked] = useState(false); const [result, setResult] = useState<LifecycleResult>();
  const active = useRef<AbortController | null>(null); const sending = useRef(false); const stale = useRef(false); const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  const reloadButton = useRef<HTMLButtonElement>(null); const actionButton = useRef<HTMLElement | null>(null);
  async function load() {
    if (sending.current) return; active.current?.abort(); const controller = new AbortController(); active.current = controller;
    setLoading(true); setError(undefined);
    try {
      const current = await readAgentLifecycle(tenant,id,controller.signal);
      if (!controller.signal.aborted) { setView(current); stale.current = false; setBlocked(false); setResult(previous => previous?.action === 'decommission' && previous.operationId === current.operationId ? previous : undefined); }
    } catch (failure) {
      if (!controller.signal.aborted) { setError(failure); setView(undefined); stale.current = true; setBlocked(true); }
    } finally { if (!controller.signal.aborted) setLoading(false); }
  }
  useEffect(() => { void load(); return () => { active.current?.abort(); confirmation.current?.destroy(); }; }, []); // eslint-disable-line react-hooks/exhaustive-deps
  async function run(action: LifecycleAction, observed: AgentLifecycle, operation: string) {
    if (sending.current || stale.current) return;
    const controller = new AbortController(); active.current = controller; sending.current = true; stale.current = true;
    setBusy(true); setBlocked(true); setError(undefined); setResult(undefined);
    try {
      const saved = await changeAgentLifecycle(tenant,id,observed,action,operation,controller.signal);
      if (!controller.signal.aborted) { setResult(saved); message.success(action === 'decommission' ? 'Instance decommissioned.' : action === 'archive' ? 'Instance archived.' : 'Instance restored.'); }
      sending.current = false;
      if (!controller.signal.aborted) await load();
    } catch (failure) {
      if (!controller.signal.aborted) { setError(failure); if (failure instanceof IdentityError && [401,403,404].includes(failure.status)) setView(undefined); message.error('The lifecycle action could not be confirmed. Read current lifecycle before another action.'); }
    } finally { sending.current = false; if (!active.current?.signal.aborted) setBusy(false); }
  }
  function decide(action: LifecycleAction, opener: HTMLElement) {
    if (!view || loading || sending.current || stale.current || confirmation.current || view.decommissioned || (action === 'decommission' ? !view.canDecommission : !view.canUpdate)) return;
    const observed = view; const operation = crypto.randomUUID(); actionButton.current = opener;
    confirmation.current = confirmAction(modal, { title: action === 'decommission' ? 'Permanently decommission this instance?' : actionName[action] + '?', okText: actionName[action], danger: action !== 'restore',
      content: <Flex vertical gap="var(--ac-space-default)" className="access-row-summary"><Typography.Text>Agent: {observed.agentName}</Typography.Text><Typography.Text>Organization: {observed.organizationName}</Typography.Text>
        <Typography.Text>{action === 'decommission' ? 'Permanently retire this identity, remove its credential bindings and local configuration, and clean up its saved Browser profile and home/workspace files. This identity cannot be restored or reused.' : action === 'archive' ? 'Keep this identity, history and saved state while making the instance inactive.' : 'Make this same instance active again. Its current permissions and policies still govern future work.'}</Typography.Text>
        {action === 'decommission' && <Typography.Text>Retained Session and execution references block retirement. Physical cleanup may remain pending after the identity is retired.</Typography.Text>}</Flex>,
      onOk: () => run(action,observed,operation), afterClose: () => { confirmation.current = null; requestAnimationFrame(() => { const target = actionButton.current; if (target?.isConnected && !target.hasAttribute('disabled')) target.focus(); else reloadButton.current?.focus(); }); } });
  }
  const retired = view?.decommissioned || result?.action === 'decommission'; const disabled = loading || busy || blocked;
  return <Flex vertical gap="var(--ac-space-section)">
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Button ref={reloadButton} disabled={busy || loading} onClick={() => void load()}>Reload lifecycle</Button></Flex>
    {error !== undefined && <Alert type="error" showIcon role="alert" title={accessErrorMessage(error)} />}
    {blocked && !loading && <Alert type="warning" showIcon title="Read current lifecycle before another action" description="Reload reads saved facts and current access. It does not repeat the previous command." />}
    {result && result.action !== 'decommission' && <Alert type="success" showIcon role="status" title={result.action === 'archive' ? 'Archive saved' : 'Restore saved'} description="The status change was saved. Reload lifecycle to inspect current facts." />}
    {result?.action === 'decommission' && <Alert type="success" showIcon role="status" title="Instance decommissioned" description={result.cleanupStatus === 'completed' ? 'Identity retirement and this cleanup attempt completed. The identity cannot be restored.' : 'Identity retirement is saved. Browser profile or workspace cleanup is pending and will be retried by the host.'} />}
    {loading ? <AccessLoading /> : view && <AgentConfigurationPanel title={view.agentName} label="Agent lifecycle" bodyGap="section">
      <Descriptions {...detail} bordered size="small" column={1}><Descriptions.Item label="Organization">{view.organizationName}</Descriptions.Item><Descriptions.Item label="Status"><Tag>{retired ? 'Decommissioned' : view.lifecycle}</Tag></Descriptions.Item></Descriptions>
      {retired ? <Alert type="info" showIcon title="Permanently retired identity" description={result?.action === 'decommission' && result.operationId === view.operationId ? 'Live inventories and configuration no longer expose this instance. Retained ownership fences its identity.' : 'Saved facts confirm retirement. This read does not confirm the current physical cleanup outcome; the host retries any pending cleanup.'} /> : <>
        {view.blocker === 'system_managed_instance' && <Alert type="info" showIcon title="System-managed instance" description={blockers.system_managed_instance} />}
        <Typography.Paragraph type="secondary">Archive keeps this instance’s identity, history and saved state. Restore makes the same instance active again.</Typography.Paragraph>
        {view.canUpdate ? <Flex wrap gap="var(--ac-space-compact)"><Button disabled={disabled} onClick={event => decide(view.lifecycle === 'Active' ? 'archive' : 'restore',event.currentTarget)}>{view.lifecycle === 'Active' ? 'Archive instance' : 'Restore instance'}</Button></Flex> : view.blocker !== 'system_managed_instance' && <Typography.Text type="secondary">Changing active status requires update access on this instance.</Typography.Text>}
        {view.lifecycle === 'Archived' && <Flex vertical gap="var(--ac-space-default)"><Typography.Title level={5}>Permanent retirement</Typography.Title><Typography.Paragraph type="secondary">Decommission permanently retires this identity and schedules cleanup of its saved Browser state, home/workspace files, bindings and local configuration.</Typography.Paragraph>
          {view.blocker && <Alert type="info" showIcon title="Retirement is blocked" description={blockers[view.blocker]} />}
          {!view.canDecommission && !view.blocker && <Typography.Text type="secondary">Permanent retirement requires delete access as well as read access on this instance.</Typography.Text>}
          <Flex wrap gap="var(--ac-space-compact)"><Button danger disabled={disabled || !view.canDecommission} onClick={event => decide('decommission',event.currentTarget)}>Decommission instance</Button></Flex></Flex>}
      </>}
    </AgentConfigurationPanel>}
  </Flex>;
}
