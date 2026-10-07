import { useAdminDetailLayout } from './useAdminDetailLayout';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { Button, Descriptions, Drawer, Flex, Form, Grid, Input, Select, Switch, Typography, theme } from 'antd';

export type SkillDrawerValue = {
  id?: string; name: string; description: string; procedure: string;
  projection: 'Always' | 'OnDemand' | undefined; enabled: boolean | undefined;
  capabilities: string; resourcePaths?: string;
};

/** Shared skill authoring and inspection; callers retain ownership and persistence. */
export function SkillDrawer({ open, title: incomingTitle, value: incomingValue, definition: incomingDefinition = false, readOnly: incomingReadOnly = false, busy = false,
  context: incomingContext, notice: incomingNotice, contentLabel: incomingContentLabel, onSave, onClose, afterOpenChange, focusTriggerAfterClose = true,
  validateId, idReadOnly: incomingIdReadOnly = false }: {
  open: boolean; title: string; value: SkillDrawerValue | null; definition?: boolean;
  readOnly?: boolean; busy?: boolean; context: ReactNode; notice?: ReactNode; contentLabel: string;
  onSave?: (value: SkillDrawerValue) => void | Promise<void>; onClose: () => void;
  afterOpenChange?: (visible: boolean) => void; focusTriggerAfterClose?: boolean;
  validateId?: (id: string) => string | null; idReadOnly?: boolean;
}) {
  // Callers can clear their selection immediately; retain the last open presentation
  // so the closing animation never switches mode, title or content.
  const [lastOpenPresentation, setLastOpenPresentation] = useState({
    title: incomingTitle, value: incomingValue, definition: incomingDefinition,
    readOnly: incomingReadOnly, idReadOnly: incomingIdReadOnly, context: incomingContext, notice: incomingNotice, contentLabel: incomingContentLabel
  });
  useEffect(() => {
    if (open) setLastOpenPresentation({
      title: incomingTitle, value: incomingValue, definition: incomingDefinition,
      readOnly: incomingReadOnly, idReadOnly: incomingIdReadOnly, context: incomingContext, notice: incomingNotice, contentLabel: incomingContentLabel
    });
  }, [open, incomingTitle, incomingValue, incomingDefinition, incomingReadOnly, incomingIdReadOnly, incomingContext, incomingNotice, incomingContentLabel]);
  const { title, value, definition, readOnly, idReadOnly, context, notice, contentLabel } = open ? {
    title: incomingTitle, value: incomingValue, definition: incomingDefinition,
    readOnly: incomingReadOnly, idReadOnly: incomingIdReadOnly, context: incomingContext, notice: incomingNotice, contentLabel: incomingContentLabel
  } : lastOpenPresentation;
  const { token } = theme.useToken(); const compact = !Grid.useBreakpoint().md;
  const detailLayout = useAdminDetailLayout();
  const closeButton = useRef<HTMLButtonElement>(null);
  const [form] = Form.useForm<SkillDrawerValue>(); const formId = useId(); const titleId = useId();
  useEffect(() => { if (value && !readOnly) { form.resetFields(); form.setFieldsValue(value); } }, [value, form, readOnly]);
  const textStyle = { margin: 0, whiteSpace: 'pre-wrap' as const, overflowWrap: 'anywhere' as const };
  return <Drawer open={open} title={<Typography.Text strong id={titleId}>{title}</Typography.Text>}
    aria-labelledby={titleId} size={compact ? '100%' : 640} onClose={onClose}
    closable={!busy} maskClosable={!busy} keyboard={!busy} afterOpenChange={visible => { if (visible && readOnly) closeButton.current?.focus(); afterOpenChange?.(visible); }}
    focusable={{ trap: open, focusTriggerAfterClose }}
    styles={{ wrapper: { maxWidth: '100vw' }, body: { padding: token.padding }, footer: { padding: token.padding } }}
    footer={<Flex justify="flex-end" gap={token.paddingXS}>
      <Button ref={closeButton} disabled={busy} onClick={onClose}>{readOnly ? 'Close inspection' : 'Cancel editing'}</Button>
      {!readOnly && <Button type="primary" aria-label="Save Skill" htmlType="submit" form={formId} loading={busy} disabled={busy} aria-busy={busy}>Save Skill</Button>}
    </Flex>}>
    <section aria-label={contentLabel}><Flex vertical gap={token.padding}>
      <Typography.Paragraph type="secondary" style={{ margin: 0 }}>{context}</Typography.Paragraph>
      {notice}
      {readOnly && value ? <>
        <Descriptions {...detailLayout} style={{ overflowWrap: 'anywhere' }} column={1} size="small" items={[
          { key: 'id', label: 'Skill ID', children: value.id || (definition ? 'Not set' : 'Assigned on save') },
          { key: 'name', label: 'Skill name', children: value.name },
          { key: 'description', label: 'Description', children: <Typography.Paragraph style={textStyle}>{value.description}</Typography.Paragraph> },
          { key: 'activation', label: 'Activation', children: value.projection === 'Always' ? 'Always' : value.projection === 'OnDemand' ? 'On demand' : 'Not set' },
          { key: 'enabled', label: definition ? 'Default enabled' : 'Enabled', children: value.enabled === undefined ? 'Not set' : value.enabled ? 'Yes' : 'No' },
          { key: 'capabilities', label: 'Required capabilities', children: <Typography.Text style={textStyle}>{value.capabilities || 'No capabilities'}</Typography.Text> },
          ...(definition ? [{ key: 'resources', label: 'Resource paths', children: <Typography.Text style={textStyle}>{value.resourcePaths || 'No resources'}</Typography.Text> }] : [])
        ]} />
        <Flex vertical gap={token.paddingXS}><Typography.Title level={5} style={{ margin: 0 }}>Procedure</Typography.Title>
          <Typography.Paragraph style={textStyle}>{value.procedure}</Typography.Paragraph></Flex>
      </> : <Form id={formId} name={formId} form={form} layout="vertical" disabled={busy} onFinish={onSave}>
        <Form.Item name="id" label="Skill ID" extra={idReadOnly ? 'The ID stays fixed after creation.' : `Optional. Empty generates from the Skill name. Unique within this ${definition ? 'Definition' : 'Agent Instance'}.`}
          rules={idReadOnly ? [] : [{ validator: (_, value: string) => {
            const id = (value ?? '').trim();
            const error = id && !/^[a-z][a-z0-9._-]{0,63}$/.test(id) ? 'Start with a lowercase letter; use lowercase letters, digits, dots, underscores or hyphens, up to 64 characters.' : validateId?.(id);
            return error ? Promise.reject(new Error(error)) : Promise.resolve();
          } }]}><Input maxLength={64} readOnly={idReadOnly} placeholder={idReadOnly ? undefined : 'e.g. refund.handle'} /></Form.Item>
        <Form.Item name="name" label="Skill name" rules={[{ required: true, whitespace: true, message: 'Please enter Skill name' }, { max: 80 }]}><Input maxLength={80} /></Form.Item>
        <Form.Item name="description" label="Description" rules={[{ required: true, whitespace: true, message: 'Please enter Description' }, { max: 240 }]}><Input.TextArea maxLength={240} showCount rows={2} /></Form.Item>
        <Form.Item name="procedure" label="Procedure" rules={[{ required: true, whitespace: true, message: 'Please enter Procedure' }, { max: 4000 }]}><Input.TextArea maxLength={4000} showCount rows={8} /></Form.Item>
        <Form.Item name="projection" label="Activation" extra="Always starts active. On demand is loaded when needed." rules={[{ required: true }]}><Select options={[{ value: 'Always', label: 'Always' }, { value: 'OnDemand', label: 'On demand' }]} /></Form.Item>
        <Form.Item name="capabilities" label="Required capabilities" extra="Comma-separated authorized capability names. Requirements never grant authority."><Input /></Form.Item>
        {definition && <Form.Item name="resourcePaths" label="Resource paths" extra="Comma-separated relative paths."><Input /></Form.Item>}
        <Form.Item layout="horizontal" colon={false} labelCol={{ flex: 'none' }} wrapperCol={{ flex: 'none' }} style={{ marginBottom: 0 }} name="enabled" label={definition ? 'Default enabled' : 'Enabled'} valuePropName="checked" rules={[{ required: true, message: 'Choose an explicit enabled state.' }]}><Switch /></Form.Item>
      </Form>}
    </Flex></section>
  </Drawer>;
}
