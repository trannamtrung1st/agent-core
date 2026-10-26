import { useState } from 'react';
import { App, Button, Drawer, Flex, Form, Input, Typography } from 'antd';
import { freshTenantRevision, type EvaluationSuite } from '../../services/tenantEvaluations';
import { IdentityCompletionError, IdentityError, identityRequest, tenantApi } from '../../services/identity';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { AccessError } from './AccessFeedback';
import { EvaluationChecksForm } from './EvaluationChecksForm';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useTenantCommand } from './useTenantCommand';

export function EvaluationSuiteEditor({ tenant, existing, draft, onClose, onSaved }: { tenant: string; existing?: EvaluationSuite | null; draft?: { draftId: string; revision: number }; onClose: () => void; onSaved: () => void }) {
  const [form] = Form.useForm(); const [dirty, setDirty] = useState(false); const command = useTenantCommand(); const { message } = App.useApp();
  const guard = useAccessDraftGuard(dirty, command.busy, onClose);
  return <>{guard.holder}<Drawer open title={draft ? 'Edit publication checks' : existing ? 'New Suite version' : 'New Evaluation Suite'} className="access-editor-drawer" size={640} styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!command.busy} maskClosable={!command.busy} closable={!command.busy}
    footer={<Flex justify="end" gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={guard.close}>Cancel</Button><Button type="primary" loading={command.busy} disabled={command.requiresReload} form="evaluation-suite-form" htmlType="submit">Save {draft ? 'checks' : 'version'}</Button></Flex>}>
    <Typography.Paragraph type="secondary">Saving creates an immutable version. Existing publications and Runs keep their exact version. Save does not execute checks.</Typography.Paragraph>
    {command.error !== undefined && <AccessError error={command.error} />}
    <Form id="evaluation-suite-form" form={form} layout="vertical" disabled={command.busy || command.requiresReload} initialValues={{ name: existing?.name ?? (draft ? 'Publication checks' : ''), checks: existing?.checks ?? [] }} onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)}
      onFinish={value => void command.run(async signal => {
        const body = { ...value, expectedVersion: existing?.version ?? 0, expectedRevision: await freshTenantRevision(tenant, signal) };
        const path = draft ? `/definition-drafts/${draft.draftId}/evaluation-suite` : existing ? `/evaluation-suites/${existing.suiteId}/versions` : '/evaluation-suites';
        try { return await identityRequest<EvaluationSuite | { suite: EvaluationSuite; draftRevision: number }>(tenantApi(tenant, path), { method: draft ? 'PUT' : 'POST', signal, body: draft ? { suite: body, expectedDraftRevision: draft.revision } : body }); }
        catch (error) { if (signal.aborted || error instanceof IdentityError && error.status < 500) throw error; throw new IdentityCompletionError(false); }
      }, result => {
        const saved = 'suite' in result ? result.suite : result;
        if (saved.tenantId !== tenant || saved.version !== (existing?.version ?? 0) + 1 || !saved.contentHash || (existing && saved.suiteId !== existing.suiteId)) throw new IdentityCompletionError();
        guard.release(); void message.success('Immutable Suite version saved.'); onSaved();
      })}>
      <Form.Item name="name" label="Suite name" rules={[{ required: true, whitespace: true }, { max: 128 }]}><Input maxLength={128} autoFocus /></Form.Item>
      <EvaluationChecksForm tenant={tenant} form={form} />
    </Form>
  </Drawer></>;
}
