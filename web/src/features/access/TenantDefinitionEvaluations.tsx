import { useState } from 'react';
import { App, Button, Drawer, Flex, Form, Input, Select, Table, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import type { TenantDraftState, TenantScenario } from '../../services/tenantDefinitions';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { AccessError } from './AccessFeedback';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import type { DraftMutation } from './TenantDefinitionResources';

const checks = ['Tool is offered', 'Tool is not offered', 'Resource is bound', 'Schedule is permitted', 'External action is denied'];
export function TenantDefinitionEvaluations({ snapshot, writable, busy, mutate, error }: { snapshot: TenantDraftState; writable: boolean; busy: boolean; mutate: DraftMutation; error?: unknown }) {
  const { modal } = App.useApp(); const [editing, setEditing] = useState<TenantScenario | null | undefined>();
  return <Flex vertical gap="var(--ac-space-section)">
    <Typography.Paragraph type="secondary">Synthetic evaluations run offline with this saved draft and its owned resources. They do not execute real tools or call hosted providers. Required checks must pass again after the draft changes.</Typography.Paragraph>
    {writable && <Button disabled={busy} style={{ alignSelf: 'start' }} onClick={() => setEditing(null)}>Add evaluation</Button>}
    <Table className="admin-collection-table" rowKey="scenarioId" dataSource={snapshot.scenarios} pagination={{ pageSize: 10, responsive: true, showSizeChanger: false }} locale={{ emptyText: 'No evaluation scenarios saved.' }} columns={[
      { title: 'Evaluation', render: (_, scenario) => { const result = snapshot.results.find(r => r.scenarioId === scenario.scenarioId); return <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>{scenario.title}</Typography.Text><Typography.Text type="secondary">{scenario.requirementLevel === 0 ? 'Required' : 'Advisory'} · {checks[scenario.checkType] ?? 'Saved check'}</Typography.Text><Tag>{!result ? 'Not run' : result.draftRevision !== snapshot.draft.revision || result.scenarioVersion !== scenario.scenarioVersion ? 'Stale — run again' : result.passed ? 'Passed' : 'Failed'}</Tag>{result?.findings.map((finding, index) => <Typography.Text key={index}>{finding}</Typography.Text>)}</Flex>; } },
      { title: 'Actions', render: (_, scenario) => writable && <Flex wrap gap="var(--ac-space-compact)"><Button disabled={busy} aria-label={`Run ${scenario.title}`} onClick={() => mutate(`/evaluation-scenarios/${encodeURIComponent(scenario.scenarioId)}/run`, 'POST')}>Run</Button><Button disabled={busy} aria-label={`Edit ${scenario.title}`} onClick={() => setEditing(scenario)}>Edit</Button><Button danger disabled={busy} aria-label={`Remove ${scenario.title}`} onClick={() => confirmAction(modal, { title: `Remove ${scenario.title}?`, content: 'The scenario will no longer be a publication requirement for this draft.', danger: true, okText: 'Remove evaluation', onOk: () => mutate(`/evaluation-scenarios/${encodeURIComponent(scenario.scenarioId)}`, 'DELETE') })}>Remove</Button></Flex> }
    ]} />
    {editing !== undefined && <ScenarioEditor existing={editing} busy={busy} error={error} onClose={() => setEditing(undefined)} onSave={scenario => mutate(`/evaluation-scenarios/${encodeURIComponent(scenario.scenarioId)}`, 'PUT', scenario, () => setEditing(undefined))} />}
  </Flex>;
}
function ScenarioEditor({ existing, busy, error, onClose, onSave }: { existing: TenantScenario | null; busy: boolean; error?: unknown; onClose: () => void; onSave: (scenario: TenantScenario) => void }) {
  const { message } = App.useApp(); const [form] = Form.useForm<TenantScenario>(); const [dirty, setDirty] = useState(false); const guard = useAccessDraftGuard(dirty, busy, onClose); const check = Form.useWatch('checkType', form) ?? existing?.checkType ?? 4;
  return <>{guard.holder}<Drawer open title={existing ? 'Edit evaluation' : 'New evaluation'} className="access-editor-drawer" size={640} styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!busy} maskClosable={!busy} closable={!busy}
    footer={<Flex justify="end" gap="var(--ac-space-compact)"><Button disabled={busy} onClick={guard.close}>Cancel</Button><Button type="primary" loading={busy} form="tenant-evaluation-form" htmlType="submit">Save evaluation</Button></Flex>}>
    {error !== undefined && <AccessError error={error} />}
    <Form id="tenant-evaluation-form" form={form} layout="vertical" disabled={busy} initialValues={existing ?? { requirementLevel: 0, checkType: 4, toolName: 'email.send', prompt: '' }} onValuesChange={() => setDirty(true)} onFinish={value => onSave({ ...value, toolName: value.checkType === 3 ? null : value.toolName?.trim() ?? null })} onFinishFailed={failure => showAdminFormValidation(message, form, failure)}>
      <Form.Item name="scenarioId" label="Evaluation ID" rules={[{ required: true, pattern: /^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/, message: 'Use up to 64 lowercase letters, digits or hyphens.' }]}><Input readOnly={!!existing} /></Form.Item>
      <Form.Item name="title" label="Evaluation title" rules={[{ required: true, whitespace: true }, { max: 128 }]}><Input maxLength={128} /></Form.Item>
      <Form.Item name="requirementLevel" label="Publication requirement"><Select options={[{ value: 0, label: 'Required to publish' }, { value: 1, label: 'Advisory' }]} /></Form.Item>
      <Form.Item name="checkType" label="Check"><Select options={checks.map((label, value) => ({ label, value }))} /></Form.Item>
      <Form.Item name="toolName" hidden={check === 3} label={check === 2 ? "Resource logical path" : "Capability name"} rules={check === 3 ? [] : [{ required: true, whitespace: true }]} extra={check === 2 ? "Use the saved resource path, such as notes/reference.txt." : "Use the exact capability name, such as email.send."}><Input maxLength={check === 2 ? 256 : 128} /></Form.Item>
      <Form.Item name="prompt" label="Scenario prompt" rules={[{ max: 2000 }]}><Input.TextArea rows={4} maxLength={2000} /></Form.Item>
    </Form>
  </Drawer></>;
}
