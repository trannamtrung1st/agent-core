import { useState } from 'react';
import { Alert, App, Button, Descriptions, Drawer, Flex, Form, Input, Select, Tag, Typography } from 'antd';
import { evaluatorLabels, freshTenantRevision, hookLabels, outcomeLabel, statusLabel, type EvaluationAttempt } from '../../services/tenantEvaluations';
import { IdentityCompletionError, identityRequest, tenantApi } from '../../services/identity';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';
import { useAccessDraftGuard } from './useAccessDraftGuard';

export function EvaluationAttemptInspector({ tenant, id, canReview, onClose, onSaved }: { tenant: string; id: string; canReview: boolean; onClose: () => void; onSaved?: () => void }) {
  const state = useIdentityQuery<EvaluationAttempt>(tenantApi(tenant, `/evaluation-attempts/${id}`)); const command = useTenantCommand();
  const { message } = App.useApp(); const [form] = Form.useForm(); const [dirty, setDirty] = useState(false); const guard = useAccessDraftGuard(dirty, command.busy, onClose);
  const value = state.data; const pending = value?.findings.filter(f => f.kind === 2 && f.reasonCode === 'review_required') ?? [];
  return <>{guard.holder}<Drawer open title="Evaluation evidence" size={720} className="access-editor-drawer" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!command.busy} maskClosable={!command.busy} closable={!command.busy}>
    <Flex vertical gap="var(--ac-space-section)">
      <Button disabled={state.loading || command.busy || dirty} onClick={() => { command.clearError(); state.reload(); }}>Refresh exact status</Button>
      {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : value && <>
        <Flex wrap gap="var(--ac-space-compact)"><Tag>{statusLabel(value.status)}</Tag><Tag>{outcomeLabel(value.outcome)}</Tag></Flex>
        <Typography.Paragraph type="secondary">An evaluation records evidence about one exact snapshot. It does not grant permission, approve an operation, or rewrite the subject's factual outcome.</Typography.Paragraph>
        {value.reasonCode && <Alert type="info" showIcon title={value.reasonCode.replaceAll('_', ' ')} />}
        <Descriptions column={1} size="small" items={[
          { key: 'subject', label: 'Subject', children: <Typography.Text copyable>{value.subjectId}</Typography.Text> },
          { key: 'revision', label: 'Subject revision', children: value.subjectRevision }, { key: 'suite', label: 'Suite version', children: value.suiteVersion },
          { key: 'hook', label: 'Trigger', children: hookLabels[value.hook] }, { key: 'hash', label: 'Source fingerprint', children: <Typography.Text copyable style={{ overflowWrap: 'anywhere' }}>{value.sourceFingerprint}</Typography.Text> },
          { key: 'sequence', label: 'Trusted commit sequence', children: value.commitSequence ?? 'Not committed' },
          { key: 'limits', label: 'Evaluation limits', children: '10 seconds · up to 512 output tokens per model check' },
          { key: 'attempt', label: 'Attempt', children: <Typography.Text copyable>{value.attemptId}</Typography.Text> }, { key: 'actor', label: 'Initiator / executor', children: value.actorId },
          { key: 'time', label: 'Recorded', children: new Date(value.updatedAt).toLocaleString() }
        ]} />
        {value.findings.map(f => <section key={f.checkId} aria-label={`Evidence for ${f.checkId}`}><Flex vertical gap="var(--ac-space-compact)">
          <Typography.Text strong>{f.checkId} · {outcomeLabel(f.outcome)}</Typography.Text><Typography.Text type="secondary">{evaluatorLabels[f.kind]} · {f.required ? 'Required' : 'Advisory'}</Typography.Text>
          <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', margin: 0 }}>{f.summary}</Typography.Paragraph>
          {f.modelIdentity && <Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>Judge: {f.modelIdentity}</Typography.Text>}
          {f.reviewerId && <Typography.Text type="secondary">Human attestation by {f.reviewerId} · {f.reviewedAt && new Date(f.reviewedAt).toLocaleString()}</Typography.Text>}
        </Flex></section>)}
        {pending.length > 0 && canReview && <Form form={form} layout="vertical" disabled={command.busy || command.requiresReload} onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)} onFinish={assessment => void command.run(async signal => identityRequest<EvaluationAttempt>(tenantApi(tenant, `/evaluation-attempts/${id}/review`), { method: 'POST', signal, body: { ...assessment, expectedRevision: await freshTenantRevision(tenant, signal), expectedAttemptRevision: value.revision } }), result => {
          if (result.attemptId !== id || result.revision !== value.revision + 1) throw new IdentityCompletionError(); setDirty(false); form.resetFields(); state.reload(); onSaved?.(); void message.success('Human assessment stored.');
        })}>
          <Typography.Title level={4}>Manual assessment</Typography.Title>
          <Form.Item name="checkId" label="Pending check" rules={[{ required: true }]}><Select options={pending.map(f => ({ value: f.checkId, label: f.checkId }))} /></Form.Item>
          <Form.Item name="outcome" label="Assessment" rules={[{ required: true }]}><Select options={[0, 1, 2].map(v => ({ value: v, label: outcomeLabel(v) }))} /></Form.Item>
          <Form.Item name="reason" label="Reason" rules={[{ required: true, whitespace: true }, { max: 2000 }]}><Input.TextArea rows={3} maxLength={2000} /></Form.Item>
          <Typography.Paragraph type="secondary">This signed-in human assessment is not authorization approval. Deterministic failures remain recorded.</Typography.Paragraph>
          <Button type="primary" htmlType="submit" loading={command.busy}>Store assessment</Button>
        </Form>}
      </>}
      {command.error !== undefined && <AccessError error={command.error} />}
    </Flex>
  </Drawer></>;
}
