import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Empty, Flex, Table, Tabs, Tag, Typography } from 'antd';
import { navigateToAppPath } from '../../app/appRoute';
import { AdminCollectionToolbar, useAdminCollectionSearch } from '../admin/AdminCollectionToolbar';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { identityRequest, type TenantContext } from '../../services/identity';
import { availabilityLabel, capabilityLabels, capabilitySummary, kindLabel, modelApi, offeringLabel, supportsCapabilityFilter, type MetadataTest, type ModelAdmin, type ModelConnection, type ModelOffering, type ModelReferenceTarget } from '../../services/models';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantModelsPath, type ModelAdminPage } from './accessRoute';
import { ModelConnectionEditor } from './ModelConnectionEditor';
import { ModelOfferingEditor } from './ModelOfferingEditor';
import { ModelCatalogFilters, type ModelCatalogFilterValue } from './ModelCatalogFilters';
import { ModelReferences } from './ModelReferences';
import { ModelRoutingEditor } from './ModelRoutingEditor';
import { ModelDefaultEditor } from './ModelDefaultEditor';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

export function TenantModels({ context, page }: { context: TenantContext; page: ModelAdminPage }) {
  const tenant = context.tenant.tenantId;
  const state = useIdentityQuery<ModelAdmin>(modelApi(tenant, '/models'));
  const collection = useAdminCollectionSearch(); const command = useTenantCommand(); const { message } = App.useApp();
  const [editor, setEditor] = useState<{ kind: 'connection'; value?: ModelConnection } | { kind: 'offering'; value?: ModelOffering; connectionId?: string; presetId?: string }>();
  const [filters, setFilters] = useState<ModelCatalogFilterValue>({});
  const [reference, setReference] = useState<ModelReferenceTarget>();
  const inspectReferences = (target: ModelReferenceTarget, element: HTMLElement) => { opener.current = element; setReference(target); };
  const closeReferences = () => { setReference(undefined); requestAnimationFrame(() => opener.current?.focus({ preventScroll: true })); };
  const [metadata, setMetadata] = useState<{ connectionId: string; result: MetadataTest }>();
  const [needsReadback, setNeedsReadback] = useState(false), [defaultDirty, setDefaultDirty] = useState(false);
  const reloadAction = useRef<HTMLButtonElement>(null); const pendingFocus = useRef(false); const focusSource = useRef<ModelAdmin | undefined>(undefined);
  const opener = useRef<HTMLElement | null>(null); const layout = useAdminDetailLayout();
  const close = () => { setEditor(undefined); requestAnimationFrame(() => opener.current?.focus({ preventScroll: true })); };
  const reload = () => { pendingFocus.current = true; focusSource.current = state.data; setNeedsReadback(false); command.clearError(); state.reload(); };
  const edit = (next: typeof editor, element: HTMLElement) => { opener.current = element; setEditor(next); };
  useEffect(() => {
    if (!state.loading && !editor && pendingFocus.current && state.data !== focusSource.current) {
      pendingFocus.current = false; requestAnimationFrame(() => reloadAction.current?.focus({ preventScroll: true }));
    }
  }, [state.loading, state.data, editor]);
  const busy = command.busy || !!editor || !!reference || defaultDirty;
  if (state.loading) return <AccessLoading />;
  if (state.error) return <AccessError error={state.error} retry={reload} />;
  if (!state.data) return null;
  const catalog = state.data;
  const query = collection.search.trim().toLocaleLowerCase();
  const connectionName = (id: string) => catalog.connections.find(c => c.connectionId === id)?.label ?? 'Connection unavailable';
  const check = (connection: ModelConnection, discover: boolean) => {
    if (command.busy || needsReadback || command.requiresReload) return;
    void command.run(async signal => {
      try { return await identityRequest<MetadataTest>(modelApi(tenant, `/model-connections/${connection.connectionId}/${discover ? 'discover' : 'test'}`), { method: 'POST', signal,
        body: { expectedRevision: catalog.securityRevision, expectedConnectionRevision: connection.revision } }); }
      catch (error) { setNeedsReadback(true); throw error; }
    }, result => { setMetadata({ connectionId: connection.connectionId, result }); pendingFocus.current = true; focusSource.current = state.data; state.reload(); });
  };
  const connectionActions = catalog.access.canCreateConnection && <Button type="primary" disabled={busy} onClick={event => edit({ kind: 'connection' }, event.currentTarget)}>Connect a provider</Button>;
  const content = <Flex vertical gap="var(--ac-space-section)">
    {command.error !== undefined && <AccessError error={command.error} />}
    {needsReadback && <Alert type="warning" showIcon title="Reload current configuration" description="The metadata command was refused or its outcome is unknown. Read the retained result before another deliberate attempt." action={<Button disabled={busy} onClick={reload}>Reload configuration</Button>} />}
    {metadata && <Alert type={metadata.result.metadataAvailable ? 'info' : 'warning'} showIcon title={metadata.result.result === 'synthetic_metadata_available' ? 'Offline Synthetic metadata fixture' : metadata.result.metadataAvailable ? 'Metadata access confirmed' : 'Metadata access failed'}
      description={`${metadata.result.modelsObserved} models observed for ${connectionName(metadata.connectionId)} · ${new Date(metadata.result.testedAt).toLocaleString()}. This is listing evidence, not inference or capability certification.`} closable onClose={() => setMetadata(undefined)} />}
    {page === 'connections' && <>
      <Typography.Paragraph type="secondary">Start with a protected provider connection, test metadata access, then explicitly add models and choose an organization default. Starter presets supply metadata, never a shared key.</Typography.Paragraph>
      <AdminCollectionToolbar label="provider connections" value={collection.search} onChange={collection.setSearch} filters={<><Button ref={reloadAction} disabled={busy} onClick={reload}>Reload</Button>{connectionActions}</>} />
      <Table<ModelConnection> className="admin-collection-table" rowKey="connectionId" scroll={{ x: 900 }} pagination={collection.pagination} dataSource={catalog.connections.filter(c => [c.label, c.kind, c.endpoint].join(' ').toLocaleLowerCase().includes(query))}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={query ? 'No connections match this search.' : 'No provider connections you can read.'} /> }} columns={[
          { title: 'Connection', key: 'name', width: 240, render: (_, row) => <Flex vertical align="start" gap="var(--ac-space-tight)"><Typography.Text strong>{row.label}</Typography.Text><Typography.Text type="secondary">{kindLabel(row.kind)}</Typography.Text><Tag>{row.enabled ? 'Enabled configuration' : 'Disabled'}</Tag></Flex> },
          { title: 'Metadata evidence', key: 'test', width: 220, render: (_, row) => <Flex vertical gap="var(--ac-space-tight)"><Typography.Text>{row.lastTestResult === 'synthetic_metadata_available' ? 'Offline Synthetic fixture' : row.lastTestResult === 'metadata_available' ? 'Metadata access confirmed' : row.lastTestResult ? 'Metadata access failed' : 'Not tested'}</Typography.Text>{row.lastTestAt && <Typography.Text type="secondary">{new Date(row.lastTestAt).toLocaleString()}</Typography.Text>}</Flex> },
          { title: 'Actions', key: 'actions', width: 440, render: (_, row) => <Flex wrap gap="var(--ac-space-compact)">
            <Button disabled={busy} aria-label={`Inspect references for connection ${row.label}`} onClick={event => inspectReferences({ kind: 'connection', id: row.connectionId, label: row.label }, event.currentTarget)}>References</Button>
            {catalog.access.manageConnectionIds.includes(row.connectionId) && <Button disabled={busy} onClick={event => edit({ kind: 'connection', value: row }, event.currentTarget)} aria-label={`Edit connection ${row.label}`}>Edit</Button>}
            {catalog.access.testConnectionIds.includes(row.connectionId) && <><Button disabled={busy || needsReadback || command.requiresReload || !row.enabled} onClick={() => check(row, false)} aria-label={`Test metadata ${row.label}`}>Test metadata</Button>{catalog.access.canCreateOffering && <Button disabled={busy || needsReadback || command.requiresReload || !row.enabled} onClick={() => check(row, true)} aria-label={`Discover models ${row.label}`}>Discover</Button>}</>}
            {catalog.access.canCreateOffering && <Button disabled={busy || !row.enabled} onClick={event => edit({ kind: 'offering', connectionId: row.connectionId }, event.currentTarget)} aria-label={`Add model on ${row.label}`}>Add model</Button>}
          </Flex> }
        ]} expandable={{ expandedRowRender: row => <Descriptions {...layout} column={1} size="small" items={[
          { key: 'endpoint', label: 'API endpoint', children: row.endpoint }, { key: 'id', label: 'Connection ID', children: row.connectionId },
          { key: 'credential', label: 'Authentication', children: row.noCredential ? 'Explicit no-credential custom endpoint' : 'Protected tenant inference key' },
          { key: 'revision', label: 'Configuration revision', children: row.revision }
        ]} /> }} />
      {catalog.access.canCreateOffering && catalog.connections.some(c => c.enabled) && <Alert type="info" showIcon title="Add recommended models" description="Use Add model on an enabled connection to adopt compatible starter presets. Discovery adds disabled registrations with unknown capabilities; review each declaration before enabling it." />}
    </>}
    {page === 'models' && <>
      <AdminCollectionToolbar label="model offerings" value={collection.search} onChange={collection.setSearch} filters={<><Button ref={reloadAction} disabled={busy} onClick={reload}>Reload</Button>{catalog.access.canCreateOffering && <Button type="primary" disabled={busy || !catalog.connections.some(c => c.enabled)} onClick={event => edit({ kind: 'offering' }, event.currentTarget)}>Add model</Button>}</>} />
      {!catalog.connections.length && <Alert type="info" showIcon title="Connect a provider first" description="A shared preset becomes runnable only through an owned, enabled provider connection and model offering." action={<Button onClick={() => navigateToAppPath(tenantModelsPath(tenant, 'connections'))}>Connections</Button>} />}
      <ModelCatalogFilters value={filters} connections={catalog.connections} stateLabel="Filter model availability"
        states={[...new Set(catalog.availability.map(a => a.reason))].map(value => ({ value, label: availabilityLabel(value) }))}
        onChange={value => { setFilters(value); collection.pagination.onChange(1); }} />
      <Table<ModelOffering> className="admin-collection-table" rowKey="offeringId" scroll={{ x: 1120 }} pagination={collection.pagination} dataSource={catalog.offerings.filter(o => (!filters.provider || o.kind === filters.provider) && (!filters.connectionId || o.connectionId === filters.connectionId)
        && (!filters.state || catalog.availability.some(a => a.offeringId === o.offeringId && a.reason === filters.state))
        && (!filters.capability || supportsCapabilityFilter(o.capabilities, filters.capability)) && [o.label, o.externalModelId, kindLabel(o.kind), connectionName(o.connectionId), capabilitySummary(o.capabilities)].join(' ').toLocaleLowerCase().includes(query))}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={query || Object.values(filters).some(Boolean) ? 'No models match this search and filters.' : 'No model offerings you can read.'} /> }} columns={[
          { title: 'Model', key: 'name', width: 250, render: (_, row) => <Flex vertical align="start" gap="var(--ac-space-tight)"><Typography.Text strong>{row.label}</Typography.Text><Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>{row.externalModelId}</Typography.Text>{catalog.settings.defaultOfferingId === row.offeringId && <Tag>Organization default</Tag>}</Flex> },
          { title: 'Provider', key: 'provider', width: 130, render: (_, row) => kindLabel(row.kind) },
          { title: 'Connection', key: 'connection', width: 180, render: (_, row) => connectionName(row.connectionId) },
          { title: 'Availability', key: 'status', width: 220, render: (_, row) => { const status = catalog.availability.find(a => a.offeringId === row.offeringId); return <Flex vertical align="start" gap="var(--ac-space-tight)"><Tag>{availabilityLabel(status?.reason ?? '')}</Tag><Typography.Text type="secondary">{row.capabilities.source === 'VerifiedPreset' ? 'Verified preset declaration' : row.capabilities.source === 'Manual' ? 'Administrator declaration' : 'Capabilities unverified'}</Typography.Text><Typography.Text type="secondary">{row.observation === 'Observed' ? 'Observed in metadata' : row.observation === 'NotReturned' ? 'Not returned by discovery' : 'Not observed'}</Typography.Text></Flex>; } },
          { title: 'Capabilities', key: 'capabilities', width: 150,
            render: (_, row) => capabilityLabels(row.capabilities) },
          { title: 'Actions', key: 'actions', width: 210, render: (_, row) => <Flex wrap gap="var(--ac-space-compact)"><Button disabled={busy} aria-label={`Inspect references for model ${offeringLabel(row, catalog)}`} onClick={event => inspectReferences({ kind: 'offering', id: row.offeringId, label: offeringLabel(row, catalog) }, event.currentTarget)}>References</Button>{catalog.access.manageOfferingIds.includes(row.offeringId) && <Button disabled={busy} onClick={event => edit({ kind: 'offering', value: row }, event.currentTarget)} aria-label={`Edit model ${offeringLabel(row, catalog)}`}>Edit model</Button>}</Flex> }
        ]} expandable={{ expandedRowRender: row => <Descriptions {...layout} column={1} size="small" items={[
          { key: 'id', label: 'Offering ID', children: row.offeringId }, { key: 'transport', label: 'Transport', children: row.transport },
          { key: 'revision', label: 'Configuration revision', children: row.revision }, { key: 'hierarchy', label: 'Instruction hierarchy', children: row.capabilities.hierarchy },
          { key: 'semantic', label: 'Semantic output', children: row.capabilities.semanticMode }, { key: 'effort', label: 'Reasoning efforts', children: row.capabilities.reasoningEfforts.join(', ') || 'No reasoning control' },
          { key: 'declaration', label: 'Capability declaration', children: capabilitySummary(row.capabilities) },
          { key: 'verified', label: 'Declaration timestamp', children: row.capabilities.verifiedAt ? new Date(row.capabilities.verifiedAt).toLocaleString() : 'No verification timestamp recorded' },
          { key: 'observed', label: 'Last metadata observation', children: row.lastSeenAt ? new Date(row.lastSeenAt).toLocaleString() : 'Not observed' }
        ]} /> }} />
    </>}
    {page === 'routing' && <ModelRoutingEditor key={tenant} context={context} catalog={catalog} saved={reload} dirtyChanged={setDefaultDirty} />}
    {page === 'defaults' && <ModelDefaultEditor key={tenant} context={context} catalog={catalog} saved={() => { message.success('Organization default saved.'); reload(); }} dirtyChanged={setDefaultDirty} reloadRef={reloadAction} />}
    {editor?.kind === 'connection' && <ModelConnectionEditor context={context} catalog={catalog} connection={editor.value} close={close} readLost={() => { close(); reload(); }} saved={connection => { close(); reload(); setMetadata(undefined); if (!editor.value) message.info(`Add models on ${connection.label}, then choose a default.`); }} />}
    {editor?.kind === 'offering' && <ModelOfferingEditor context={context} catalog={catalog} offering={editor.value} connectionId={editor.connectionId} presetId={editor.presetId} close={close} readLost={() => { close(); reload(); }} saved={() => { close(); reload(); }} />}
  </Flex>;
  return <>{reference && <ModelReferences tenant={tenant} catalog={catalog} target={reference} close={closeReferences} />}<Tabs activeKey={page} onChange={value => navigateToAppPath(tenantModelsPath(tenant, value as ModelAdminPage))} items={[
    { key: 'connections', label: 'Connections', children: page === 'connections' ? content : null },
    { key: 'models', label: 'Models', children: page === 'models' ? content : null },
    { key: 'defaults', label: 'Defaults', children: page === 'defaults' ? content : null },
    { key: 'routing', label: 'Routing', children: page === 'routing' ? content : null }
  ]} /></>;
}
