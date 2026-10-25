import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Checkbox, Descriptions, Drawer, Empty, Flex, Form, Input, Select, Table, Tag, Typography } from 'antd';
import { identityRequest, IdentityError } from '../../services/identity';
import { blankCapabilities, capabilityInput, capabilityLabels, capabilitySummary, kindLabel, modelKinds, supportsCapabilityFilter, transportsFor, type ModelCapabilities, type ModelKind, type ModelPreset, type ModelTransport } from '../../services/models';
import { AdminCollectionToolbar, useAdminCollectionSearch } from '../admin/AdminCollectionToolbar';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { AccessError, AccessLoading } from './AccessFeedback';
import { ModelCatalogFilters, type ModelCatalogFilterValue } from './ModelCatalogFilters';
import { ModelCapabilityFields, validateCapabilities } from './ModelCapabilityFields';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

const api = '/api/v2/system/model-presets';
export function SystemModelPresets({ permissions }: { permissions: string[] }) {
  const layout = useAdminDetailLayout();
  const state = useIdentityQuery<ModelPreset[]>(api); const collection = useAdminCollectionSearch();
  const focusSource = useRef<ModelPreset[] | undefined>(undefined);
  const pendingFocus = useRef(false), reloadAction = useRef<HTMLButtonElement>(null);
  const [filters, setFilters] = useState<ModelCatalogFilterValue>({});
  const [editing, setEditing] = useState<{ value?: ModelPreset }>(); const opener = useRef<HTMLElement | null>(null);
  const close = () => { setEditing(undefined); requestAnimationFrame(() => opener.current?.focus({ preventScroll: true })); };
  useEffect(() => { if (!state.loading && !editing && pendingFocus.current && state.data !== focusSource.current) { pendingFocus.current = false; requestAnimationFrame(() => reloadAction.current?.focus()); } }, [state.loading, state.data, editing]);
  const manage = permissions.includes('system.model-presets.manage');
  if (state.loading) return <AccessLoading />;
  if (state.error) return <AccessError error={state.error} retry={state.reload} />;
  return <Flex vertical gap="var(--ac-space-section)">
    <Alert type="info" showIcon title="Shared metadata only" description="Enabled starter presets help organizations configure known models. They contain no credentials and grant no tenant access or execution default. Existing tenant offerings retain their own declarations." />
    <AdminCollectionToolbar label="model presets" value={collection.search} onChange={collection.setSearch} filters={<><Button ref={reloadAction} disabled={!!editing} onClick={() => { pendingFocus.current = true; focusSource.current = state.data; state.reload(); }}>Reload</Button>{manage && <Button type="primary" disabled={!!editing} onClick={event => { opener.current = event.currentTarget; setEditing({}); }}>Add model preset</Button>}</>} />
    <ModelCatalogFilters value={filters} stateLabel="Filter preset state" states={[{ value: 'enabled', label: 'Enabled preset' }, { value: 'disabled', label: 'Disabled preset' }, { value: 'archived', label: 'Archived' }]}
      onChange={value => { setFilters(value); collection.pagination.onChange(1); }} />
    <Table<ModelPreset> className="admin-collection-table" rowKey="presetId" scroll={{ x: 1080 }} pagination={collection.pagination} dataSource={(state.data ?? []).filter(item => (!filters.provider || item.kind === filters.provider)
      && (!filters.state || filters.state === (item.archived ? 'archived' : item.enabled ? 'enabled' : 'disabled'))
      && (!filters.capability || supportsCapabilityFilter(item.capabilities, filters.capability)) && [item.label, kindLabel(item.kind), item.externalModelId, capabilitySummary(item.capabilities)].join(' ').toLocaleLowerCase().includes(collection.search.trim().toLocaleLowerCase()))}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No model presets match this search and filters." /> }} columns={[
        { title: 'Preset', key: 'name', width: 290, render: (_, row) => <Flex vertical align="start" gap="var(--ac-space-tight)"><Typography.Text strong>{row.label}</Typography.Text><Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>{row.externalModelId}</Typography.Text><Typography.Text type="secondary">{row.transport}</Typography.Text></Flex> },
        { title: 'Provider', key: 'provider', width: 140, render: (_, row) => kindLabel(row.kind) },
        { title: 'State', key: 'state', width: 170,  render: (_, row) => <Flex vertical align="start" gap="var(--ac-space-tight)"><Tag>{row.archived ? 'Archived' : row.enabled ? 'Enabled preset' : 'Disabled preset'}</Tag><Typography.Text type="secondary">Revision {row.revision}</Typography.Text></Flex> },
        { title: 'Capabilities', key: 'capabilities', width: 170, render: (_, row) => capabilityLabels(row.capabilities) },
        { title: 'Stored tenant registrations', key: 'references', width: 170, render: (_, row) => row.offeringReferences == null ? 'Count unavailable' : `${row.offeringReferences} ${row.offeringReferences === 1 ? 'registration' : 'registrations'}` },
        { title: 'Actions', key: 'actions', width: 190, render: (_, row) => <Button disabled={!!editing} aria-label={`${manage ? 'Edit' : 'Inspect'} preset ${row.label}`} onClick={event => { opener.current = event.currentTarget; setEditing({ value: row }); }}>{manage ? 'Edit preset' : 'Inspect preset'}</Button> }
      ]} expandable={{ expandedRowRender: row => <Descriptions {...layout} column={1} size="small" items={[
        { key: 'id', label: 'Preset ID', children: row.presetId }, { key: 'profile', label: 'Capability declaration', children: capabilitySummary(row.capabilities) },
        { key: 'source', label: 'Declaration source', children: row.capabilities.source },
        { key: 'verified', label: 'Declaration timestamp', children: row.capabilities.verifiedAt ? new Date(row.capabilities.verifiedAt).toLocaleString() : 'No verification timestamp recorded' },
        { key: 'references', label: 'Registration count', children: 'Counts stored tenant offerings, including disabled and retired registrations. It does not measure executions or expose tenant identities or credentials.' }
      ]} /> }} />
    {editing && <PresetEditor preset={editing.value} manage={manage} close={close} readLost={() => { close(); state.reload(); }} saved={() => { close(); pendingFocus.current = true; focusSource.current = state.data; state.reload(); }} />}
  </Flex>;
}

type Fields = { kind: ModelKind; externalModelId: string; label: string; transport: ModelTransport; capabilities: ModelCapabilities; enabled: boolean; archived: boolean };
function PresetEditor({ preset, manage, close, saved, readLost }: { preset?: ModelPreset; manage: boolean; close: () => void; saved: () => void; readLost: () => void }) {
  const [readback, setReadback] = useState(false);
  const [baseline, setBaseline] = useState(preset), [dirty, setDirty] = useState(false), [fresh, setFresh] = useState(true);
  const [form] = Form.useForm<Fields>(); const values = Form.useWatch([], form); const command = useTenantCommand(), reader = useTenantCommand();
  const busy = command.busy || reader.busy; const guard = useAccessDraftGuard(dirty, busy, close); const { message } = App.useApp();
  const reload = () => void reader.run(async signal => {
    try { return await identityRequest<ModelPreset[]>(api, { signal }); }
    catch (error) { if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) readLost(); throw error; }
  }, current => {
    const next = current.find(item => item.presetId === preset?.presetId);
    if (preset && !next) { close(); return; }
    setBaseline(next); setReadback(true); setFresh(true); command.clearError();
  });
  return <>{guard.holder}<Drawer open title={preset ? manage ? 'Edit model preset' : 'Model preset details' : 'Add model preset'} className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!busy} closable={!busy} mask={{ closable: !busy }} destroyOnHidden
    footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button disabled={busy} onClick={guard.close}>{manage ? 'Cancel' : 'Close'}</Button><Button disabled={busy} onClick={reload}>Reload current preset</Button>{manage && <Button type="primary" htmlType="submit" form="model-preset-form" loading={command.busy} disabled={busy || !fresh || command.requiresReload}>Save preset</Button>}</Flex>}>
    <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">
      <Typography.Text type="secondary">Platform model metadata. This does not configure a tenant connection or reveal protected values.</Typography.Text>
      {command.error !== undefined && <AccessError error={command.error} />}{reader.error !== undefined && <AccessError error={reader.error} />}
      {!fresh && <Alert type="warning" showIcon title="Reload before saving again" description="Compare the current preset revision with your retained draft before another deliberate save." />}
      {readback && baseline && <Alert type="info" showIcon title={`Current saved preset: ${baseline.label} · revision ${baseline.revision}`} description={`${baseline.enabled ? 'Enabled' : 'Disabled'} · ${baseline.archived ? 'Archived' : 'Not archived'} · ${baseline.transport}. ${capabilitySummary(baseline.capabilities)}`} />}
      <Form form={form} id="model-preset-form" layout="vertical" disabled={busy || !manage} initialValues={preset ? { ...preset, capabilities: capabilityInput(preset.capabilities) } : { kind: 'OpenAI', externalModelId: '', label: '', transport: 'Responses', capabilities: blankCapabilities(), enabled: false, archived: false }}
        onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)} onFinish={fields => {
          if (!manage || busy || !fresh) return;
          void command.run(async signal => {
            try { return await identityRequest<ModelPreset>(api + (preset ? `/${preset.presetId}` : ''), { method: preset ? 'PATCH' : 'POST', signal, body: {
              kind: fields.kind, externalModelId: fields.externalModelId, label: fields.label, transport: fields.transport, capabilities: capabilityInput(fields.capabilities), enabled: fields.enabled, archived: fields.archived, expectedRevision: baseline?.revision ?? 0
            } }); }
            catch (error) { setFresh(false); if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) readLost(); throw error; }
          }, () => { guard.release(); message.success('Model preset saved.'); saved(); });
        }}>
        <Form.Item name="kind" label="Provider" rules={[{ required: true }]}><Select aria-label="Provider" disabled={busy || !manage || !!preset} options={modelKinds.map(kind => ({ value: kind, label: kindLabel(kind) }))} onChange={kind => form.setFieldValue('transport', transportsFor(kind)[0])} /></Form.Item>
        <Form.Item name="label" label="Preset name" rules={[{ required: true, max: 120 }]}><Input maxLength={120} /></Form.Item>
        <Form.Item name="externalModelId" label="External model ID" rules={[{ required: true, max: 255 }]}><Input maxLength={255} disabled={busy || !manage || !!preset} /></Form.Item>
        <Form.Item name="transport" label="Inference transport" rules={[{ required: true }]}><Select aria-label="Inference transport" options={transportsFor(values?.kind ?? 'OpenAI').map(value => ({ value, label: value }))} /></Form.Item>
        <Form.Item name="capabilities" label="Capability declaration" rules={[{ validator: validateCapabilities }]}><ModelCapabilityFields disabled={busy || !manage} /></Form.Item>
        <Form.Item name="enabled" valuePropName="checked"><Checkbox disabled={busy || !manage || values?.archived}>Preset enabled for new adoption</Checkbox></Form.Item>
        <Form.Item name="archived" valuePropName="checked"><Checkbox onChange={event => { if (event.target.checked) form.setFieldValue('enabled', false); }}>Archived</Checkbox></Form.Item>
      </Form>
      <Typography.Text type="secondary">{baseline ? `Current saved preset revision ${baseline.revision}. Reload retains your draft.` : 'New stable preset identity will be assigned on save.'}</Typography.Text>
    </Flex>
  </Drawer></>;
}
