import { useState } from 'react';
import { Alert, App, Button, Checkbox, Drawer, Flex, Form, Input, Select, Typography } from 'antd';
import { identityRequest, IdentityError, type TenantContext } from '../../services/identity';
import { blankCapabilities, capabilityInput, capabilitySummary, kindLabel, modelApi, readModels, transportsFor, type ModelAdmin, type ModelCapabilities, type ModelOffering, type ModelTransport } from '../../services/models';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { ModelImpactConfirmation } from './ModelReferences';
import { AccessError } from './AccessFeedback';
import { ModelCapabilityFields, validateCapabilities } from './ModelCapabilityFields';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useTenantCommand } from './useTenantCommand';

type Fields = { connectionId: string; presetId?: string; externalModelId: string; label: string; transport: ModelTransport; capabilities: ModelCapabilities; enabled: boolean; retired: boolean };
export function ModelOfferingEditor({ context, catalog, offering, connectionId, presetId, close, saved, readLost }: {
  context: TenantContext; catalog: ModelAdmin; offering?: ModelOffering; connectionId?: string; presetId?: string;
  close: () => void; saved: () => void; readLost: () => void;
}) {
  const tenant = context.tenant.tenantId;
  const [pending, setPending] = useState<Fields>();
  const [referenceError, setReferenceError] = useState<unknown>();
  const [readback, setReadback] = useState(false);
  const [baseline, setBaseline] = useState(catalog), [dirty, setDirty] = useState(false), [fresh, setFresh] = useState(true);
  const [form] = Form.useForm<Fields>(); const values = Form.useWatch([], form);
  const command = useTenantCommand(), reader = useTenantCommand(); const busy = command.busy || reader.busy || !!pending;
  const guard = useAccessDraftGuard(dirty, busy, close); const { message } = App.useApp();
  const preset = catalog.presets.find(item => item.presetId === presetId);
  const connection = baseline.connections.find(item => item.connectionId === values?.connectionId);
  const initialConnection = offering?.connectionId ?? connectionId ?? '';
  const isDefault = offering?.offeringId === baseline.settings.defaultOfferingId;
  const current = offering && baseline.offerings.find(item => item.offeringId === offering.offeringId);
  const choosePreset = (id?: string) => {
    const selected = baseline.presets.find(item => item.presetId === id);
    form.setFieldsValue(selected ? { presetId: selected.presetId, externalModelId: selected.externalModelId, label: selected.label, transport: selected.transport, capabilities: capabilityInput(selected.capabilities), enabled: true }
      : { presetId: undefined, externalModelId: '', label: '', capabilities: blankCapabilities(), enabled: false });
    setDirty(true);
  };
  const reload = () => void reader.run(async signal => {
    let next: ModelAdmin;
    try { next = await readModels(tenant, signal); }
    catch (error) { if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) readLost(); throw error; }
    if (offering ? !next.access.manageOfferingIds.includes(offering.offeringId) : !next.access.canCreateOffering) {
      readLost(); throw new IdentityError(403, 'resource_unavailable');
    }
    return next;
  }, next => { setBaseline(next); setReadback(true); setFresh(true); setReferenceError(undefined); command.clearError(); });
  const save = (fields: Fields) => command.run(async signal => {
    try { return await identityRequest<ModelOffering>(modelApi(tenant, '/models' + (offering ? `/${offering.offeringId}` : '')), { method: offering ? 'PATCH' : 'POST', signal, body: {
      ...fields, presetId: offering?.presetId ?? fields.presetId ?? null, enabled: fields.enabled === true, retired: fields.retired === true, capabilities: capabilityInput(fields.capabilities),
      expectedRevision: baseline.securityRevision, expectedOfferingRevision: current?.revision ?? 0
    } }); }
    catch (error) { setFresh(false); if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) readLost(); throw error; }
  }, () => { guard.release(); message.success('Model offering saved.'); saved(); });
  return <>{guard.holder}<Drawer open title={offering ? 'Edit model offering' : 'Add model'} className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }}
    onClose={guard.close} keyboard={!busy} closable={!busy} mask={{ closable: !busy }} destroyOnHidden footer={<Flex wrap justify="end" gap="var(--ac-space-compact)">
      <Button disabled={busy} onClick={guard.close}>Cancel</Button><Button disabled={busy} onClick={reload}>Reload current configuration</Button>
      <Button type="primary" htmlType="submit" form="model-offering-form" loading={command.busy} disabled={busy || !fresh || command.requiresReload}>Save model</Button>
    </Flex>}>
    <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">
      <Typography.Paragraph>Organization: <Typography.Text strong>{context.tenant.name}</Typography.Text></Typography.Paragraph>
      <Typography.Text type="secondary">The offering identifies this exact model on one connection. The same external model on another connection is a separate choice.</Typography.Text>
      {referenceError !== undefined && <AccessError error={referenceError} />}{command.error !== undefined && <AccessError error={command.error} />}{reader.error !== undefined && <AccessError error={reader.error} />}
      {!fresh && <Alert type="warning" showIcon title="Reload before saving again" description="Your draft is retained. Reload the current configuration and compare it before another deliberate save." />}
      {isDefault && <Alert type="info" showIcon title="Current organization default" description="Choose a different default before disabling or retiring this model. Existing Runs retain their captured route." />}
      {readback && current && <Alert type="info" showIcon title={`Current saved model: ${current.label} · revision ${current.revision}`} description={`${current.enabled ? 'Enabled' : 'Disabled'} · ${current.retired ? 'Retired' : 'Not retired'} · ${current.transport}. ${capabilitySummary(current.capabilities)}`} />}
      <Form form={form} id="model-offering-form" layout="vertical" disabled={busy} initialValues={{ connectionId: initialConnection, presetId: offering?.presetId ?? presetId,
        externalModelId: offering?.externalModelId ?? preset?.externalModelId ?? '', label: offering?.label ?? preset?.label ?? '',
        transport: offering?.transport ?? preset?.transport ?? transportsFor(catalog.connections.find(item => item.connectionId === initialConnection)?.kind ?? 'OpenAI')[0],
        capabilities: capabilityInput(offering?.capabilities ?? preset?.capabilities ?? blankCapabilities()), enabled: offering?.enabled ?? !!preset, retired: offering?.retired ?? false }}
        onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)} onFinish={fields => {
          if (busy || !fresh) return;
          if (current) { setPending(structuredClone(fields)); return; }
          void save(fields);
        }}>
        <Form.Item name="connectionId" label="Provider connection" rules={[{ required: true }]}><Select aria-label="Provider connection" disabled={busy || !!offering} placeholder="Choose a saved connection" options={baseline.connections.map(item => ({ value: item.connectionId, label: `${item.label} · ${kindLabel(item.kind)}${item.enabled ? '' : ' · Disabled'}`, disabled: !item.enabled }))} onChange={id => {
          const next = baseline.connections.find(item => item.connectionId === id); choosePreset(); if (next) form.setFieldValue('transport', transportsFor(next.kind)[0]);
        }} /></Form.Item>
        {!offering && <Form.Item name="presetId" label="Starter model" extra="Starter presets carry verified metadata, never a shared key. Clear this field for manual registration."><Select aria-label="Starter model" allowClear placeholder="Manual registration" disabled={!connection} options={baseline.presets.filter(item => item.kind === connection?.kind).map(item => ({ value: item.presetId, label: item.label, disabled: baseline.offerings.some(existing => existing.connectionId === connection?.connectionId && existing.externalModelId === item.externalModelId) }))} onChange={choosePreset} /></Form.Item>}
        <Form.Item name="label" label="Model name" rules={[{ required: true, max: 120 }]}><Input maxLength={120} autoComplete="off" /></Form.Item>
        <Form.Item name="externalModelId" label="External model ID" rules={[{ required: true, max: 255 }]} extra="Exact provider ID. It is fixed after this offering is saved."><Input maxLength={255} autoComplete="off" disabled={busy || !!offering || !!values?.presetId} /></Form.Item>
        <Form.Item name="transport" label="Inference transport" rules={[{ required: true }]}><Select aria-label="Inference transport" disabled={busy || !offering && !!values?.presetId} options={(connection ? transportsFor(connection.kind) : []).map(value => ({ value, label: value }))} /></Form.Item>
        {values?.presetId && !offering && <Typography.Paragraph type="secondary">This adoption uses the saved preset declaration. After saving, deliberate model edits have administrator-declared provenance.</Typography.Paragraph>}
        <Form.Item name="capabilities" label="Capability declaration" rules={[{ validator: validateCapabilities }]}><ModelCapabilityFields disabled={busy || !!values?.presetId && !offering} /></Form.Item>
        <Form.Item name="enabled" valuePropName="checked"><Checkbox disabled={busy || isDefault || !!values?.retired}>Model enabled</Checkbox></Form.Item>
        {offering && <Form.Item name="retired" valuePropName="checked"><Checkbox disabled={busy || isDefault || offering.retired} onChange={event => { if (event.target.checked) form.setFieldValue('enabled', false); }}>Retire this offering permanently</Checkbox></Form.Item>}
      </Form>
      <Typography.Text type="secondary">{current ? `Saved revision ${current.revision}. ` : ''}Current security revision {baseline.securityRevision}. Metadata discovery never enables unknown models or replaces your declaration.</Typography.Text>
    </Flex>
  </Drawer>{pending && current && <ModelImpactConfirmation tenant={tenant} target={{ kind: 'offering', id: current.offeringId, label: `${current.label} · ${connection?.label ?? 'Connection unavailable'}` }}
    revision={baseline.securityRevision} changes={[`Model name: ${current.label} → ${pending.label}`, `Enabled: ${current.enabled ? 'Yes' : 'No'} → ${pending.enabled ? 'Yes' : 'No'}`,
      `Retired: ${current.retired ? 'Yes' : 'No'} → ${pending.retired ? 'Yes' : 'No'}`, `Transport: ${current.transport} → ${pending.transport}`,
      `Before: ${capabilitySummary(current.capabilities)}`, `After: ${capabilitySummary(pending.capabilities)}`]} busy={command.busy} failed={reason => { setReferenceError(reason); setFresh(false); setPending(undefined); }} close={() => setPending(undefined)}
    confirm={() => { void save(pending).finally(() => setPending(undefined)); }} />}</>;
}
