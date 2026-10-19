import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Flex, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { IdentityError } from '../../services/identity';
import { resetTenantBrowserProfile, tenantBrowserProfileApi, type TenantBrowserProfile, type TenantBrowserResetResult } from '../../services/tenantBrowserProfile';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

type Notice = { type: 'success' | 'info' | 'warning' | 'error'; title: string; description: string };
function resultNotice(value: TenantBrowserResetResult, saved = false): Notice {
  if (value.status === 'reset') return { type: 'success', title: saved ? 'Last reset confirmed' : 'Profile reset confirmed', description: saved
    ? 'The last reset removed saved Browser state. Later Browser activity can create a new profile.' : 'Saved Browser state was removed. Reload Browser state to inspect the current profile.' };
  if (value.status === 'pending') return { type: 'info', title: 'Reset pending', description: 'The reset has no final recorded outcome. Reload Browser state to check saved facts. The request will not be repeated.' };
  return { type: 'warning', title: saved ? 'Last reset outcome unknown' : 'Reset outcome unknown', description: 'The Browser may have closed while saved state remains. Persistent Browser use is blocked until a new reset confirms removal. Reload Browser state before deciding on another reset.' };
}
export function TenantBrowserProfilePanel({ tenant, id, organization, onReset }: { tenant: string; id: string; organization: string; onReset?: () => void }) {
  const { modal } = App.useApp(); const command = useTenantCommand(); const [epoch, setEpoch] = useState(0); const [invalidated, setInvalidated] = useState(false); const [notice, setNotice] = useState<Notice>();
  const state = useIdentityQuery<TenantBrowserProfile>(tenantBrowserProfileApi(tenant, id), String(epoch)); const view = state.data;
  const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null); const reloadButton = useRef<HTMLButtonElement>(null); const resetButton = useRef<HTMLButtonElement>(null);
  useEffect(() => () => { confirmation.current?.destroy(); confirmation.current = null; }, []);
  const blocked = command.busy || invalidated || command.requiresReload;
  function reload() { if (command.busy) return; setNotice(undefined); setInvalidated(false); setEpoch(value => value + 1); }
  function reset() {
    if (!view || !view.canReset || blocked || confirmation.current) return;
    const observed = view; const operation = crypto.randomUUID();
    confirmation.current = confirmAction(modal, { title: 'Reset this Browser profile?', okText: 'Reset Browser profile', danger: true,
      content: <Flex vertical gap="var(--ac-space-default)" className="access-row-summary"><Typography.Text>Agent: {observed.agentName}</Typography.Text><Typography.Text>Organization: {organization}</Typography.Text>
        <Typography.Text>Close all persistent Browser views for this agent and remove its saved cookies, sign-ins and Browser state. The agent may need to sign in again.</Typography.Text>
        <Typography.Text>Ephemeral Browser sessions, other agents, credentials, bindings and files remain separate.</Typography.Text></Flex>,
      afterClose: () => { confirmation.current = null; requestAnimationFrame(() => { const target = resetButton.current; if (target?.isConnected && !target.disabled) target.focus(); else reloadButton.current?.focus(); }); },
      onOk: () => command.run(async signal => {
        setInvalidated(true); setNotice({ type: 'info', title: 'Reset request in progress', description: 'Waiting for the saved outcome. No other reset can start from this view.' });
        try { return { result: await resetTenantBrowserProfile(tenant, id, observed, operation, signal) }; }
        catch (error) { return { error }; }
      }, outcome => {
        setInvalidated(true);
        if (outcome.result) setNotice(resultNotice(outcome.result));
        else {
          const refused = outcome.error instanceof IdentityError && outcome.error.status < 500;
          setNotice({ type: refused ? 'error' : 'warning', title: refused ? 'Reset was refused' : 'Reset outcome not confirmed', description: refused
            ? 'Current access or Browser state changed. Reload Browser state before deciding on a new reset.'
            : 'The response could not confirm what happened. Reload saved Browser state before deciding on another reset. This request will not be repeated.' });
        }
        onReset?.();
      }) });
  }
  const savedNotice = view?.lastReset ? resultNotice(view.lastReset, true) : undefined;
  return <AgentConfigurationPanel title="Browser state" label="Browser state" bodyGap="default"
    description="Saved Browser state is shared by this agent’s persistent Browser views. Browser access is independent of credential bindings."
    extra={<Button ref={reloadButton} onClick={reload} disabled={command.busy}>Reload Browser state</Button>}>
    {notice && <Alert showIcon role="status" type={notice.type} title={notice.title} description={notice.description} />}
    {command.error !== undefined && <AccessError error={command.error} />}
    {!invalidated && (state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : view && <>
      <Flex wrap align="center" gap="var(--ac-space-compact)" className="access-row-summary"><Typography.Text strong>{view.agentName}</Typography.Text><Tag>{view.lifecycle}</Tag></Flex>
      <Flex vertical gap="var(--ac-space-tight)"><Typography.Text>{view.profilePresent ? 'Saved Browser profile present' : 'No saved Browser profile'}</Typography.Text><Typography.Text type="secondary">Open persistent views: {view.liveViews}</Typography.Text></Flex>
      {savedNotice && <Alert showIcon type={savedNotice.type} title={savedNotice.title} description={savedNotice.description} />}
      {view.busy ? <Typography.Paragraph type="secondary">The Browser is busy. Reload Browser state after the current work settles.</Typography.Paragraph>
        : view.lifecycle !== 'Active' ? <Typography.Paragraph type="secondary">Restore this instance before resetting its Browser profile.</Typography.Paragraph>
          : !view.canReset && view.lastReset?.status !== 'pending' && <Typography.Paragraph type="secondary">Reset requires Browser management access as well as Agent and Browser read access.</Typography.Paragraph>}
      <Flex wrap gap="var(--ac-space-compact)"><Button ref={resetButton} danger disabled={!view.canReset || blocked} onClick={reset}>Reset Browser profile</Button></Flex>
    </>)}
  </AgentConfigurationPanel>;
}
