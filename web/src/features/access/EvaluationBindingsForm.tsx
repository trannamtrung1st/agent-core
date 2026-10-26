import { Button, Flex, Form, InputNumber, Select, Typography } from 'antd';
import type { FormInstance } from 'antd';
import { hookLabels, type EvaluationBinding, type EvaluationPage, type EvaluationSuite } from '../../services/tenantEvaluations';
import { tenantApi } from '../../services/identity';
import { useIdentityQuery } from './useIdentityQuery';
export const policyLabels = ['Record evidence only', 'Require pass before generation', 'Require pass for verified outcome', 'Require pass before publication', 'Request human assessment'];
export function EvaluationBindingsForm({ tenant, form, defaults = false }: { tenant: string; form: FormInstance; defaults?: boolean }) {
  const suites = useIdentityQuery<EvaluationPage<EvaluationSuite>>(tenantApi(tenant, '/evaluation-suites?page=0&pageSize=100'));
  const bindings = Form.useWatch('bindings', form) as EvaluationBinding[] | undefined;
  return <Form.List name="bindings">{(fields, { add, remove }) => <Flex vertical gap="var(--ac-space-section)">
    {fields.map(({ key, name }) => <section key={key} aria-label={`Evaluation binding ${name + 1}`}>
      <Flex justify="space-between" gap="var(--ac-space-compact)"><Typography.Text strong>Binding {name + 1}</Typography.Text><Button danger onClick={() => remove(name)}>Remove binding {name + 1}</Button></Flex>
      <Form.Item name={[name, 'suiteId']} label="Evaluation Suite" rules={[{ required: true }]}><Select loading={suites.loading} options={suites.data?.items.filter(s => !s.archived).map(s => ({ value: s.suiteId, label: `${s.name} · v${s.version}` }))} onChange={id => form.setFieldValue(['bindings', name, 'version'], suites.data?.items.find(s => s.suiteId === id)?.version)} /></Form.Item>
      <Form.Item name={[name, 'version']} label="Exact immutable version" rules={[{ required: true, type: 'number', min: 1, max: 1000 }]}><InputNumber min={1} max={1000} precision={0} /></Form.Item>
      <Form.Item name={[name, 'hook']} label="Lifecycle boundary" rules={[{ required: true }]}><Select options={hookLabels.map((label, value) => ({ label, value })).filter(h => !defaults || h.value < 3)} onChange={() => form.setFieldValue(['bindings', name, 'policy'], 0)} /></Form.Item>
      <Form.Item name={[name, 'policy']} label="Outcome policy" rules={[{ required: true }]}><Select options={policyLabels.map((label, value) => ({ label, value })).filter(p => p.value === 0 || p.value === 1 && bindings?.[name]?.hook === 0 || p.value === 3 && bindings?.[name]?.hook === 4 || [2, 4].includes(p.value) && [1, 2, 3].includes(bindings?.[name]?.hook ?? -1))} /></Form.Item>
    </section>)}
    <Button disabled={fields.length >= 8} onClick={() => add({ suiteId: undefined, version: 1, hook: 1, policy: 2 })}>Add evaluation binding</Button>
  </Flex>}</Form.List>;
}
