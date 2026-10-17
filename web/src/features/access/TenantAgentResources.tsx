import { useEffect, useRef, useState, type ComponentRef } from 'react';
import { Alert, App, Button, Drawer, Empty, Flex, Form, Grid, Input, Pagination, Switch, Table, Tag, Typography, theme } from 'antd';
import { IdentityCompletionError, IdentityError, identityRequest, type TenantContext } from '../../services/identity';
import { tenantAgentApi, type TenantResourceDetail, type TenantResourceEditor, type TenantResourceItem } from '../../services/tenantAgentConfiguration';
import { importTenantResources, ResourceAcknowledgedError, resourceFileContent, saveTenantResource, type ResourceWitness } from '../../services/tenantAgentResources';
import type { InstanceResource } from '../../services/instanceConfiguration';
import { followAppLink } from '../../app/appLink';
import { confirmAction } from '../../app/confirmAction';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { InstanceResourceFields, InstanceResourceInspector, RESOURCE_COPY_DESCRIPTION, type InstanceResourceDraft } from '../admin/InstanceResourceFields';
import { ResourceFilePicker, ResourceImportPreview } from '../admin/ResourceImportFields';
import { RESOURCE_KINDS, createPreviewItem, resourceBatchLimitProblem, resourceMediaType, resourcePreviewProblem, type ResourcePreviewItem } from '../admin/resourcePreview';
import { FilePreviewButton } from '../filePreview/FilePreview';
import { downloadFile, fileReadLost, PreviewError, type FileRef } from '../filePreview/fileSource';
import { AccessError, AccessLoading } from './AccessFeedback';
import { TenantAgentHeading } from './TenantAgentHeading';
import { tenantAccessPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

const feedbackKey = 'tenant-resource-change';
const lostRead = (error: unknown) => error instanceof IdentityError && [401, 403, 404].includes(error.status);
const stale = (error: unknown) => error instanceof IdentityError && [401, 403, 404, 409].includes(error.status);
type Selection = { key?: string; mode: 'import' | 'inspect' | 'edit' | 'copy'; opener: HTMLElement };
const witness = (view: TenantResourceDetail | TenantResourceEditor): ResourceWitness => ({ securityRevision: view.securityRevision, instanceRevision: view.agent.instance.revision });
const fileRef = (tenant: string, id: string, resource: InstanceResource): FileRef => ({ kind: 'tenantResource', tenantId: tenant, instanceId: id, resourceKey: resource.key,
  metadata: { displayName: resource.logicalPath.split('/').at(-1)!, contentType: resource.mediaType, byteSize: resource.byteLength, sha256: resource.contentSha256 } });
const draftOf = (resource: InstanceResource): InstanceResourceDraft => ({ logicalPath: resource.logicalPath, kind: resource.kind, enabled: resource.enabled, file: null });

export function TenantAgentResources({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const { modal, message } = App.useApp(); const command = useTenantCommand();
  const [page, setPage] = useState(0); const [search, setSearch] = useState(''); const [query, setQuery] = useState(''); const [refresh, setRefresh] = useState(0);
  const [selection, setSelection] = useState<Selection>(); const [confirming, setConfirming] = useState(false); const [committed, setCommitted] = useState(false);
  const state = useIdentityQuery<TenantResourceEditor>(tenantAgentApi(tenant, id, `/resources/editor?page=${page}&pageSize=10&search=${encodeURIComponent(query)}`), String(refresh));
  const view = state.data; const busy = command.busy || confirming; const reloadButton = useRef<ComponentRef<typeof Button>>(null);
  const alive = useRef(false); const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  const changedInEditor = useRef(false);
  useEffect(() => { alive.current = true; return () => { alive.current = false; confirmation.current?.destroy(); }; }, []);
  useEffect(() => { if (view) { setCommitted(false); if (page > 0 && page * 10 >= view.total) setPage(Math.max(0, Math.ceil(view.total / 10) - 1)); } }, [view, page]);
  useEffect(() => { if (lostRead(state.error)) { setSelection(undefined); confirmation.current?.destroy(); } }, [state.error]);
  const guard = useAccessDraftGuard(false, busy, () => {});
  const blocked = busy || committed || stale(command.error) || state.loading;
  function reload() { command.clearError(); setRefresh(value => value + 1); }
  function close() { const opener = selection?.opener; setSelection(undefined); if (changedInEditor.current) { changedInEditor.current = false; reload(); } requestAnimationFrame(() => { if (opener?.isConnected) opener.focus(); else reloadButton.current?.focus(); }); }
  function retireDetail() { changedInEditor.current = true; close(); }
  async function mutate(item: TenantResourceItem, operation: 'enabled' | 'delete', enabled?: boolean | null) {
    if (!view || command.busy || committed || stale(command.error) || selection) return;
    const resource = item.resource;
    await command.run(async signal => {
      const body = { expectedRevision: view.securityRevision, expectedInstanceRevision: view.agent.instance.revision, expectedResourceRevision: resource.revision, enabled };
      const path = tenantAgentApi(tenant, id, `/resources/${encodeURIComponent(resource.key)}`);
      try { await identityRequest(operation === 'delete' ? `${path}?${new URLSearchParams({ expectedRevision: String(body.expectedRevision), expectedInstanceRevision: String(body.expectedInstanceRevision), expectedResourceRevision: String(resource.revision) })}` : `${path}/enabled`, { method: operation === 'delete' ? 'DELETE' : 'POST', signal, ...(operation === 'delete' ? {} : { body }) }); }
      catch (error) { if (!(error instanceof IdentityCompletionError)) throw error; }
    }, () => { setCommitted(true); message.success({ key: feedbackKey, content: 'Resource change saved. Reading current values.' }); setRefresh(value => value + 1); });
  }
  function confirm(item: TenantResourceItem, operation: 'enabled' | 'delete', enabled?: boolean | null) {
    if (!view || blocked || selection) return;
    message.destroy(feedbackKey); let submitted = false; setConfirming(true);
    const action = operation === 'delete' ? 'Delete resource' : enabled === null ? 'Reset choice' : enabled ? 'Enable resource' : 'Disable resource';
    confirmation.current = confirmAction(modal, { title: `${action}?`, okText: action, danger: operation === 'delete',
      content: <Flex vertical gap="var(--ac-space-compact)"><Typography.Text className="admin-instance-id">Resource: {item.resource.logicalPath}</Typography.Text><Typography.Text>Agent: {view.agent.instance.persona.name}. Organization: {context.tenant.name}.</Typography.Text><Typography.Text>{enabled === null ? 'Remove the stored choice and inherit the published default.' : 'Apply this change to future Runs. Existing Runs retain their pinned content.'}</Typography.Text>{enabled === false && item.resource.dependencies.length > 0 && <Typography.Text>Readable enabled Skills depend on this file: {item.resource.dependencies.join(', ')}. Resolve dependencies first.</Typography.Text>}</Flex>,
      onOk: async () => { if (!alive.current) return; submitted = true; setConfirming(false); await mutate(item, operation, enabled); },
      afterClose: () => { confirmation.current = null; if (alive.current) { setConfirming(false); if (submitted) requestAnimationFrame(() => reloadButton.current?.focus()); } }
    });
  }
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">{guard.holder}
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to instances</Typography.Link><Button ref={reloadButton} disabled={busy || !!selection} onClick={reload}>Reload resources</Button></Flex>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}{committed && <Alert type="info" showIcon title="Resource changes were saved" description="Reload reads current facts and never replays a command." />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : view && <>
      <TenantAgentHeading view={{ agent: view.agent, canUpdate: view.canCreate || view.items.some(item => item.canManage || item.canDelete) }} readOnlyDescription="Resource changes require typed item access. Creating or copying a local file requires organization-wide Resource read and manage access." />
      <AgentConfigurationPanel title="Resources" label="Instance resources" description="Inherited files are read-only. Local files are independent. Changes apply to the next new Run and grant no execution authority." bodyGap="section">
        {!view.allReadable && <Alert type="info" showIcon title="Only readable resources are shown" description="Search, totals and dependencies include only content you can read." />}
        <Flex data-feedback-actions wrap gap="var(--ac-space-compact)"><Button type="primary" disabled={blocked || !!selection || !view.canCreate} onClick={event => setSelection({ mode: 'import', opener: event.currentTarget })}>Add Instance resources</Button><Input.Search enterButton="Search" aria-label="Search readable resources" placeholder="Search readable resources" maxLength={128} value={search} disabled={busy || !!selection} onChange={event => { setSearch(event.target.value); if (!event.target.value) { setPage(0); setQuery(''); } }} onSearch={value => { setPage(0); setQuery(value.trim()); }} /></Flex>
        <Table<TenantResourceItem> size="small" className="admin-collection-table" tableLayout="fixed" rowKey={item => item.resource.key} pagination={false} dataSource={view.items} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={query ? 'No matching readable resources.' : 'No readable resources on this page.'} /> }} columns={[{ title: 'Resource and configuration', render: (_, item) => {
          const r = item.resource; return <Flex vertical gap="var(--ac-space-compact)">
            <Typography.Text strong className="admin-instance-id">{r.logicalPath}</Typography.Text><Flex wrap gap="var(--ac-space-compact)"><Tag>{r.origin}</Tag><Typography.Text type="secondary" className="admin-instance-id">{RESOURCE_KINDS[r.kind - 1]} · {r.byteLength.toLocaleString()} bytes · {r.mediaType}</Typography.Text></Flex>
            {r.sourceDefinitionId && <Typography.Text type="secondary" className="admin-instance-id">Copied from Definition v{r.sourceDefinitionVersion} · resource {r.sourceDefinitionResourceId ?? r.sourceDefinitionId}</Typography.Text>}
            <Typography.Text type="secondary" className="admin-instance-id">Readable enabled Skill dependencies: {r.dependencies.join(', ') || 'None'}</Typography.Text>
            <Flex data-feedback-actions wrap align="center" gap="var(--ac-space-compact)"><Switch aria-label={`Enable resource ${r.logicalPath}`} checked={r.enabled} disabled={blocked || !!selection || !item.canManage} onChange={enabled => confirm(item, 'enabled', enabled)} /><Typography.Text>{r.enabled ? 'Effective: enabled' : 'Effective: disabled'}</Typography.Text><Typography.Text type="secondary">{r.origin === 'Instance' ? 'Local choice' : r.enabledOverride == null ? 'Inherited choice' : 'Instance choice'}</Typography.Text></Flex>
            <Flex data-feedback-actions wrap gap="var(--ac-space-compact)"><Button disabled={command.busy || !!selection} onClick={event => setSelection({ key: r.key, mode: 'inspect', opener: event.currentTarget })}>Inspect resource</Button><ResourceDownload tenant={tenant} id={id} resource={r} disabled={busy || !!selection} onReadLost={reload} />{r.origin === 'Instance' && item.canManage && <Button disabled={blocked || !!selection} onClick={event => setSelection({ key: r.key, mode: 'edit', opener: event.currentTarget })}>Edit resource</Button>}{item.canCopy && <Button disabled={blocked || !!selection} onClick={event => setSelection({ key: r.key, mode: 'copy', opener: event.currentTarget })}>Copy to Instance</Button>}{item.canDelete && <Button danger disabled={blocked || !!selection} onClick={() => confirm(item, 'delete')}>Delete</Button>}{r.origin === 'Definition' && r.enabledOverride != null && item.canManage && <Button disabled={blocked || !!selection} onClick={() => confirm(item, 'enabled', null)}>Reset choice</Button>}</Flex>
          </Flex>;
        } }]} />
        {view.total > 10 && <Pagination size="small" aria-label="Resource pages" current={page + 1} total={view.total} pageSize={10} showSizeChanger={false} disabled={busy || !!selection} onChange={value => setPage(value - 1)} />}
      </AgentConfigurationPanel>
    </>}
    {selection && <TenantResourceDrawer key={`${selection.mode}:${selection.key ?? 'new'}`} tenant={tenant} organization={context.tenant.name} id={id} selection={selection} onClose={close} onChanged={() => { changedInEditor.current = true; }} onSaved={() => { changedInEditor.current = true; close(); setCommitted(true); }} onReadLost={retireDetail} />}
  </Flex>;
}

function ResourceDownload({ tenant, id, resource, disabled, onReadLost }: { tenant: string; id: string; resource: InstanceResource; disabled: boolean; onReadLost: () => void }) {
  const command = useTenantCommand();
  useEffect(() => { if (fileReadLost(command.error)) onReadLost(); }, [command.error]);
  return <Flex vertical gap="var(--ac-space-compact)"><Button aria-label="Download" disabled={disabled} loading={command.busy} onClick={() => void command.run(signal => downloadFile(fileRef(tenant, id, resource), signal), () => {})}>Download</Button>{command.error !== undefined && <Alert type="error" showIcon title={command.error instanceof PreviewError ? command.error.message : 'Download failed. Reload and retry.'} />}</Flex>;
}

function TenantResourceDrawer({ tenant, organization, id, selection, onClose, onReadLost, onSaved, onChanged }: { tenant: string; organization: string; id: string; selection: Selection; onClose: () => void; onReadLost: () => void; onSaved: () => void; onChanged: () => void }) {
  const { modal, message } = App.useApp(); const { token } = theme.useToken(); const compact = !Grid.useBreakpoint().md; const command = useTenantCommand();
  const [refresh, setRefresh] = useState(0); const [draft, setDraft] = useState<InstanceResourceDraft>(); const [dirty, setDirty] = useState(false); const [enabledTouched, setEnabledTouched] = useState(false);
  const [items, setItems] = useState<ResourcePreviewItem[]>([]); const [enabled, setEnabled] = useState(true); const [copyPath, setCopyPath] = useState(''); const [savedCount, setSavedCount] = useState(0); const [localError, setLocalError] = useState<string>(); const [confirming, setConfirming] = useState(false);
  const alive = useRef(false); const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null); const closeButton = useRef<ComponentRef<typeof Button>>(null);
  const primaryButton = useRef<ComponentRef<typeof Button>>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; confirmation.current?.destroy(); }; }, []);
  const state = useIdentityQuery<TenantResourceDetail | TenantResourceEditor>(tenantAgentApi(tenant, id, selection.key ? `/resources/${encodeURIComponent(selection.key)}/editor` : '/resources/editor?pageSize=100'), String(refresh));
  const manifest = useIdentityQuery<{ catalog: { instanceRevision: number; resources: InstanceResource[] }; securityRevision: number }>(selection.mode === 'import' ? tenantAgentApi(tenant, id, '/resources') : null, String(refresh));
  const view = state.data; const detail = view && 'item' in view ? view.item : undefined; const resource = detail?.resource;
  useEffect(() => { if (resource) setDraft(current => current ? enabledTouched ? current : { ...current, enabled: resource.enabled } : draftOf(resource)); }, [view, enabledTouched]);
  useEffect(() => { if (lostRead(state.error)) { setDraft(undefined); setItems([]); onReadLost(); } }, [state.error]);
  useEffect(() => { if (selection.mode === 'import' && lostRead(manifest.error)) { setItems([]); onReadLost(); } }, [manifest.error]);
  const staged = selection.mode === 'import' ? items.length > 0 || !enabled : selection.mode === 'copy' ? !!copyPath : dirty;
  const busy = command.busy || confirming; const guard = useAccessDraftGuard(staged, busy, onClose);
  const allowed = !!view && view.agent.instance.lifecycle === 'Active' && (selection.mode === 'import' ? 'canCreate' in view && view.canCreate : selection.mode === 'copy' ? detail?.canCopy : detail?.canManage && resource?.origin === 'Instance');
  // The paged editor can contain fewer than the bounded complete 64 inherited + 64 local files.
  // Import preflight uses the existing authorized manifest only when both read witnesses agree.
  const coherentManifest = !!view && manifest.data?.securityRevision === view.securityRevision && manifest.data.catalog.instanceRevision === view.agent.instance.revision;
  const importPending = selection.mode === 'import' && (manifest.loading || !!manifest.error || !coherentManifest);
  const disabled = busy || state.loading || !!state.error || !allowed || command.error !== undefined || importPending;
  const existing = coherentManifest ? manifest.data!.catalog.resources.filter(item => item.origin === 'Instance') : [];
  const limit = resourceBatchLimitProblem(items, existing.length, existing.reduce((sum, item) => sum + item.byteLength, 0), 'Instance');
  const importBlocked = !items.length || !!limit || items.some(item => resourcePreviewProblem({ ...item, uploadError: undefined }, items.map(i => i.logicalPath), existing.map(i => i.logicalPath), 'Instance'));
  const editProblem = draft && resource && resourcePreviewProblem({ logicalPath: draft.logicalPath, kind: RESOURCE_KINDS[draft.kind - 1], byteLength: draft.file?.size ?? resource.byteLength, mediaType: draft.file ? resourceMediaType(draft.file, draft.logicalPath) : resource.mediaType }, [draft.logicalPath], [], 'Instance');
  const reload = () => { command.clearError(); setLocalError(undefined); setItems(current => current.map(item => ({ ...item, uploadError: undefined }))); setRefresh(value => value + 1); };
  async function save() {
    if (!view || disabled) return;
    setLocalError(undefined);
    await command.run(async signal => {
      if (selection.mode === 'import') {
        if (importBlocked) return false;
        await importTenantResources(tenant, id, witness(view), items.map(item => ({ ...item, kind: RESOURCE_KINDS.indexOf(item.kind as typeof RESOURCE_KINDS[number]) + 1, mediaType: item.mediaType! })), enabled, signal, item => {
          if (!signal.aborted) { setItems(current => current.filter(i => i.key !== item.key)); setSavedCount(value => value + 1); onChanged(); }
        });
      } else if (selection.mode === 'edit' && draft && resource && dirty && !editProblem) {
        try { await saveTenantResource(tenant, id, witness(view), { resourceId: resource.key.slice('instance:'.length), expectedResourceRevision: resource.revision, logicalPath: draft.logicalPath.trim(), kind: draft.kind,
          mediaType: draft.file ? resourceMediaType(draft.file, draft.logicalPath) : resource.mediaType, ...(draft.file ? { content: await resourceFileContent(draft.file, signal) } : {}), ...(enabledTouched ? { enabled: draft.enabled } : {}) }, signal); }
        catch (error) { if (!(error instanceof ResourceAcknowledgedError)) throw error; }
      } else return false;
      return true;
    }, saved => { if (saved) { guard.release(); setDirty(false); message.success({ key: feedbackKey, content: 'Resources saved. Changes apply to the next new Run.' }); onSaved(); } });
  }
  function copy() {
    if (!view || !resource || disabled || !copyPath.trim()) return;
    setConfirming(true); message.destroy(feedbackKey);
    confirmation.current = confirmAction(modal, { title: 'Copy Definition resource?', okText: 'Copy resource',
      content: <Flex vertical gap="var(--ac-space-compact)"><Typography.Text className="admin-instance-id">Source: {resource.logicalPath}</Typography.Text><Typography.Text className="admin-instance-id">Local destination: {copyPath.trim()}</Typography.Text><Typography.Text>Agent: {view.agent.instance.persona.name}. Organization: {organization}.</Typography.Text><Typography.Text>{RESOURCE_COPY_DESCRIPTION}</Typography.Text></Flex>,
      onOk: async () => {
        if (!alive.current) return;
        setConfirming(false);
        await command.run(async signal => {
          try { await identityRequest(tenantAgentApi(tenant, id, `/resources/${encodeURIComponent(resource.key)}/copy`), { method: 'POST', signal, body: { logicalPath: copyPath.trim(), expectedRevision: view.securityRevision, expectedInstanceRevision: view.agent.instance.revision } }); }
          catch (error) { if (!(error instanceof IdentityCompletionError)) throw error; }
        }, () => { guard.release(); onSaved(); });
      }, afterClose: () => { confirmation.current = null; if (alive.current) { setConfirming(false); requestAnimationFrame(() => { if (!alive.current) return; primaryButton.current?.focus(); if (document.activeElement !== primaryButton.current) closeButton.current?.focus(); }); } }
    });
  }
  if (lostRead(state.error)) return guard.holder;
  const inspect = selection.mode === 'inspect';
  const primaryLabel = selection.mode === 'copy' ? 'Review copy' : selection.mode === 'import' ? 'Add resources' : 'Save resource';
  return <>{guard.holder}<Drawer open className="access-editor-drawer" title={inspect ? 'Resource inspection' : selection.mode === 'copy' ? 'Copy to Instance' : selection.mode === 'edit' ? 'Edit Instance resource' : 'Add Instance resources'} size={compact ? '100%' : selection.mode === 'import' ? 880 : 640} styles={{ wrapper: { maxWidth: '100vw' }, body: { padding: token.padding } }}
    onClose={guard.close} keyboard={!busy} maskClosable={!busy} closable={!busy} focusable={{ trap: true, focusTriggerAfterClose: false }} afterOpenChange={open => { if (open && inspect) closeButton.current?.focus(); }}
    footer={<Flex data-feedback-actions wrap justify="end" gap={token.paddingXS}><Button ref={closeButton} disabled={busy} onClick={guard.close}>{inspect ? 'Close inspection' : 'Cancel editing'}</Button>{!inspect && <Button ref={primaryButton} type="primary" aria-label={primaryLabel} loading={command.busy} disabled={disabled || (selection.mode === 'import' ? importBlocked : selection.mode === 'edit' ? !dirty || !!editProblem : !copyPath.trim())} onClick={() => selection.mode === 'copy' ? copy() : void save()}>{primaryLabel}</Button>}</Flex>}>
    <Flex vertical gap="var(--ac-space-section)"><Typography.Text>Agent: {view?.agent.instance.persona.name ?? 'Loading…'}. Organization: {organization}. Files apply to future Runs and grant no execution authority.</Typography.Text>
      <Flex data-feedback-actions wrap gap="var(--ac-space-compact)"><Button disabled={busy} onClick={reload}>Reload latest resources</Button>{staged && view && <Button disabled={busy || state.loading} onClick={() => { if (resource) setDraft(draftOf(resource)); setDirty(false); setEnabledTouched(false); setItems([]); setEnabled(true); setCopyPath(''); }}>Discard draft</Button>}</Flex>
      {savedCount > 0 && <Alert type="info" showIcon title={`${savedCount} ${savedCount === 1 ? 'resource saved' : 'resources saved'}`} description="Only remaining files are staged. Reload reads current facts and never submits saved files again." />}
      {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : !inspect && !allowed && <Alert type="warning" showIcon title="Resource cannot be changed" description="Your draft is retained. Current typed access and an Active instance are required." />}
      {selection.mode === 'import' && (manifest.error ? <AccessError error={manifest.error} retry={reload} /> : !state.loading && !manifest.loading && view && !coherentManifest && <Alert type="warning" showIcon title="Resource facts changed while loading" description="Reload latest resources before saving. Your staged files are retained." />)}
      {command.error !== undefined && (command.error instanceof ResourceAcknowledgedError ? <Alert type="info" showIcon title={command.error.message} /> : <AccessError error={command.error} retry={reload} />)}{localError && <Alert type="error" showIcon title={localError} />}
      {staged && <Typography.Text type="secondary">Unsaved changes. Reload keeps this draft; Cancel asks before discarding.</Typography.Text>}
      {inspect && resource ? <><Typography.Text strong className="admin-instance-id">{resource.logicalPath}</Typography.Text><InstanceResourceInspector resource={resource} /><Flex data-feedback-actions wrap gap="var(--ac-space-compact)"><FilePreviewButton file={fileRef(tenant, id, resource)} disabled={busy || state.loading || !!state.error} onReadLost={onReadLost} /><ResourceDownload tenant={tenant} id={id} resource={resource} disabled={busy || state.loading || !!state.error} onReadLost={onReadLost} /></Flex></> : selection.mode === 'import' ? <>
        <Typography.Text type="secondary">Choose files or a folder, then review each path and kind. Nothing uploads until Add resources. Each saved file is removed from this draft; a failure keeps the remaining files.</Typography.Text>
        <ResourceFilePicker disabled={busy} onFiles={files => { if (alive.current) setItems(current => [...current, ...files.map(createPreviewItem)]); }} onError={setLocalError} />
        {items.length > 0 && <ResourceImportPreview items={items} existingPaths={existing.map(item => item.logicalPath)} scope="Instance" disabled={busy} onChange={setItems} />}{limit && <Alert type="warning" showIcon title={limit} />}
        <Flex data-feedback-actions align="center" gap="var(--ac-space-compact)"><Switch aria-label="Enable imported resources" disabled={busy} checked={enabled} onChange={setEnabled} /><Typography.Text>Enable added resources</Typography.Text></Flex>
      </> : selection.mode === 'copy' && resource ? <><Typography.Text className="admin-instance-id">Read-only source: {resource.logicalPath}</Typography.Text><Form layout="vertical"><Form.Item label="Local destination" extra="A distinct local file with retained source provenance."><Input aria-label="Copy resource logical path" maxLength={240} value={copyPath} disabled={busy} onChange={event => setCopyPath(event.target.value)} /></Form.Item></Form></> : resource && draft && <InstanceResourceFields resource={resource} value={draft} disabled={busy || state.loading || !!state.error || !allowed} problem={editProblem} onChange={patch => { setDraft({ ...draft, ...patch }); setDirty(true); if (patch.enabled !== undefined) setEnabledTouched(true); }} />}
    </Flex>
  </Drawer></>;
}
