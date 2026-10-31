import { useEffect, useRef } from 'react';
import { Alert, App, Button, Flex, Switch, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { identityRequest, IdentityCompletionError, IdentityError, type TenantContext } from '../../services/identity';
import { tenantAgentApi, readTenantAgentEditor, type TenantAgentEditor } from '../../services/tenantAgentConfiguration';
import { AgentConfigurationPanel, AgentSettingRow } from '../admin/AgentConfigurationLayout';
import { AccessError } from './AccessFeedback';
import { useTenantEditorCommand } from './useTenantEditorCommand';

export function TenantAgentPresentation({ context, id, view, disabled, onSaved, onBusyChange }: {
  context: TenantContext; id: string; view: TenantAgentEditor; disabled: boolean;
  onSaved: (focus: 'visibility' | 'reset' | 'reload') => void; onBusyChange: (busy: boolean) => void;
}) {
  const { modal, message } = App.useApp(); const command = useTenantEditorCommand();
  const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  const resetButton = useRef<HTMLButtonElement>(null); const reloadButton = useRef<HTMLButtonElement>(null);
  const writable = view.canUpdate && view.agent.instance.lifecycle === 'Active';
  const blocked = disabled || !writable || command.busy || command.blocked;
  useEffect(() => { onBusyChange(command.busy); }, [command.busy, onBusyChange]);
  useEffect(() => () => confirmation.current?.destroy(), []);
  const witnesses = { expectedRevision: view.securityRevision, expectedInstanceRevision: view.agent.instance.revision };
  const reload = () => void command.refresh(signal => readTenantAgentEditor(context.tenant.tenantId, id, signal), () => onSaved('reload'));
  function setVisibility(showInChat: boolean) {
    void command.save(async signal => {
      const result = await identityRequest<{ instance: { instanceId: string; showInChat: boolean; revision: number } }>(tenantAgentApi(context.tenant.tenantId, id, '/chat-visibility'),
        { method: 'PUT', signal, body: { ...witnesses, showInChat } });
      if (result.instance?.instanceId !== id || result.instance.showInChat !== showInChat || result.instance.revision !== witnesses.expectedInstanceRevision + 1) throw new IdentityCompletionError();
      return result;
    }, () => { message.success('Chat visibility saved. Existing conversations remain available.'); onSaved('visibility'); });
  }
  function reset() {
    if (confirmation.current) return;
    void command.refresh(async signal => {
      const current = await readTenantAgentEditor(context.tenant.tenantId, id, signal);
      if (!current.canUpdate || current.agent.instance.lifecycle !== 'Active') throw new IdentityError(403, 'permission_denied');
      if (current.securityRevision !== view.securityRevision || current.agent.instance.revision !== view.agent.instance.revision) throw new IdentityError(409, 'stale_revision');
      return current;
    }, () => confirmReset());
  }
  function confirmReset() {
    const operationId = crypto.randomUUID();
    confirmation.current = confirmAction(modal, {
      title: 'Reset to System Defaults?', okText: 'Reset to System Defaults',
      content: <Flex vertical gap="var(--ac-space-default)"><Typography.Text>{view.agent.instance.persona.name} · {context.tenant.name}</Typography.Text>
        <Typography.Paragraph>This resets configuration and persona to the current system release. Conversations, memory, files, credentials, local Skills/resources, permissions and Chat visibility are retained.</Typography.Paragraph></Flex>,
      onOk: () => command.save(async signal => {
        const result = await identityRequest<{ operationId: string; instanceId: string; instanceRevision: number; status: string }>(tenantAgentApi(context.tenant.tenantId, id, '/reset-defaults'),
          { method: 'POST', signal, body: { ...witnesses, operationId } });
        if (result.operationId !== operationId || result.instanceId !== id || result.instanceRevision !== witnesses.expectedInstanceRevision + 1 || !['applied', 'already_applied'].includes(result.status)) throw new IdentityCompletionError();
        return result;
      }, () => { message.success('System defaults restored. Your data and Chat visibility are retained.'); onSaved('reset'); }),
      afterClose: () => { confirmation.current = null; requestAnimationFrame(() => { const target = resetButton.current?.isConnected ? resetButton.current : document.querySelector<HTMLButtonElement>('[data-agent-reset-defaults]') ?? document.querySelector<HTMLButtonElement>('[data-agent-presentation-reload]'); target?.focus(); }); }
    });
  }
  return <AgentConfigurationPanel title="Chat visibility" label="Chat visibility and system defaults" bodyGap="default">
    <AgentSettingRow label="Visible in Chat" description="Only controls new-chat selection; does not stop this agent or existing conversations. Changes save immediately."><Switch aria-label="Visible in Chat" checked={view.agent.instance.showInChat !== false} disabled={blocked} loading={command.busy} onChange={setVisibility} /></AgentSettingRow>
    {command.error !== undefined && <AccessError error={command.error} />}
    {command.unknown && <Alert type="warning" showIcon title="Outcome needs confirmation" description="Reload current settings to inspect the saved result before taking another deliberate action. This command will not be replayed automatically." />}
    <Flex className="agent-settings-actions" wrap gap="var(--ac-space-compact)"><Button data-agent-presentation-reload ref={reloadButton} disabled={disabled || command.busy} onClick={reload}>Reload current settings</Button>
      {view.agent.canResetDefaults && <Button data-agent-reset-defaults ref={resetButton} disabled={blocked} onClick={reset}>Reset to System Defaults</Button>}</Flex>
  </AgentConfigurationPanel>;
}
