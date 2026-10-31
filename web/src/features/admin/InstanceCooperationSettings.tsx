import { useEffect, useState } from 'react';
import { Alert, Button, Checkbox, Flex, Form, InputNumber, Typography, theme } from 'antd';
import { identityRequest } from '../../services/identity';
import { tenantAgentApi, type TenantAgentEditor } from '../../services/tenantAgentConfiguration';
import type { AgentCooperation } from '../../services/tenantTasks';
import { useTenantCommand } from '../access/useTenantCommand';

import { AgentConfigurationPanel, AgentSettingRow } from './AgentConfigurationLayout';

const off: AgentCooperation = { acceptTasks: false, acceptDirectMessages: false, allowDirectMessageWake: false, maxConcurrentTasks: 2 };
export function InstanceCooperationSettings({ tenant, instance, view, disabled, onSaved, onDirtyChange, onBusyChange }: {
  tenant: string; instance: string; view: TenantAgentEditor; disabled: boolean; onSaved: () => void;
  onDirtyChange: (dirty: boolean) => void; onBusyChange: (busy: boolean) => void;
}) {
  const { token } = theme.useToken(); const [form] = Form.useForm<AgentCooperation>(); const command = useTenantCommand();
  const [dirty, setDirty] = useState(false); const acceptMessages = Form.useWatch('acceptDirectMessages', form);
  const canUpdate = view.canUpdate && view.agent.instance.lifecycle === 'Active';
  useEffect(() => { if (!dirty) form.setFieldsValue(view.agent.instance.cooperation ?? off); }, [view, dirty, form]);
  useEffect(() => { onDirtyChange(dirty); return () => onDirtyChange(false); }, [dirty, onDirtyChange]);
  useEffect(() => { onBusyChange(command.busy); return () => onBusyChange(false); }, [command.busy, onBusyChange]);
  function save(values: AgentCooperation) {
    if (!canUpdate || disabled || command.busy) return;
    void command.run(signal => identityRequest(tenantAgentApi(tenant, instance, '/cooperation'), { method: 'PUT', signal,
      body: { cooperation: values, expectedRevision: view.securityRevision, expectedInstanceRevision: view.agent.instance.revision } }), () => { setDirty(false); onSaved(); });
  }
  return <AgentConfigurationPanel title="Cooperation" label="Cooperation" description="Acceptance is off by default. The worker keeps its own permissions, Space and model. Direct-message wake can spend its own bounded execution budget.">
    {command.error ? <Alert type="error" showIcon title={command.error instanceof Error ? command.error.message : 'Cooperation could not be saved. Reload settings before trying again.'} /> : null}
    <Form form={form} layout="vertical" className="agent-settings-rows" disabled={!canUpdate || disabled || command.busy} onValuesChange={changed => { if (changed.acceptDirectMessages === false) form.setFieldValue('allowDirectMessageWake', false); setDirty(true); }} onFinish={save}>
      <AgentSettingRow label={<label htmlFor="cooperation-tasks">Accept delegated tasks</label>} description="Allow other agents to request accountable work."><Form.Item name="acceptTasks" valuePropName="checked"><Checkbox id="cooperation-tasks" aria-label="Accept delegated tasks" /></Form.Item></AgentSettingRow>
      <AgentSettingRow label={<label htmlFor="cooperation-messages">Accept direct Agent messages</label>} description="Receive messages under this agent’s own authority."><Form.Item name="acceptDirectMessages" valuePropName="checked"><Checkbox id="cooperation-messages" aria-label="Accept direct Agent messages" /></Form.Item></AgentSettingRow>
      <AgentSettingRow label={<label htmlFor="cooperation-wake">Allow direct-message wake</label>} description={acceptMessages ? 'A received message may start bounded execution.' : 'Enable direct Agent messages first.'}><Form.Item name="allowDirectMessageWake" valuePropName="checked"><Checkbox id="cooperation-wake" aria-label="Allow direct-message wake" disabled={!acceptMessages || !canUpdate || disabled || command.busy} /></Form.Item></AgentSettingRow>
      <AgentSettingRow label={<label htmlFor="cooperation-concurrency">Maximum concurrent tasks</label>} description="From 1 to 8 simultaneous tasks."><Form.Item name="maxConcurrentTasks" rules={[{ required: true, type: 'number', min: 1, max: 8 }]}><InputNumber id="cooperation-concurrency" aria-label="Maximum concurrent tasks" min={1} max={8} /></Form.Item></AgentSettingRow>
      <Flex className="agent-settings-actions" wrap gap={token.paddingXS}>
      <Button type="primary" htmlType="submit" loading={command.busy} disabled={!dirty || !canUpdate || disabled || command.requiresReload}>Save cooperation</Button>{dirty && <Typography.Text type="secondary">Unsaved changes</Typography.Text>}</Flex>
    </Form>
  </AgentConfigurationPanel>;
}
