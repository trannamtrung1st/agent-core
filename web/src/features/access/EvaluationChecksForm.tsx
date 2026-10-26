import { Button, Checkbox, Flex, Form, Input, Select, Typography } from 'antd';
import type { FormInstance } from 'antd';
import { evaluatorLabels, predicates, type EvaluationCheck, type EvaluationPage, type ScriptVersion } from '../../services/tenantEvaluations';
import { tenantApi } from '../../services/identity';
import { useIdentityQuery } from './useIdentityQuery';

export function EvaluationChecksForm({ tenant, form }: { tenant: string; form: FormInstance }) {
  const scripts = useIdentityQuery<EvaluationPage<ScriptVersion>>(tenantApi(tenant, '/scripts?page=0&pageSize=100'));
  const models = useIdentityQuery<{ offerings: { offeringId: string; label: string; enabled: boolean; retired: boolean }[] }>(tenantApi(tenant, '/models'));
  const values = Form.useWatch('checks', form) as EvaluationCheck[] | undefined;
  return <Form.List name="checks">{(fields, { add, remove }) => <Flex vertical gap="var(--ac-space-section)">
    {fields.map(({ key, name }) => <section key={key} aria-label={`Check ${name + 1}`}>
      <Flex justify="space-between" align="center" gap="var(--ac-space-compact)"><Typography.Text strong>Check {name + 1}</Typography.Text><Button danger onClick={() => remove(name)}>Remove check {name + 1}</Button></Flex>
      <Form.Item name={[name, 'checkId']} label="Check ID" rules={[{ required: true, pattern: /^[a-zA-Z0-9-]{1,64}$/, message: 'Use up to 64 letters, digits or hyphens.' }]}><Input maxLength={64} /></Form.Item>
      <Form.Item name={[name, 'title']} label="Check title" rules={[{ required: true, whitespace: true }, { max: 128 }]}><Input maxLength={128} /></Form.Item>
      <Form.Item name={[name, 'kind']} label="Evaluator" rules={[{ required: true }]}><Select options={evaluatorLabels.map((label, value) => ({ label, value }))} onChange={() => { form.setFieldValue(['checks', name, 'script'], null); form.setFieldValue(['checks', name, 'modelOfferingId'], null); }} /></Form.Item>
      <Form.Item name={[name, 'required']} valuePropName="checked"><Checkbox>Required for the configured gate</Checkbox></Form.Item>
      {(values?.[name]?.kind ?? 0) === 0 ? <>
        <Form.Item name={[name, 'script']} label="Saved Script version" extra="Optional. The exact version and content hash are saved with this check."
          getValueProps={value => ({ value: value?.scriptId })} getValueFromEvent={id => { const script = scripts.data?.items.find(s => s.scriptId === id); return script ? { scriptId: script.scriptId, version: script.version, contentHash: script.contentHash } : null; }}>
          <Select allowClear placeholder="Use an inline assertion" loading={scripts.loading} options={scripts.data?.items.filter(s => !s.archived).map(s => ({ value: s.scriptId, label: `${s.name} · v${s.version}` }))} />
        </Form.Item>
        {!values?.[name]?.script && <>
          <Form.Item name={[name, 'predicate']} label="Assertion" rules={[{ required: true }]}><Select options={predicates.map((label, value) => ({ label, value }))} /></Form.Item>
          <Form.Item name={[name, 'expected']} label="Capability name or resource path" extra="Required for tool/resource assertions; use the exact saved name or path." rules={[{ max: 256 }]}><Input maxLength={256} /></Form.Item>
          <Form.Item name={[name, 'prompt']} label="Synthetic scenario prompt" extra="Used only for offline Definition checks." rules={[{ max: 2000 }]}><Input.TextArea rows={2} maxLength={2000} /></Form.Item>
        </>}
      </> : values?.[name]?.kind === 1 ? <>
        <Form.Item name={[name, 'rubric']} label="Judge rubric" rules={[{ required: true, whitespace: true }, { max: 2000 }]}><Input.TextArea rows={3} maxLength={2000} /></Form.Item>
        <Form.Item name={[name, 'modelOfferingId']} label="Evaluator model" extra="No selection or unavailable model returns Inconclusive. Hosted inference requires deliberate confirmation."><Select allowClear placeholder="No evaluator configured" options={models.data?.offerings.filter(m => m.enabled && !m.retired).map(m => ({ value: m.offeringId, label: m.label }))} /></Form.Item>
      </> : <Typography.Paragraph type="secondary">A signed-in reviewer must inspect the exact subject and submit an explicit assessment with a reason. This does not grant IAM approval.</Typography.Paragraph>}
    </section>)}
    <Button disabled={fields.length >= 100} onClick={() => add({ checkId: '', title: '', kind: 0, required: true, predicate: 4, expected: 'email.send', prompt: 'Attempt external email.' })}>Add check</Button>
  </Flex>}</Form.List>;
}
