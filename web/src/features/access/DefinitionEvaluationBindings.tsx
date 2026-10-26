import { useState } from 'react';
import { App, Button, Flex, Form, Typography } from 'antd';
import type { EvaluationBinding } from '../../services/tenantEvaluations';
import { EvaluationBindingsForm } from './EvaluationBindingsForm';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
export function DefinitionEvaluationBindings({ tenant, value, writable, onChange }: { tenant: string; value: EvaluationBinding[]; writable: boolean; onChange: (value: EvaluationBinding[]) => void }) {
  const [form] = Form.useForm(); const [dirty, setDirty] = useState(false); const { message } = App.useApp(); const guard = useAccessDraftGuard(dirty, false, () => setDirty(false));
  return <section aria-label="Definition evaluation bindings"><Flex vertical gap="var(--ac-space-section)">{guard.holder}
    <Typography.Title level={3}>Lifecycle and reusable Suite bindings</Typography.Title>
    <Typography.Paragraph type="secondary">Exact versions apply to future Runs. Required Before Run checks can block generation. After Run checks leave factual completion intact. Configured model judges can incur charges automatically. Before Run evaluation uses at most 10 seconds and one quarter of the admitted Run duration. Executors need their own current evaluation, model and source permissions. Instance settings inherit these bindings.</Typography.Paragraph>
    <Form form={form} layout="vertical" initialValues={{ bindings: value.filter(b => b.hook !== 4 || b.suiteId !== '') }} disabled={!writable} onValuesChange={() => setDirty(true)} onFinish={fields => { onChange(fields.bindings ?? []); guard.release(); setDirty(false); }} onFinishFailed={failure => showAdminFormValidation(message, form, failure)}>
      <EvaluationBindingsForm tenant={tenant} form={form} />
      {writable && <Button htmlType="submit" disabled={!dirty}>Apply bindings to draft edits</Button>}
    </Form>
    {dirty && <Typography.Text type="secondary">Apply these bindings, then Save draft to persist them.</Typography.Text>}
  </Flex></section>;
}
