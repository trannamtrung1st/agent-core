import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Drawer, Empty, Flex, Form, InputNumber, Select, Table, Tag, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { navigateToAppPath } from '../../app/appRoute';
import { confirmAction } from '../../app/confirmAction';
import { IdentityError, identityRequest, type Page, type TenantContext } from '../../services/identity';
import type { TenantCredential } from '../../services/tenantCredentials';
import { credentialApi } from '../../services/tenantCredentials';
import { sandboxImageRegistry, sandboxStatusLabel, type SandboxProfile } from '../../services/sandboxes';
import { blankTenantSandbox, saveTenantSandbox, tenantSandboxApi, type TenantSandboxCatalog, type TenantSandboxProfile, type TenantSandboxVerification } from '../../services/tenantSandboxes';
import { AdminCollectionToolbar, adminCollectionPagination } from '../admin/AdminCollectionToolbar';
import { SandboxProfileFields } from '../admin/SandboxProfileFields';
import { SandboxProfileDetails } from '../admin/SandboxProfileDetails';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { SandboxEvidence } from '../sandbox/SandboxEvidence';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantSandboxPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

export function TenantSandboxes({ context }: { context: TenantContext }) {
  const tenant = context.tenant.tenantId; const [page, setPage] = useState(0); const [search, setSearch] = useState(''); const [creating, setCreating] = useState(false); const [acknowledged, setAcknowledged] = useState(false);
  const collection = useRef<HTMLDivElement>(null); const opener = useRef<HTMLElement | null>(null);
  const selected = new URLSearchParams(window.location.search).get('profileId'); const id = selected && /^[a-z][a-z0-9-]{0,63}$/.test(selected) ? selected : undefined;
  const state = useIdentityQuery<TenantSandboxCatalog>(tenantSandboxApi(tenant) + `?page=${page}&pageSize=20&search=${encodeURIComponent(search)}`);
  const close = () => { setCreating(false); navigateToAppPath(tenantSandboxPath(tenant)); requestAnimationFrame(() => (opener.current?.isConnected ? opener.current : collection.current)?.focus()); };
  const saved = (value: TenantSandboxProfile | null) => { setCreating(false); setAcknowledged(true); state.reload(); if (value) navigateToAppPath(tenantSandboxPath(tenant, value.profile.id)); };
  return <Flex ref={collection} tabIndex={-1} aria-label="Sandbox profile collection" vertical gap="var(--ac-space-section)">
    <AdminCollectionToolbar label="Sandbox profiles" value={search} onChange={value => { setSearch(value); setPage(0); }} showScrollHint={false} filters={<><Button onClick={state.reload}>Reload catalog</Button>{state.data?.canCreate && <Button type="primary" onClick={event => { opener.current = event.currentTarget; setCreating(true); }}>Add prebuilt image</Button>}</>} />
    <Typography.Paragraph type="secondary">Profiles describe prebuilt environments. Saving or verifying a profile grants no agent permission. Definition capabilities, agent authority and exact command Review are checked separately.</Typography.Paragraph>
    {acknowledged && <Alert type="success" showIcon title="Profile changes were saved" description="Reload the saved catalog or details before making another change. Verification is separate." closable onClose={() => setAcknowledged(false)} />}
    {selected && !id && <Alert type="warning" showIcon title="This profile link is invalid" action={<Button onClick={close}>Clear link</Button>} />}
    {state.error ? <AccessError error={state.error} retry={state.reload} /> : state.loading ? <AccessLoading /> : <>
      {state.data && !state.data.allReadable && <Alert type="info" showIcon title="Profile access is limited" description="Only profiles you can read are listed." />}
      <Table<TenantSandboxProfile> className="admin-collection-table" tableLayout="fixed" rowKey={item => item.profile.id} dataSource={state.data?.items}
        pagination={{ ...adminCollectionPagination, current: page + 1, pageSize: 20, total: state.data?.total, showSizeChanger: false, onChange: next => setPage(next - 1) }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching readable profiles.' : 'No Sandbox profiles you can read in this organization.'}>{search && <Button onClick={() => { setSearch(''); setPage(0); }}>Clear search</Button>}</Empty> }} columns={[
          { title: 'Profile', render: (_, { profile: p }) => <Flex vertical align="start" gap="var(--ac-space-tight)"><Typography.Link strong style={{ overflowWrap: 'anywhere' }} href={tenantSandboxPath(tenant, p.id)} onClick={event => { opener.current = event.currentTarget; followAppLink(event); }}>{p.name}</Typography.Link><Typography.Text type="secondary">Revision {p.revision} · {p.enabled ? 'Enabled intent' : 'Disabled'}</Typography.Text></Flex> },
          { title: 'Availability', width: 150, render: (_, { profile: p }) => <Tag style={{ whiteSpace: 'normal', marginInlineEnd: 0 }}>{sandboxStatusLabel(p)}</Tag> }
        ]} />
    </>}
    {id && <SandboxInspector key={`${tenant}:${id}`} tenant={tenant} organization={context.tenant.name} id={id} changed={state.reload} close={close} saved={saved} />}
    {creating && <SandboxEditor tenant={tenant} organization={context.tenant.name} initial={null} securityRevision={state.data!.securityRevision} close={close} saved={saved} readLost={() => { setCreating(false); state.reload(); }} />}
  </Flex>;
}
function SandboxInspector({ tenant, organization, id, changed, close, saved }: { tenant: string; organization: string; id: string; changed: () => void; close: () => void; saved: (value: TenantSandboxProfile | null) => void }) {
  const [requestedRevision, setRequestedRevision] = useState<number>(); const [revision, setRevision] = useState<number>(); const [editing, setEditing] = useState(false); const [refresh, setRefresh] = useState(0); const [verificationBlocked, setVerificationBlocked] = useState(false);
  const state = useIdentityQuery<TenantSandboxProfile>(tenantSandboxApi(tenant, id) + (revision ? `?revision=${revision}` : ''), String(refresh));
  const evidence = useIdentityQuery<TenantSandboxVerification>(revision ? null : tenantSandboxApi(tenant, id, '/verification'), String(refresh));
  const editAction = useRef<HTMLButtonElement>(null); const reloadAction = useRef<HTMLButtonElement>(null);
  const closeEditor = () => { setEditing(false); requestAnimationFrame(() => editAction.current?.focus({ preventScroll: true })); };
  const command = useTenantCommand(); const { modal } = App.useApp(); const layout = useAdminDetailLayout(); const [confirming, setConfirming] = useState(false);
  const confirmation = useRef<ReturnType<typeof modal.confirm> | null>(null); const active = useRef(true);
  useEffect(() => { active.current = true; return () => { active.current = false; confirmation.current?.destroy(); }; }, []);
  const [latestRevision, setLatestRevision] = useState<number>(); useEffect(() => { if (state.data && !revision) setLatestRevision(state.data.profile.revision); }, [state.data, revision]);
  const reload = () => { setRefresh(value => value + 1); setVerificationBlocked(false); command.clearError(); changed(); };
  const value = state.data; const p = value?.profile; const busy = command.busy || confirming; const attempt = !revision ? evidence.data?.attempt : null;
  const verify = (imageOnly: boolean) => {
    if (!value || revision || busy || verificationBlocked || command.requiresReload || !value.canManage || evidence.loading || evidence.error || attempt?.active) return;
    setConfirming(true);
    confirmation.current = confirmAction(modal, { title: imageOnly ? 'Check image readiness?' : 'Verify this saved profile?', okText: imageOnly ? 'Check image' : 'Verify profile', content: `${p!.name}, revision ${p!.revision}, in ${organization}. ${imageOnly ? 'Check only the saved image. This does not verify initialization or grant execution authority.' : 'Run saved initialization in a disposable environment. Failed or uncertain cleanup remains recorded. Session files are not used and no agent permission is granted.'}`,
      afterClose: () => { if (active.current) setConfirming(false); }, onOk: () => { setVerificationBlocked(true); return command.run(signal => identityRequest<TenantSandboxVerification>(tenantSandboxApi(tenant, id, imageOnly ? '/check-image' : '/verify'), { method: 'POST', signal, body: { expectedRevision: value.securityRevision, expectedProfileRevision: p!.revision } }), () => { reload(); }); }
    });
  };
  return <Drawer open title="Sandbox profile details" aria-label="Sandbox profile details" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} destroyOnHidden onClose={() => { if (!busy && !editing) close(); }} closable={!busy && !editing} keyboard={!busy && !editing} mask={{ closable: !busy && !editing }} footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button ref={reloadAction} disabled={busy || editing} onClick={reload}>Reload details</Button><Button disabled={busy || editing} onClick={close}>Close details</Button></Flex>}>
    {state.error ? <AccessError error={state.error} retry={reload} /> : state.loading || !p ? <AccessLoading /> : <Flex vertical gap="var(--ac-space-section)">
      <Flex vertical align="start" gap="var(--ac-space-compact)"><Typography.Title level={4}>{p.name}</Typography.Title><Tag>{sandboxStatusLabel(p)}</Tag><Typography.Text type="secondary">{p.availabilityMessage}</Typography.Text>{p.description && <Typography.Paragraph>{p.description}</Typography.Paragraph>}</Flex>
      <Typography.Text type="secondary">{organization} · Revision {p.revision} · {p.enabled ? 'Enabled intent' : 'Disabled'}</Typography.Text>
      {!value.canManage && <Alert type="info" showIcon title="Read-only profile" description="Management access on this profile is required to edit or start verification." />}
      {(latestRevision ?? p.revision) > 1 && <Flex wrap align="center" gap="var(--ac-space-compact)"><Typography.Text>Inspect revision</Typography.Text><InputNumber aria-label="Profile revision history" min={1} max={latestRevision ?? p.revision} precision={0} value={requestedRevision ?? revision ?? p.revision} disabled={busy || editing} onChange={next => setRequestedRevision(next ?? undefined)} /><Button disabled={busy || editing || !Number.isSafeInteger(requestedRevision ?? revision ?? p.revision) || (requestedRevision ?? revision ?? p.revision) < 1 || (requestedRevision ?? revision ?? p.revision) > (latestRevision ?? p.revision)} onClick={() => setRevision((requestedRevision ?? revision ?? p.revision) === latestRevision ? undefined : requestedRevision ?? revision ?? p.revision)}>Read revision</Button>{revision && <Button disabled={busy || editing} onClick={() => { setRevision(undefined); setRequestedRevision(undefined); }}>Return to current</Button>}</Flex>}
      {revision && <Alert type="info" showIcon title="Historical revision" description="This immutable configuration is read-only. Return to Current before editing or verifying." />}
      <SandboxProfileDetails profile={p} />
      <Descriptions {...layout} column={1} size="small" items={[{ label: 'Profile reference', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{p.id}</Typography.Text> }, { label: 'Registry authentication', children: p.registryCredentialId ? 'A protected registry credential is bound. Its values are never returned here.' : 'Public image · no credential' }]} />
      {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
      {!revision && <>
        {evidence.error ? <AccessError error={evidence.error} retry={reload} /> : evidence.loading ? <AccessLoading /> : <section aria-label="Saved verification"><Typography.Title level={5}>Saved verification</Typography.Title>{attempt ? <><Tag>{({ Running: 'Verification in progress', Succeeded: 'Verification completed', Failed: 'Verification failed', CleanupPending: 'Cleanup pending', Expired: 'Expired', Canceled: 'Cancelled' } as Record<string, string>)[attempt.status] ?? 'Retained verification'}</Tag><Typography.Paragraph>{attempt.imageOnly ? 'Image-only check · does not establish setup compatibility.' : attempt.accepted ? 'Full verification accepted for this exact saved revision.' : 'Full verification was not accepted.'} Profile revision {attempt.profileRevision} · {new Date(attempt.startedAt).toLocaleString()}</Typography.Paragraph>{attempt.active && <Typography.Paragraph type="secondary">Cleanup or verification is still pending. Reload retained evidence; do not start another attempt.</Typography.Paragraph>}</> : <Typography.Text type="secondary">No retained verification attempt.</Typography.Text>}</section>}
        {value.canManage && <Flex wrap gap="var(--ac-space-compact)"><Button ref={editAction} disabled={busy} onClick={() => setEditing(true)}>Edit profile</Button><Button disabled={busy || command.requiresReload || verificationBlocked || !p.enabled || evidence.loading || !!evidence.error || !!attempt?.active} onClick={() => verify(true)}>Check image readiness</Button><Button disabled={busy || command.requiresReload || verificationBlocked || !p.enabled || evidence.loading || !!evidence.error || !!attempt?.active} onClick={() => verify(false)}>Verify profile</Button></Flex>}
      </>}
      {(attempt?.result ?? p.verification) && <VerificationOutput value={(attempt?.result ?? p.verification)!} />}
      {verificationBlocked && <Typography.Text role="status" type="secondary">Reload saved evidence before another verification attempt. A failed response never starts an automatic retry.</Typography.Text>}
    </Flex>}
    {editing && value && <SandboxEditor tenant={tenant} organization={organization} initial={value} securityRevision={value.securityRevision} close={closeEditor} saved={next => { setEditing(false); reload(); saved(next); requestAnimationFrame(() => reloadAction.current?.focus()); }} readLost={() => { setEditing(false); reload(); requestAnimationFrame(() => reloadAction.current?.focus()); }} />}
  </Drawer>;
}
function VerificationOutput({ value }: { value: NonNullable<SandboxProfile['verification']> }) {
  return <Flex vertical gap="var(--ac-space-compact)"><Alert showIcon type={value.passed ? 'success' : 'warning'} title={value.message} /><Typography.Text type="secondary">Profile revision {value.profileRevision} · {new Date(value.verifiedAt).toLocaleString()} · {value.exitCode === null ? 'Exit unknown' : `Exit ${value.exitCode}`} · {(value.durationMs / 1000).toFixed(1)}s</Typography.Text><SandboxEvidence stdout={value.stdout} stderr={value.stderr} truncated={value.truncated} label="Profile verification" /></Flex>;
}

function SandboxEditor({ tenant, organization, initial, securityRevision, close, saved, readLost }: { tenant: string; organization: string; initial: TenantSandboxProfile | null; securityRevision: number; close: () => void; saved: (value: TenantSandboxProfile | null) => void; readLost: () => void }) {
  const [baseline, setBaseline] = useState(initial); const [security, setSecurity] = useState(securityRevision); const [dirty, setDirty] = useState(false); const [fresh, setFresh] = useState(true); const [advanced, setAdvanced] = useState<string[]>([]);
  const touched = useRef(new Set<string>()); const [form] = Form.useForm<SandboxProfile>(); const values = Form.useWatch([], form); const command = useTenantCommand(); const reader = useTenantCommand(); const { message } = App.useApp(); const busy = command.busy || reader.busy;
  const guard = useAccessDraftGuard(dirty, busy, close); const original = baseline?.profile ?? blankTenantSandbox();
  const reload = () => void reader.run(async signal => {
    if (baseline) return identityRequest<TenantSandboxProfile>(tenantSandboxApi(tenant, baseline.profile.id), { signal });
    const catalog = await identityRequest<TenantSandboxCatalog>(tenantSandboxApi(tenant), { signal });
    if (!catalog.canCreate) throw new IdentityError(403, 'permission_denied');
    return { profile: blankTenantSandbox(), canManage: true, securityRevision: catalog.securityRevision };
  }, current => {
    if (!current.canManage) { readLost(); return; }
    const prior = form.getFieldsValue(true); const next = { ...current.profile };
    for (const field of touched.current) (next as unknown as Record<string, unknown>)[field] = (prior as unknown as Record<string, unknown>)[field];
    if (baseline) setBaseline(current); setSecurity(current.securityRevision); form.setFieldsValue(next); setFresh(true); command.clearError();
  });
  useEffect(() => { if (reader.error instanceof IdentityError && [401, 403, 404].includes(reader.error.status)) readLost(); }, [reader.error]);
  return <>{guard.holder}<Drawer open title={baseline ? 'Edit Sandbox profile' : 'Add prebuilt image'} aria-label={baseline ? 'Edit Sandbox profile' : 'Add prebuilt image'} className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} destroyOnHidden onClose={guard.close} keyboard={!busy} closable={!busy} mask={{ closable: !busy }} footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button disabled={busy} onClick={guard.close}>Cancel editing</Button><Button type="primary" aria-label="Save profile" loading={command.busy} disabled={busy || !dirty || !fresh || command.requiresReload} onClick={() => form.submit()}>Save profile</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)"><Typography.Paragraph>Save a prebuilt environment in {organization}. Tags resolve to fixed image digests. Agent execution authority remains separate. Host security ceilings can restrict these values.</Typography.Paragraph>
      {command.error !== undefined && <AccessError error={command.error} retry={reload} />}{reader.error !== undefined && <AccessError error={reader.error} retry={reload} />}
      <Flex wrap gap="var(--ac-space-compact)"><Button disabled={busy || command.requiresReload} onClick={reload}>Reload latest profile</Button><Button disabled={busy || !dirty} onClick={() => { touched.current.clear(); form.resetFields(); form.setFieldsValue(original); setDirty(false); }}>Discard edits</Button></Flex>
      {!fresh && <Typography.Text role="status" type="secondary">Reload the latest profile before saving. Your deliberate edits remain staged.</Typography.Text>}
      <Form form={form} initialValues={original} layout="vertical" className="admin-config-form admin-settings-form sandbox-profile-form" disabled={busy || command.requiresReload} onValuesChange={changed => { Object.keys(changed).forEach(key => touched.current.add(key)); setDirty(true); }} onFinishFailed={failure => { setAdvanced(['advanced']); showAdminFormValidation(message, form, failure); }} onFinish={next => void command.run(async signal => {
        try { return await saveTenantSandbox(tenant, baseline, security, next, signal); }
        catch (error) { if (error instanceof IdentityError && error.status === 409) setFresh(false); if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) readLost(); throw error; }
      }, result => { guard.release(); setDirty(false); saved(result); })}>
        <SandboxProfileFields form={form} maxWorkingStorageBytes={16 * 1024 * 1048576} storageLimitDescription="Configuration maximum 16,384 MiB. The host may enforce a lower ceiling." advancedKeys={advanced} onAdvancedChange={setAdvanced} registryField={<RegistryChoices tenant={tenant} selected={values?.registryCredentialId ?? original.registryCredentialId} image={values?.imageReference ?? original.imageReference} disabled={busy} />} />
      </Form>
    </Flex>
  </Drawer></>;
}
function RegistryChoices({ tenant, selected, image, disabled }: { tenant: string; selected?: string | null; image: string; disabled: boolean }) {
  const [page, setPage] = useState(0); const state = useIdentityQuery<Page<TenantCredential>>(credentialApi(tenant) + `?page=${page}&pageSize=20`);
  const options = state.data?.items.filter(item => item.credential.purpose === 'ContainerRegistry').map(({ credential: c }) => ({ value: c.credentialId, label: `${c.displayName} · ${c.containerRegistry?.registry ?? 'Registry unavailable'} · ${c.status}`, disabled: c.status !== 'Active' || !c.containerRegistry || sandboxImageRegistry(image) !== c.containerRegistry.registry || !!c.containerRegistry.expiresAtUtc && Date.parse(c.containerRegistry.expiresAtUtc) <= Date.now() })) ?? [];
  if (selected && !options.some(item => item.value === selected)) options.push({ value: selected, label: 'Saved credential · unavailable on this page', disabled: true });
  return <><Form.Item name="registryCredentialId" label="Registry authentication" extra="Public images need no credential. Only readable organization credentials are listed; binding and use permissions are checked when saving. The saved selection is retained until you change or clear it."><Select aria-label="Registry credential" allowClear disabled={disabled || state.loading || !!state.error} placeholder="Public image · no credential" options={options} /></Form.Item>
    {state.error && <AccessError error={state.error} retry={state.reload} />}
    <Flex wrap gap="var(--ac-space-compact)"><Button disabled={disabled || state.loading || page === 0} onClick={() => setPage(value => value - 1)}>Previous credentials</Button><Button disabled={disabled || state.loading || !state.data || (page + 1) * 20 >= state.data.total} onClick={() => setPage(value => value + 1)}>More credentials</Button><Button disabled={disabled || state.loading} onClick={state.reload}>Reload credentials</Button></Flex>
    <Typography.Paragraph type="secondary">Credential page {page + 1}. Create or manage registry authentication in organization Credentials.</Typography.Paragraph>
  </>;
}
