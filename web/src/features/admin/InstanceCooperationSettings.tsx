import { useEffect, useState } from 'react';
import { Alert, Button, Checkbox, Flex, Form, InputNumber, Typography, theme } from 'antd';
import { identityRequest } from '../../services/identity';
import { tenantAgentApi, type TenantAgentEditor } from '../../services/tenantAgentConfiguration';
import type { AgentCooperation } from '../../services/tenantTasks';
import { useTenantCommand } from '../access/useTenantCommand';

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
  return <Flex vertical gap={token.paddingXS}>
    <Typography.Title level={5} style={{ margin: 0 }}>Cooperation</Typography.Title>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Acceptance is off by default. The worker keeps its own permissions, Space and model. Direct-message wake can spend its own bounded execution budget.</Typography.Paragraph>
    {command.error ? <Alert type="error" showIcon title={command.error instanceof Error ? command.error.message : 'Cooperation could not be saved. Reload settings before trying again.'} /> : null}
    <Form form={form} layout="vertical" disabled={!canUpdate || disabled || command.busy} onValuesChange={changed => { if (changed.acceptDirectMessages === false) form.setFieldValue('allowDirectMessageWake', false); setDirty(true); }} onFinish={save}>
      <Form.Item name="acceptTasks" valuePropName="checked"><Checkbox>Accept delegated tasks</Checkbox></Form.Item>
      <Form.Item name="acceptDirectMessages" valuePropName="checked"><Checkbox>Accept direct Agent messages</Checkbox></Form.Item>
      <Form.Item name="allowDirectMessageWake" valuePropName="checked"><Checkbox disabled={!acceptMessages || !canUpdate || disabled || command.busy}>Allow direct-message wake</Checkbox></Form.Item>
      <Form.Item name="maxConcurrentTasks" label="Maximum concurrent tasks" rules={[{ required: true, type: 'number', min: 1, max: 8 }]}><InputNumber min={1} max={8} /></Form.Item>
      <Button type="primary" htmlType="submit" loading={command.busy} disabled={!dirty || !canUpdate || disabled || command.requiresReload}>Save cooperation</Button>
    </Form>
  </Flex>;
}
