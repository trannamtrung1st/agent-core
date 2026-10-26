import { useState } from 'react';
import { Alert, App, Button, Flex, Form, Typography } from 'antd';
import { freshTenantRevision, type EvaluationBinding } from '../../services/tenantEvaluations';
import { identityRequest, IdentityCompletionError, tenantApi, type TenantContext } from '../../services/identity';
import { EvaluationBindingsForm } from './EvaluationBindingsForm';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';
import { useAccessDraftGuard } from './useAccessDraftGuard';
type Policies = { tenantId: string; revision: number; defaults: EvaluationBinding[] };
export function EvaluationGovernanceSettings({ context }: { context: TenantContext }) {
  const tenant = context.tenant.tenantId; const url = tenantApi(tenant, '/governance/evaluation-policies'); const saved = useIdentityQuery<Policies>(url);
  return <section aria-label="Evaluation outcome policies"><Flex vertical gap="var(--ac-space-section)">
    <Typography.Title level={3}>Evaluation outcome policies</Typography.Title>
    <Alert type="info" showIcon title="Evaluation is separate from operation Review" description="Organization defaults constrain future Run admissions. Configured model judges can incur bounded provider charges automatically. Passing checks grants no tool, resource or approval authority. Published Definition checks remain bound to their exact publication Suite." />
    {saved.loading ? <AccessLoading /> : saved.error ? <AccessError error={saved.error} retry={saved.reload} /> : saved.data && <PolicyForm key={saved.data.revision} tenant={tenant} saved={saved.data} writable={context.permissions.includes('tenant.governance.manage')} onSaved={saved.reload} />}
  </Flex></section>;
}
function PolicyForm({ tenant, saved, writable, onSaved }: { tenant: string; saved: Policies; writable: boolean; onSaved: () => void }) {
  const [form] = Form.useForm(); const [dirty, setDirty] = useState(false); const command = useTenantCommand(); const { message } = App.useApp(); const guard = useAccessDraftGuard(dirty, command.busy, () => {});
  return <>{guard.holder}{command.error !== undefined && <AccessError error={command.error} />}<Form form={form} layout="vertical" initialValues={{ bindings: saved.defaults }} disabled={!writable || command.busy || command.requiresReload} onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)} onFinish={value => void command.run(async signal => identityRequest<Policies>(tenantApi(tenant, '/governance/evaluation-policies'), { method: 'PUT', signal, body: { defaults: value.bindings ?? [], expectedPolicyRevision: saved.revision, expectedRevision: await freshTenantRevision(tenant, signal) } }), result => {
    if (result.tenantId !== tenant || result.revision !== saved.revision + 1) throw new IdentityCompletionError(); setDirty(false); onSaved(); void message.success('Evaluation policy defaults stored for future Runs.');
  })}>
    <EvaluationBindingsForm tenant={tenant} form={form} defaults />
    {writable && <Button type="primary" htmlType="submit" loading={command.busy} disabled={!dirty}>Save evaluation policies</Button>}
  </Form></>;
}
