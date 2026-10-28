import { Button, Checkbox, Collapse, Flex, Form, Input, InputNumber, Select, Typography } from 'antd';
import type { WorkflowGraph, WorkflowInput } from '../../services/tenantWorkflows';
import { GoalBinding, type WorkflowAgent } from './WorkflowNodeProperties';

export function WorkflowGraphSettings({ tenant, graph, agents, disabled, onChange }: {
  tenant: string; graph: WorkflowGraph; agents: WorkflowAgent[]; disabled: boolean; onChange: (value: WorkflowGraph) => void;
}) {
  const input = (index: number, value: Partial<WorkflowInput>) => onChange({ ...graph, inputs: graph.inputs.map((item, at) => at === index ? { ...item, ...value } : item) });
  return <Form layout="vertical" component="div" disabled={disabled}>
    <Form.Item label="Allowed ways to start"><Checkbox.Group value={graph.allowedOrigins} options={[{ value: 0, label: 'Human' }, { value: 1, label: 'Claimed Agent' }, { value: 2, label: 'Automation occurrence' }]} onChange={allowedOrigins => onChange({ ...graph, allowedOrigins: allowedOrigins.map(Number) })} /></Form.Item>
    <Form.Item label="Eligible coordinators" htmlFor="workflow-coordinators" extra="Agent steps require a coordinator with current delegation authority. Selecting a name grants no permissions."><Select id="workflow-coordinators" labelRender={option => agents.find(a => a.instance.instanceId === option.value)?.instance.persona.name ?? 'Saved coordinator (not on this page)'} mode="multiple" showSearch optionFilterProp="label" placeholder="Choose existing coordinator instances" value={graph.coordinatorInstanceIds} options={agents.map(a => ({ value: a.instance.instanceId, label: a.instance.persona.name }))} onChange={coordinatorInstanceIds => onChange({ ...graph, coordinatorInstanceIds })} /></Form.Item>
    <Flex wrap gap="var(--ac-space-default)">
      <Form.Item label="Total Task step cap" htmlFor="workflow-total-steps"><InputNumber id="workflow-total-steps" min={8} max={144} value={graph.maxTotalTaskSteps} onChange={value => onChange({ ...graph, maxTotalTaskSteps: value ?? 24 })} /></Form.Item>
      <Form.Item label="Total Task time cap (seconds)" htmlFor="workflow-total-seconds"><InputNumber id="workflow-total-seconds" min={60} max={900} value={graph.maxTotalTaskSeconds} onChange={value => onChange({ ...graph, maxTotalTaskSeconds: value ?? 180 })} /></Form.Item>
    </Flex>
    <Typography.Paragraph type="secondary">All real Task reservations share these caps. Agent and Automation starts also require their source budget; workflow selection cannot reset it.</Typography.Paragraph>
    <Collapse items={[{ key: 'inputs', label: `Typed inputs (${graph.inputs.length}/16)`, children: <Flex vertical gap="var(--ac-space-section)">
      {graph.inputs.map((field, index) => <section key={index} aria-label={`Input ${index + 1}`}>
        <Form.Item label="Input key" htmlFor={`workflow-input-key-${index}`}><Input id={`workflow-input-key-${index}`} value={field.key} maxLength={40} onChange={e => input(index, { key: e.target.value })} /></Form.Item>
        <Form.Item label="Input label" htmlFor={`workflow-input-label-${index}`}><Input id={`workflow-input-label-${index}`} value={field.label} maxLength={80} onChange={e => input(index, { label: e.target.value })} /></Form.Item>
        <Form.Item label="Input type" htmlFor={`workflow-input-kind-${index}`}><Select id={`workflow-input-kind-${index}`} value={field.kind} options={['Text', 'Boolean', 'Number', 'Date', 'Choice'].map((label, value) => ({ label, value }))} onChange={kind => input(index, { kind, choices: kind === 4 ? [] : null })} /></Form.Item>
        {field.kind === 0 && <Form.Item label="Maximum characters" htmlFor={`workflow-input-max-${index}`}><InputNumber id={`workflow-input-max-${index}`} value={field.maxLength} min={1} max={4000} onChange={value => input(index, { maxLength: value ?? 1000 })} /></Form.Item>}
        {field.kind === 4 && <Form.Item label="Allowed choices" htmlFor={`workflow-input-choices-${index}`}><Select id={`workflow-input-choices-${index}`} mode="tags" value={field.choices ?? []} tokenSeparators={[',']} onChange={choices => input(index, { choices })} /></Form.Item>}
        <Flex wrap gap="var(--ac-space-compact)" align="center"><Checkbox checked={field.required} onChange={e => input(index, { required: e.target.checked })}>Required input</Checkbox><Button danger onClick={() => onChange({ ...graph, inputs: graph.inputs.filter((_, at) => at !== index) })}>Remove input</Button></Flex>
      </section>)}
      <Button disabled={disabled || graph.inputs.length >= 16} onClick={() => onChange({ ...graph, inputs: [...graph.inputs, { key: `input_${graph.inputs.length + 1}`, label: 'New input', kind: 0, required: false, maxLength: 1000 }] })}>Add typed input</Button>
    </Flex> }, { key: 'goal', label: 'Independent final Goal verification', children: <>
      <Typography.Paragraph type="secondary">Optionally require an existing Goal plan to complete through its own verification owner. Workflow sign-off cannot complete that Goal.</Typography.Paragraph>
      <GoalBinding tenant={tenant} value={graph.successGoal ?? undefined} onChange={successGoal => onChange({ ...graph, successGoal: successGoal ?? null })} />
    </> }]} />
  </Form>;
}
