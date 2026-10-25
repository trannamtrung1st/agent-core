import { useRef, useState } from 'react';
import { Alert, App, Button, Checkbox, Drawer, Flex, Form, Input, Pagination, Select, Typography } from 'antd';
import { identityRequest, IdentityError, type Page, type TenantContext } from '../../services/identity';
import { kindLabel, modelApi, modelKinds, readModels, type ModelAdmin, type ModelConnection, type ModelKind } from '../../services/models';
import { credentialApi, type TenantCredential } from '../../services/tenantCredentials';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { ModelImpactConfirmation } from './ModelReferences';
import { AccessError } from './AccessFeedback';
import { TenantCredentialEditor } from './TenantCredentialEditor';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

type Fields = { kind?: ModelKind; label: string; endpoint: string; credentialId?: string; noCredential: boolean; enabled: boolean };
export function ModelConnectionEditor({ context, catalog, connection, close, saved, readLost }: {
  context: TenantContext; catalog: ModelAdmin; connection?: ModelConnection; close: () => void;
  saved: (connection: ModelConnection) => void; readLost: () => void;
}) {
  const tenant = context.tenant.tenantId;
  const [pending, setPending] = useState<Fields>();
  const [referenceError, setReferenceError] = useState<unknown>();
  const [baseline, setBaseline] = useState(catalog);
  const [readback, setReadback] = useState(false);
  const [dirty, setDirty] = useState(false), [fresh, setFresh] = useState(true), [creatingKey, setCreatingKey] = useState(false), [page, setPage] = useState(0);
  const [form] = Form.useForm<Fields>();
  const values = Form.useWatch([], form);
  const command = useTenantCommand(), reader = useTenantCommand();
  const busy = command.busy || reader.busy || creatingKey || !!pending;
  const guard = useAccessDraftGuard(dirty, busy, close);
  const { message } = App.useApp();
  const credentialAction = useRef<HTMLButtonElement>(null);
  const credentials = useIdentityQuery<Page<TenantCredential>>(!values?.noCredential ? `${credentialApi(tenant)}?page=${page}&pageSize=20` : null);
  const keyChoices = credentials.data?.items.map(item => item.credential).filter(item => item.purpose === 'ModelInference' && item.kind === 'ApiKey') ?? [];
  if (values?.credentialId && !keyChoices.some(item => item.credentialId === values.credentialId))
    keyChoices.unshift({ credentialId: values.credentialId, displayName: 'Saved key (not on this page)', status: 'Unavailable', purpose: 'ModelInference', kind: 'ApiKey' } as typeof keyChoices[number]);
  const reload = (selectedKey?: string) => void reader.run(async signal => {
    let current: ModelAdmin;
    try { current = await readModels(tenant, signal); }
    catch (error) { if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) readLost(); throw error; }
    if (connection && (!current.connections.some(item => item.connectionId === connection.connectionId) || !current.access.manageConnectionIds.includes(connection.connectionId)) || !connection && !current.access.canCreateConnection) throw new IdentityError(403, 'resource_unavailable');
    return current;
  }, current => {
    setBaseline(current); setReadback(true); setFresh(true); setReferenceError(undefined); command.clearError(); credentials.reload();
    if (selectedKey) form.setFieldValue('credentialId', selectedKey);
  });
  const refuse = (error: unknown) => {
    setFresh(false);
    if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) readLost();
    throw error;
  };
  const save = (fields: Fields) => command.run(async signal => {
    try {
      const original = baseline.connections.find(item => item.connectionId === connection?.connectionId);
      return await identityRequest<ModelConnection>(modelApi(tenant, '/model-connections' + (connection ? `/${connection.connectionId}` : '')), { method: connection ? 'PATCH' : 'POST', signal, body: {
        kind: fields.kind, label: fields.label, endpoint: fields.kind === 'OpenAICompatible' ? fields.endpoint : null,
        credentialId: fields.kind === 'OpenAICompatible' && fields.noCredential ? null : fields.credentialId,
        noCredential: fields.kind === 'OpenAICompatible' && fields.noCredential === true, enabled: fields.enabled,
        expectedRevision: baseline.securityRevision, expectedConnectionRevision: original?.revision ?? 0
      } });
    } catch (error) { return refuse(error); }
  }, result => { guard.release(); saved(result); message.success('Provider connection saved.'); });
  return <>{guard.holder}<Drawer open title={connection ? 'Edit provider connection' : 'Connect a provider'} className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }}
    onClose={guard.close} keyboard={!busy} closable={!busy} mask={{ closable: !busy }} destroyOnHidden footer={<Flex wrap justify="end" gap="var(--ac-space-compact)">
      <Button disabled={busy} onClick={guard.close}>Cancel</Button><Button disabled={busy} onClick={() => reload()}>Reload current configuration</Button>
      <Button type="primary" htmlType="submit" form="model-connection-form" loading={command.busy} disabled={busy || !fresh || command.requiresReload}>Save connection</Button>
    </Flex>}>
    <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">
      <Typography.Paragraph>Organization: <Typography.Text strong>{context.tenant.name}</Typography.Text></Typography.Paragraph>
      <Typography.Text type="secondary">A connection chooses the provider and protected key. Models become selectable only after you explicitly add enabled offerings and choose a default.</Typography.Text>
      {referenceError !== undefined && <AccessError error={referenceError} />}{command.error !== undefined && <AccessError error={command.error} />}{reader.error !== undefined && <AccessError error={reader.error} />}
      {!fresh && <Alert type="warning" showIcon title="Reload before saving again" description="The save was refused or its outcome is unknown. Reload current configuration, compare your retained draft, then deliberately save again." />}
      {readback && connection && <Alert type="info" showIcon title="Current saved connection" description={(() => { const current = baseline.connections.find(item => item.connectionId === connection.connectionId); return current ? `${current.label} · ${current.enabled ? 'Enabled' : 'Disabled'} · revision ${current.revision} · ${current.endpoint} · ${current.noCredential ? 'Explicit no-credential custom endpoint' : `Protected inference key reference: ${current.credentialId}`}` : 'Unavailable'; })()} />}
      <Form id="model-connection-form" form={form} layout="vertical" disabled={busy} initialValues={{ kind: connection?.kind, label: connection?.label ?? '', endpoint: connection?.endpoint ?? '', credentialId: connection?.credentialId ?? undefined, noCredential: connection?.noCredential ?? false, enabled: connection?.enabled ?? true }}
        onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)} onFinish={fields => {
          if (!fresh || busy) return;
          const original = baseline.connections.find(item => item.connectionId === connection?.connectionId);
          if (original) { setPending(structuredClone(fields)); return; }
          void save(fields);
        }}>
        <Form.Item name="kind" label="Provider" rules={[{ required: true }]} extra={connection ? 'Provider kind is fixed for this connection.' : undefined}><Select aria-label="Provider" disabled={busy || !!connection} placeholder="Choose a provider" options={modelKinds.map(kind => ({ value: kind, label: kindLabel(kind) }))} onChange={() => { form.setFieldValue('noCredential', false); form.setFieldValue('endpoint', ''); }} /></Form.Item>
        <Form.Item name="label" label="Connection name" rules={[{ required: true, max: 120 }]}><Input autoComplete="off" maxLength={120} placeholder="For example, Research team" /></Form.Item>
        {values?.kind === 'OpenAICompatible' ? <>
          <Form.Item name="endpoint" label="API endpoint" rules={[{ required: true, type: 'url', max: 2048 }]} extra="HTTPS API root. Local HTTP requires an exact host origin allowance. Redirects and credential-bearing URLs are refused."><Input autoComplete="off" placeholder="https://models.example/v1/" /></Form.Item>
          <Form.Item name="noCredential" valuePropName="checked"><Checkbox>No credential — explicitly allowed custom endpoint</Checkbox></Form.Item>
        </> : values?.kind && <Typography.Paragraph type="secondary">The native {kindLabel(values.kind)} endpoint is controlled by Agent Core.</Typography.Paragraph>}
        {!values?.noCredential && <>
          {credentials.error && <AccessError error={credentials.error} retry={credentials.reload} />}
          <Form.Item name="credentialId" label="Protected inference key" rules={[{ required: true, message: 'Choose or create a protected model-inference API key.' }]} extra="Only readable model-inference API keys are shown. The key is never copied into model configuration."><Select aria-label="Protected inference key" showSearch optionFilterProp="label" loading={credentials.loading} options={keyChoices.map(item => ({ value: item.credentialId, label: item.displayName, disabled: item.status !== 'Active' }))} placeholder="Choose a saved key" /></Form.Item>
          {values?.credentialId && <Typography.Paragraph type="secondary">{(() => {
            const key = credentials.data?.items.find(item => item.credential.credentialId === values.credentialId);
            return !key ? 'Rotation metadata unavailable on this page.' : key.lastRotatedAt ? `Last key replacement in Agent Core: ${new Date(key.lastRotatedAt).toLocaleString()}.` : 'No key replacement recorded in Agent Core.';
          })()}</Typography.Paragraph>}
          {values?.credentialId && <Typography.Paragraph type="secondary" style={{ overflowWrap: 'anywhere' }}>Draft key reference: {values.credentialId}</Typography.Paragraph>}
          {credentials.data && credentials.data.total > 20 && <Pagination size="small" aria-label="Inference credential pages" current={page + 1} total={credentials.data.total} pageSize={20} showSizeChanger={false} onChange={next => setPage(next - 1)} />}
          {context.permissions.includes('credentials.manage') && <Button ref={credentialAction} disabled={busy} onClick={() => setCreatingKey(true)}>Create protected inference key</Button>}
        </>}
        <Form.Item name="enabled" valuePropName="checked"><Checkbox>Connection enabled</Checkbox></Form.Item>
      </Form>
      <Typography.Text type="secondary">Current security revision {baseline.securityRevision}. Reload updates the witness and retains your draft. Move its saved organization default before disabling. Existing Run pins remain retained and can become unavailable.</Typography.Text>
    </Flex>
  </Drawer>{pending && connection && <ModelImpactConfirmation tenant={tenant} target={{ kind: 'connection', id: connection.connectionId, label: connection.label }}
    revision={baseline.securityRevision} changes={[`Connection name: ${baseline.connections.find(c => c.connectionId === connection.connectionId)?.label} → ${pending.label}`, `Enabled: ${baseline.connections.find(c => c.connectionId === connection.connectionId)?.enabled ? 'Yes' : 'No'} → ${pending.enabled ? 'Yes' : 'No'}`,
      `Endpoint: ${baseline.connections.find(c => c.connectionId === connection.connectionId)?.endpoint} → ${pending.kind === 'OpenAICompatible' ? pending.endpoint : 'Provider-native endpoint'}`,
      `Authentication: ${baseline.connections.find(c => c.connectionId === connection.connectionId)?.noCredential ? 'Explicit no credential' : `Protected key ${baseline.connections.find(c => c.connectionId === connection.connectionId)?.credentialId}`} → ${pending.noCredential ? 'Explicit no credential' : `Protected key ${pending.credentialId}`}`]} busy={command.busy} failed={reason => { setReferenceError(reason); setFresh(false); setPending(undefined); }}
    close={() => setPending(undefined)} confirm={() => { void save(pending).finally(() => setPending(undefined)); }} />}{creatingKey && <TenantCredentialEditor tenant={tenant} organization={context.tenant.name} initialPurpose="ModelInference" onClose={() => { setCreatingKey(false); credentialAction.current?.focus(); }} onSaved={item => { setCreatingKey(false); reload(item.credential.purpose === 'ModelInference' ? item.credential.credentialId : undefined); credentialAction.current?.focus(); }} />}</>;
}
