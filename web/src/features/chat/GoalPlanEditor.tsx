import { useEffect, useState } from 'react';
import { Alert, Button, Checkbox, Collapse, Flex, Form, Input, InputNumber, Select, Switch, Typography, theme } from 'antd';
import { MinusCircleOutlined, PlusOutlined } from '@ant-design/icons';
import type { GoalPlan, GoalVerification } from '../../services/tenantSessionGoals';
import type { EvaluationSuite } from '../../services/tenantEvaluations';
import { identityRequest, tenantApi } from '../../services/identity';

const suiteKey = (id: string, version: number, hash: string) => `${id}:${version}:${hash}`;
const checkpoint = () => ({ checkpointId: crypto.randomUUID(), title: '', expectedOutcome: '', required: true, dependencies: [], methods: [2], suite: undefined });
type PlanDraft = { title: string; objective: string; successCriteria: string; requireFinalAcceptance: boolean; deadline?: string; auto: boolean; maxRuns: number; until?: string; checkpoints: ReturnType<typeof checkpoint>[] };
export function GoalPlanEditor({ tenant, initial, busy, reloadRequired = false, onSave, onCancel, onDirty }: { tenant: string; initial?: GoalPlan; busy: boolean; reloadRequired?: boolean; onSave: (plan: GoalPlan) => void; onCancel: () => void; onDirty: () => void }) {
  const { token } = theme.useToken(); const [form] = Form.useForm<PlanDraft>(); const [suites, setSuites] = useState<EvaluationSuite[]>([]); const [suiteError, setSuiteError] = useState(false);
  const rows = Form.useWatch('checkpoints', { form, preserve: true }) ?? []; const auto = Form.useWatch('auto', form);
  useEffect(() => { const controller = new AbortController(); void identityRequest<{ items: EvaluationSuite[] }>(tenantApi(tenant, '/evaluation-suites?pageSize=100'), { signal: controller.signal }).then(page => setSuites(page.items.filter(s => !s.archived))).catch(() => { if (!controller.signal.aborted) setSuiteError(true); }); return () => controller.abort(); }, [tenant]);
  const initialValues = initial ? { ...initial, auto: initial.autoContinuation.enabled, maxRuns: initial.autoContinuation.maxRuns || 1,
    deadline: initial.deadlineAt?.slice(0, 16), until: initial.autoContinuation.until?.slice(0, 16), checkpoints: initial.checkpoints.map(c => ({ ...c, methods: c.verification.map(v => v.kind), suite: (() => { const v = c.verification.find(v => v.kind === 0); return v?.suiteId && v.suiteVersion && v.suiteHash ? suiteKey(v.suiteId, v.suiteVersion, v.suiteHash) : undefined; })() })) }
    : { title: '', objective: '', successCriteria: '', requireFinalAcceptance: false, auto: false, maxRuns: 1, checkpoints: [checkpoint()] };
  const suiteOptions = [...new Map([
    ...(initial?.checkpoints.flatMap(c => c.verification.filter(v => v.kind === 0 && v.suiteId && v.suiteVersion && v.suiteHash).map(v => ({ value: suiteKey(v.suiteId!, v.suiteVersion!, v.suiteHash!), label: `Accepted Suite ${v.suiteId} · v${v.suiteVersion} · ${v.suiteHash!.slice(0, 8)}` }))) ?? []),
    ...suites.map(s => ({ value: suiteKey(s.suiteId, s.version, s.contentHash), label: `${s.name} · v${s.version} · ${s.contentHash.slice(0, 8)}` }))
  ].map(o => [o.value, o])).values()];
  function finish(value: PlanDraft) {
    if (reloadRequired) return;
    value = form.getFieldsValue(true);
    const checkpoints = value.checkpoints.map(c => {
      const verification: GoalVerification[] = c.methods.map(kind => {
        if (kind !== 0) return { kind };
        const suite = suites.find(s => suiteKey(s.suiteId, s.version, s.contentHash) === c.suite);
        const pinned = initial?.checkpoints.find(p => p.checkpointId === c.checkpointId)?.verification.find(v => v.kind === 0 && v.suiteId && v.suiteVersion && v.suiteHash && suiteKey(v.suiteId, v.suiteVersion, v.suiteHash) === c.suite);
        return suite ? { kind, suiteId: suite.suiteId, suiteVersion: suite.version, suiteHash: suite.contentHash } : pinned ?? { kind };
      });
      return { checkpointId: c.checkpointId, title: c.title.trim(), expectedOutcome: c.expectedOutcome.trim(), required: c.required, dependencies: c.dependencies ?? [], verification };
    });
    onSave({ title: value.title.trim(), objective: value.objective.trim(), successCriteria: value.successCriteria.trim(), checkpoints,
      deadlineAt: value.deadline ? new Date(value.deadline + 'Z').toISOString() : null, requireFinalAcceptance: value.requireFinalAcceptance,
      autoContinuation: value.auto ? { enabled: true, maxRuns: value.maxRuns, until: value.until ? new Date(value.until + 'Z').toISOString() : null } : { enabled: false, maxRuns: 0, until: null } });
  }
  return <Form form={form} layout="vertical" initialValues={initialValues} disabled={busy} onValuesChange={onDirty} onFinish={finish}>
    {suiteError && <Alert type="warning" showIcon title="Evaluation Suite choices are unavailable" description="Existing exact Suite pins are preserved. Restore catalog access or refresh before selecting a new Evaluation policy." />}
    <Form.Item name="title" label="Goal title" rules={[{ required: true, whitespace: true, max: 200 }]}><Input maxLength={200} /></Form.Item>
    <Form.Item name="objective" label="Outcome to achieve" rules={[{ required: true, whitespace: true, max: 4000 }]}><Input.TextArea autoSize={{ minRows: 2, maxRows: 8 }} maxLength={4000} /></Form.Item>
    <Form.Item name="successCriteria" label="Overall success criteria" rules={[{ required: true, whitespace: true, max: 4000 }]}><Input.TextArea autoSize={{ minRows: 2, maxRows: 8 }} maxLength={4000} /></Form.Item>
    <Form.List name="checkpoints" rules={[{ validator: (_, values) => values?.length >= 1 && values.length <= 40 && values.some((v: { required: boolean }) => v.required) ? Promise.resolve() : Promise.reject(new Error('Use 1–40 checkpoints, including a required outcome.')) }]}>
      {(fields, { add, remove }, { errors }) => <Flex vertical gap={token.paddingSM}>
        <Typography.Text strong>Outcome checkpoints</Typography.Text>
        <Collapse items={fields.map((field, index) => ({ key: field.key, forceRender: true, label: `${index + 1}. ${rows[index]?.title || 'New checkpoint'}`, children: <>
          <Form.Item name={[field.name, 'checkpointId']} hidden><Input /></Form.Item>
          <Form.Item name={[field.name, 'title']} label={`Checkpoint ${index + 1} title`} rules={[{ required: true, whitespace: true, max: 200 }]}><Input maxLength={200} /></Form.Item>
          <Form.Item name={[field.name, 'expectedOutcome']} label="Expected outcome and criterion" rules={[{ required: true, whitespace: true, max: 4000 }]}><Input.TextArea rows={2} maxLength={4000} /></Form.Item>
          <Form.Item name={[field.name, 'required']} valuePropName="checked"><Checkbox>Required for completion</Checkbox></Form.Item>
          {index > 0 && <Form.Item name={[field.name, 'dependencies']} label="Requires these earlier checkpoints"><Select mode="multiple" options={rows.slice(0, index).map((row, i) => ({ value: row.checkpointId, label: row.title || `Checkpoint ${i + 1}` }))} /></Form.Item>}
          <Form.Item name={[field.name, 'methods']} label="How it will be verified (all selected methods must pass)" rules={[{ required: true, type: 'array', min: 1 }]}><Select mode="multiple" options={[{ value: 2, label: 'Human acceptance' }, { value: 0, label: 'Exact Evaluation pass' }, { value: 1, label: 'Artifact publication receipt only' }]} /></Form.Item>
          {rows[index]?.methods?.includes(0) && <Form.Item name={[field.name, 'suite']} label="Evaluation Suite (pins this version)" rules={[{ required: true }]}><Select options={suiteOptions} /></Form.Item>}
          {fields.length > 1 && <Button danger icon={<MinusCircleOutlined aria-hidden />} onClick={() => { remove(field.name); onDirty(); }}>Remove pending checkpoint</Button>}
        </> }))} defaultActiveKey={fields.length === 1 ? [fields[0].key] : []} />
        <Form.ErrorList errors={errors} />
        <Button icon={<PlusOutlined aria-hidden />} disabled={busy || fields.length >= 40} onClick={() => { add(checkpoint()); onDirty(); }}>Add checkpoint</Button>
      </Flex>}
    </Form.List>
    <Flex vertical gap={token.paddingSM} style={{ paddingBlockStart: token.padding }}>
      <Form.Item name="deadline" label="Optional goal deadline (UTC)"><Input type="datetime-local" /></Form.Item>
      <Form.Item name="requireFinalAcceptance" valuePropName="checked"><Checkbox>Require final human acceptance after all required checkpoints pass</Checkbox></Form.Item>
      <Form.Item name="auto" label="Automatically continue into verified next checkpoints" valuePropName="checked"><Switch /></Form.Item>
      {auto && <><Alert type="info" showIcon title="Explicit bounded permission" description="Core may admit one successor at a time after conclusive verification. User input, review, pause or uncertainty stops it. The total allowance never resets." />
        <Form.Item name="maxRuns" label="Maximum automatic successor Runs (1–5 total)" rules={[{ required: true }]}><InputNumber min={1} max={5} /></Form.Item>
        <Form.Item name="until" label="Automatic continuation deadline (UTC, within 24 hours)" rules={[{ required: true }]}><Input type="datetime-local" /></Form.Item></>}
      <Flex wrap gap={token.paddingXS}><Button htmlType="submit" type="primary" loading={busy} disabled={reloadRequired}>{initial ? 'Propose revised plan' : 'Create proposed goal'}</Button><Button onClick={onCancel}>Keep current plan</Button></Flex>
    </Flex>
  </Form>;
}
