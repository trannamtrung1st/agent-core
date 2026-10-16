import { useEffect, useRef, useState } from 'react';
import { App, Alert, Button, Descriptions, Drawer, Empty, Flex, Table, Tag, Typography } from 'antd';
import type { SystemCredential } from '../../services/adminApi';
import { identityRequest, type Page, type TenantContext } from '../../services/identity';
import { checkedAuthorizationUrl, credentialApi, credentialKindLabel, credentialPurpose, freshCredentialAccess, purposeLabel, type CredentialAccess, type OAuthSetup, type TenantCredential } from '../../services/tenantCredentials';
import { followAppLink } from '../../app/appLink';
import { navigateToAppPath } from '../../app/appRoute';
import { confirmAction } from '../../app/confirmAction';
import { AdminCollectionToolbar } from '../admin/AdminCollectionToolbar';
import { CredentialConnectionTag } from '../admin/CredentialConnectionTag';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantCredentialPath } from './accessRoute';
import { TenantCredentialEditor } from './TenantCredentialEditor';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

export function TenantCredentials({ context }: { context: TenantContext }) {
  const tenant = context.tenant.tenantId; const [page, setPage] = useState(0); const [search, setSearch] = useState(''); const [creating, setCreating] = useState(false);
  const state = useIdentityQuery<Page<TenantCredential>>(credentialApi(tenant) + `?page=${page}&pageSize=20`);
  const selected = new URLSearchParams(window.location.search).get('credentialId');
  const id = selected && /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(selected) ? selected.toLowerCase() : undefined;
  const collection = useRef<HTMLDivElement>(null); const createAction = useRef<HTMLButtonElement>(null); const previousId = useRef<string | undefined>(undefined); const focusId = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (previousId.current && !id) focusId.current = previousId.current;
    previousId.current = id;
    if (!id && !state.loading && focusId.current) {
      (collection.current?.querySelector<HTMLAnchorElement>(`a[href="${tenantCredentialPath(tenant, focusId.current)}"]`) ?? createAction.current ?? collection.current)?.focus(); focusId.current = undefined;
    }
  }, [id, state.loading, state.data, tenant]);
  const canCreate = ['credentials.read', 'credentials.manage'].every(key => context.permissions.includes(key));
  const items = state.data?.items.filter(({ credential: c }) => [c.displayName, purposeLabel(credentialPurpose(c)), c.emailAccount?.emailAddress ?? c.driveAccount?.emailAddress ?? '', c.status].join(' ').toLowerCase().includes(search.trim().toLowerCase()));
  const changed = () => { state.reload(); };
  return <Flex ref={collection} tabIndex={-1} aria-label="Credential collection" vertical gap="var(--ac-space-section)">
    <AdminCollectionToolbar label="loaded credentials" value={search} onChange={setSearch} showScrollHint={false} filters={<><Button onClick={state.reload}>Reload</Button>{canCreate && <Button ref={createAction} type="primary" onClick={() => setCreating(true)}>Add credential</Button>}</>} />
    {selected && !id && <Alert type="warning" showIcon title="This credential link is invalid." action={<Button onClick={() => navigateToAppPath(tenantCredentialPath(tenant), true)}>Clear link</Button>} />}
    {state.error ? <AccessError error={state.error} retry={state.reload} /> : state.loading ? <AccessLoading /> : <Table className="admin-collection-table" tableLayout="fixed" rowKey={item => item.credential.credentialId} dataSource={items}
      pagination={{ current: page + 1, total: state.data?.total, pageSize: 20, responsive: true, showSizeChanger: false, onChange: value => setPage(value - 1) }}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching credentials on this page.' : 'No credentials you can read in this organization.'}>{search && <Button onClick={() => setSearch('')}>Clear search</Button>}</Empty> }} columns={[
        { title: 'Credential', render: (_, { credential: c }) => <Flex vertical style={{ minWidth: 0 }}><Typography.Link className="single-line-ellipsis" title={c.displayName} href={tenantCredentialPath(tenant, c.credentialId)} onClick={followAppLink}>{c.displayName}</Typography.Link><Typography.Text className="single-line-ellipsis" type="secondary" title={c.emailAccount?.emailAddress || c.driveAccount?.emailAddress || purposeLabel(credentialPurpose(c))}>{purposeLabel(credentialPurpose(c))}{c.emailAccount?.emailAddress || c.driveAccount?.emailAddress ? ` · ${c.emailAccount?.emailAddress || c.driveAccount?.emailAddress}` : ''}</Typography.Text></Flex> },
        { title: 'Status', width: 150, render: (_, { credential: c }) => <Flex vertical align="start" gap="var(--ac-space-tight)"><Tag title="Credential status">{c.status}</Tag>{(c.emailAccount || c.driveAccount) && <CredentialConnectionTag account={c} showCredentialStatus={false} />}</Flex> }
      ]} />}
    {creating && <TenantCredentialEditor tenant={tenant} organization={context.tenant.name} onClose={() => setCreating(false)} onSaved={item => { setCreating(false); changed(); navigateToAppPath(tenantCredentialPath(tenant, item.credential.credentialId)); }} />}
    {id && <CredentialInspector key={`${tenant}:${id}`} tenant={tenant} organization={context.tenant.name} id={id} onChanged={changed} onClose={() => navigateToAppPath(tenantCredentialPath(tenant))} />}
  </Flex>;
}
function CredentialInspector({ tenant, organization, id, onChanged, onClose }: { tenant: string; organization: string; id: string; onChanged: () => void; onClose: () => void }) {
  const state = useIdentityQuery<TenantCredential>(credentialApi(tenant, id)); const access = useIdentityQuery<CredentialAccess>(credentialApi(tenant, id, '/access'));
  const [saved, setSaved] = useState<TenantCredential>(); const [editor, setEditor] = useState<'edit' | 'replace'>(); const [redirecting, setRedirecting] = useState(false); const [connecting, setConnecting] = useState(false);
  const { message, modal } = App.useApp(); const command = useTenantCommand(); const layout = useAdminDetailLayout();
  const item = saved?.credential ?? state.data?.credential; const manage = access.data?.permissions.includes('credentials.manage') ?? false;
  const reload = () => { setSaved(undefined); state.reload(); access.reload(); onChanged(); };
  const updated = (value: TenantCredential) => { setSaved(value); setEditor(undefined); onChanged(); if (new URLSearchParams(window.location.search).has('connection')) navigateToAppPath(tenantCredentialPath(tenant, id), true); };
  const busy = command.busy || command.requiresReload || redirecting || connecting; const oauth = item && ['Gmail', 'Drive'].includes(credentialPurpose(item));
  return <Drawer open title="Credential details" className="access-editor-drawer" size={640} styles={{ wrapper: { maxWidth: '100vw' } }} onClose={() => { if (!editor && !busy) onClose(); }}
    keyboard={!editor && !busy} closable={!editor && !busy} maskClosable={!editor && !busy} footer={<Button disabled={!!editor || busy} onClick={onClose}>Close details</Button>}>
    {state.error || access.error ? <AccessError error={state.error ?? access.error} retry={reload} /> : !item || !access.data ? <AccessLoading /> : <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">
      <Typography.Title level={4}>{item.displayName}</Typography.Title>
      <CallbackOutcome account={item} tenant={tenant} />
      {command.error !== undefined && <AccessError error={command.error} />}
      {!manage && <Alert type="info" showIcon title="Read-only credential" description="You can inspect this saved account. An organization access administrator can grant management access." />}
      <Descriptions {...layout} column={1} size="small" items={[
        { label: 'Organization', children: organization },
        { label: 'Purpose', children: purposeLabel(credentialPurpose(item)) }, { label: 'Kind', children: credentialKindLabel(item.kind) }, { label: 'Credential status', children: <Tag>{item.status}</Tag> },
        ...(oauth || item.emailAccount ? [{ label: 'Connection status', children: <CredentialConnectionTag account={item} showCredentialStatus={false} /> }] : []),
        ...(item.emailAccount?.emailAddress || item.driveAccount?.emailAddress ? [{ label: 'Account', children: item.emailAccount?.emailAddress || item.driveAccount?.emailAddress }] : []),
        { label: 'Agent bindings', children: `${item.bindingCount} ${item.bindingCount === 1 ? 'binding' : 'bindings'}` },
        { label: 'Updated', children: <time dateTime={item.updatedAtUtc}>{new Date(item.updatedAtUtc).toLocaleString()}</time> },
        ...(item.containerRegistry ? [{ label: 'Registry', children: item.containerRegistry.registry }, { label: 'Registry username', children: item.containerRegistry.username }, { label: 'Repository scopes', children: item.containerRegistry.repositoryPrefixes.join(', ') || 'This registry' },
          { label: 'Expiry', children: item.containerRegistry.expiresAtUtc ? new Date(item.containerRegistry.expiresAtUtc).toLocaleString() : 'No expiry' }] : []),
        ...Object.entries(item.metadata).map(([label, value]) => ({ label, children: value })),
        ...(item.allowedOrigins.length ? [{ label: 'Allowed origins', children: item.allowedOrigins.join(', ') }] : [])
      ]} />
      {item.emailAccount?.preset === 'Custom' && <Descriptions {...layout} column={1} size="small" items={(['imap', 'smtp'] as const).flatMap(protocol => [{ label: protocol.toUpperCase(), children: `${item.emailAccount![`${protocol}Endpoint`]?.host}:${item.emailAccount![`${protocol}Endpoint`]?.port} · ${item.emailAccount![`${protocol}Endpoint`]?.tlsMode === 'StartTls' ? 'Required STARTTLS' : 'Required implicit TLS'}` }, { label: `${protocol.toUpperCase()} username`, children: item.emailAccount![`${protocol}Username`] }])} />}
      <Typography.Paragraph type="secondary">Protected values cannot be read back. Connections, agent bindings and permissions are managed separately.</Typography.Paragraph>
      <Flex wrap gap="var(--ac-space-compact)"><Button disabled={busy} onClick={reload}>Reload details</Button>{manage && <>
        <Button disabled={busy} onClick={() => setEditor('edit')}>Edit credential</Button>
        {!oauth && <Button disabled={busy} onClick={() => setEditor('replace')}>Replace protected value</Button>}
        <Button danger disabled={busy || item.bindingCount > 0} title={item.bindingCount > 0 ? 'Remove its agent bindings before deleting this credential.' : undefined} onClick={() => confirmAction(modal, { title: `Delete ${item.displayName}?`, content: `In ${organization}, the protected value and its direct access grants will be removed. This cannot be undone.`, okText: 'Delete credential', danger: true,
          onOk: () => command.run(async signal => { const current = await freshCredentialAccess(tenant, id, signal); return identityRequest(credentialApi(tenant, id) + `?expectedRevision=${current.revision}&expectedCredentialRevision=${item.revision}`, { method: 'DELETE', signal }); }, () => { message.success('Credential deleted.'); onChanged(); onClose(); }) })}>Delete credential</Button>
      </>}</Flex>
      {oauth && <TenantGoogleConnection tenant={tenant} account={item} manage={manage} busy={command.busy || redirecting || !!editor} onChanged={updated} onRedirect={() => setRedirecting(true)} onBusyChange={setConnecting} />}
      {editor && <TenantCredentialEditor tenant={tenant} organization={organization} account={item} replace={editor === 'replace'} onClose={() => setEditor(undefined)} onSaved={updated} />}
    </Flex>}
  </Drawer>;
}
function TenantGoogleConnection({ tenant, account, manage, busy, onChanged, onRedirect, onBusyChange }: { tenant: string; account: SystemCredential; manage: boolean; busy: boolean; onChanged: (value: TenantCredential) => void; onRedirect: () => void; onBusyChange: (busy: boolean) => void }) {
  const setup = useIdentityQuery<OAuthSetup>(credentialApi(tenant, account.credentialId, '/oauth')); const command = useTenantCommand(); const { message, modal } = App.useApp();
  const [revocationUnconfirmed, setRevocationUnconfirmed] = useState(false);
  useEffect(() => { onBusyChange(command.busy); return () => onBusyChange(false); }, [command.busy, onBusyChange]);
  const connected = (account.driveAccount ?? account.emailAccount)?.connectionStatus === 'Connected';
  return <Flex vertical gap="var(--ac-space-default)" className="access-bounded-form"><Typography.Title level={5}>Google connection</Typography.Title>
    {setup.error ? <AccessError error={setup.error} retry={setup.reload} /> : setup.loading ? <Typography.Text type="secondary" role="status">Checking Google setup…</Typography.Text> : setup.data && <>
      {!setup.data.hostEnabled ? <Alert type="warning" showIcon title="Google Drive is disabled on this host" description="Ask the operator to enable it before connecting." />
        : !setup.data.configured ? <Alert type="warning" showIcon title="Google connection is not configured" description="Ask the operator to configure shared Google OAuth, then reload these details." />
          : setup.data.synthetic && <Alert type="info" showIcon title="Synthetic connection" description="Consent is simulated offline. No real Google account or network is used." />}
      <Typography.Text type="secondary">{account.driveAccount ? 'Only app-authorized Drive files are included.' : 'Read mailbox and send permissions are requested together.'} Connecting does not grant an agent access.</Typography.Text>
      {command.error !== undefined && <AccessError error={command.error} />}
      {revocationUnconfirmed && <Alert type="warning" showIcon title="Disconnected locally" description="Google revocation could not be confirmed. Remove Agent Core access in your Google account if you need to revoke it remotely." />}
      {manage && <Flex wrap gap="var(--ac-space-compact)">
        <Button type="primary" aria-label={connected ? 'Reconnect with Google' : 'Connect with Google'} loading={command.busy} disabled={busy || command.requiresReload || account.status !== 'Active' || !setup.data.configured || !setup.data.hostEnabled} onClick={() => void command.run(async signal => {
          const access = await freshCredentialAccess(tenant, account.credentialId, signal);
          return identityRequest<{ authorizationUrl: string }>(credentialApi(tenant, account.credentialId, '/oauth/start'), { method: 'POST', signal, body: { expectedRevision: access.revision, expectedCredentialRevision: account.revision } });
        }, result => { const url = checkedAuthorizationUrl(result.authorizationUrl); onRedirect(); window.location.assign(url); })}>{connected ? 'Reconnect with Google' : 'Connect with Google'}</Button>
        {connected && <Button disabled={busy || command.busy} onClick={() => confirmAction(modal, { title: 'Disconnect this Google account?', content: 'The local grant will be removed. Agent bindings stay saved, and remote revocation will be attempted separately.', okText: 'Disconnect account', danger: true,
          onOk: () => command.run(async signal => { const access = await freshCredentialAccess(tenant, account.credentialId, signal); return identityRequest<TenantCredential & { providerRevoked: boolean }>(credentialApi(tenant, account.credentialId, '/oauth/disconnect'), { method: 'POST', signal,
            body: { expectedRevision: access.revision, expectedCredentialRevision: account.revision } }); }, result => { setRevocationUnconfirmed(!result.providerRevoked); onChanged(result); if (result.providerRevoked) message.success('Account disconnected.'); else message.warning('Disconnected locally. Google revocation could not be confirmed.'); }) })}>Disconnect account</Button>}
      </Flex>}
      {manage && account.status === 'Disabled' && <Typography.Text type="secondary">Enable the credential before connecting.</Typography.Text>}
    </>}
  </Flex>;
}
function CallbackOutcome({ account, tenant }: { account: SystemCredential; tenant: string }) {
  const outcome = new URLSearchParams(window.location.search).get('connection');
  if (!['connected', 'denied', 'failed'].includes(outcome ?? '')) return null;
  const connected = (account.driveAccount ?? account.emailAccount)?.connectionStatus === 'Connected';
  return <Alert showIcon type={outcome === 'connected' && connected ? 'success' : 'info'} title={outcome === 'connected' && connected ? 'Google account connected' : outcome === 'denied' ? 'Connection cancelled' : 'Check the saved connection status'}
    description={outcome === 'failed' ? 'The connection attempt did not finish. The saved account is shown below; reload before trying again.' : undefined}
    action={<Button onClick={() => navigateToAppPath(tenantCredentialPath(tenant, account.credentialId), true)}>Dismiss</Button>} />;
}
