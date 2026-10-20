import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Empty, Flex, Form, Input, Table, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { IdentityError, identityRequest, tenantApi, type TenantContext } from '../../services/identity';
import { createTenantWebhook, requireWebhook, requireWebhookToken, tenantWebhookApi, type TenantWebhook, type TenantWebhookPage, type TenantWebhookReceipts, type TenantWebhookToken } from '../../services/tenantWebhooks';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { AdminCollectionToolbar, adminCollectionPagination } from '../admin/AdminCollectionToolbar';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';
import { useAccessDraftGuard } from './useAccessDraftGuard';

const lostRead = (error: unknown) => error instanceof IdentityError && [401, 403, 404].includes(error.status);
const uncertain = (error: unknown) => !(error instanceof IdentityError) || error.status >= 500;
const date = (value: string) => new Date(value).toLocaleString();
type Selection = { id?: string; opener: HTMLElement };
export function TenantEvents({ context }: { context: TenantContext }) {
  const { message } = App.useApp(); const tenant = context.tenant.tenantId; const [page, setPage] = useState(0); const [search, setSearch] = useState('');
  const [selection, setSelection] = useState<Selection>(); const [secret, setSecret] = useState<TenantWebhookToken>();
  const collection = useRef<HTMLDivElement>(null); const reloadButton = useRef<HTMLButtonElement>(null); const createButton = useRef<HTMLButtonElement>(null);
  const pendingFocus = useRef<Selection | undefined>(undefined);
  const state = useIdentityQuery<TenantWebhookPage>(tenantWebhookApi(tenant, `?page=${page}&pageSize=10`));
  const builtins = useIdentityQuery<{ items: { key: string; description: string }[] }>(context.permissions.includes('core-events.read') ? tenantApi(tenant, '/events/builtin') : null);
  useEffect(() => { if (lostRead(state.error)) { setSecret(undefined); setSelection(undefined); } }, [state.error]);
  useEffect(() => {
    if (!state.data || !pendingFocus.current) return;
    const target = pendingFocus.current; const row = [...(collection.current?.querySelectorAll<HTMLButtonElement>('[data-webhook-id]') ?? [])].find(button => button.dataset.webhookId === target.id);
    (row ?? (target.opener.isConnected ? target.opener : createButton.current && !createButton.current.disabled ? createButton.current : reloadButton.current))?.focus(); pendingFocus.current = undefined;
  }, [state.data]);
  const close = () => { const opener = selection?.opener; setSelection(undefined); if (opener?.isConnected) opener.focus(); };
  const refresh = () => { setSecret(undefined); state.reload(); builtins.reload(); };
  const changed = (token?: TenantWebhookToken) => {
    if (selection) pendingFocus.current = selection; setSelection(undefined); if (token) setSecret(token); state.reload();
  };
  const rows = state.data?.items.filter(item => `${item.displayName} ${item.eventKey}`.toLocaleLowerCase().includes(search.toLocaleLowerCase())) ?? [];
  return <Flex ref={collection} vertical gap="var(--ac-space-section)">
    <Flex justify="end"><Button ref={reloadButton} disabled={!!selection} onClick={refresh}>Reload Events</Button></Flex>
    {secret && <Alert type="success" showIcon title="Save this new webhook token now" description={<Flex className="access-bounded-form" vertical gap="var(--ac-space-compact)">
      <Typography.Text>{secret.source.displayName} · {context.tenant.name}. This token is shown only for this successful create or rotation. Configure the external sender, then dismiss it.</Typography.Text>
      <Input.Password aria-label="New webhook token" value={secret.token} readOnly autoComplete="off" />
      <Typography.Text type="secondary">Send it in the Authorization Bearer header. It grants receipt admission to this source only.</Typography.Text>
      <Flex wrap gap="var(--ac-space-compact)"><Button onClick={() => { if (!navigator.clipboard?.writeText) { message.error('Copy is unavailable. Reveal and select the token to save it.'); return; } void navigator.clipboard.writeText(secret.token).then(() => { message.success('Token copied. Store it with the external sender.'); }).catch(() => { message.error('Copy is unavailable. Reveal and select the token to save it.'); }); }}>Copy token</Button><Button onClick={() => setSecret(undefined)}>Dismiss token</Button></Flex>
    </Flex>} />}
    <AgentConfigurationPanel title="Webhook connections" label="Webhook connections" description="Receive bounded external data for this organization. An accepted receipt is separate from Automation matching, work admission and completion." bodyGap="section">
      {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={refresh} /> : state.data && <>
        <AdminCollectionToolbar label="Webhook connections on this page" value={search} onChange={setSearch} showScrollHint={false} filters={<Button ref={createButton} type="primary" disabled={!!selection || !state.data.canCreate} onClick={event => setSelection({ opener: event.currentTarget })}>New webhook</Button>} />
        <Table<TenantWebhook> className="admin-collection-table" size="small" tableLayout="fixed" rowKey="resourceId" pagination={{ ...adminCollectionPagination, total: state.data.total, current: page + 1, pageSize: 10, onChange: current => setPage(current - 1), showSizeChanger: false }} dataSource={rows}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching readable sources on this page.' : 'No readable webhook connections.'} /> }}
          columns={[{ title: 'Source', render: (_, source) => <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong className="admin-instance-id">{source.displayName}</Typography.Text><Typography.Text className="admin-instance-id">{source.eventKey}</Typography.Text><Flex wrap gap="var(--ac-space-compact)"><Tag>{source.status}</Tag><Typography.Text type="secondary">Revision {source.revision}</Typography.Text></Flex><Flex data-feedback-actions wrap gap="var(--ac-space-compact)"><Button data-webhook-id={source.resourceId} disabled={!!selection} onClick={event => setSelection({ id: source.resourceId, opener: event.currentTarget })}>Inspect webhook</Button></Flex></Flex> }]} />
      </>}
    </AgentConfigurationPanel>
    <AgentConfigurationPanel title="Built-in event catalog" label="Built-in event catalog" description="Shared read-only event definitions. This catalog grants no access to actual event data or subscriptions." bodyGap="section">
      {!context.permissions.includes('core-events.read') ? <Typography.Text type="secondary">Current built-in event read access is required.</Typography.Text> : builtins.loading ? <AccessLoading /> : builtins.error ? <AccessError error={builtins.error} retry={builtins.reload} /> : <Collapse items={(builtins.data?.items ?? []).map(item => ({ key: item.key, label: item.key, children: <Typography.Paragraph>{item.description}</Typography.Paragraph> }))} />}
    </AgentConfigurationPanel>
    {selection && <WebhookEditor key={`${tenant}:${selection.id ?? 'new'}`} tenant={tenant} organization={context.tenant.name} id={selection.id} securityRevision={state.data?.securityRevision ?? context.revision}
      close={close} changed={changed} clearToken={() => setSecret(undefined)} retired={() => { if (selection) pendingFocus.current = selection; setSecret(undefined); setSelection(undefined); state.reload(); }} />}
  </Flex>;
}

function WebhookEditor({ tenant, organization, id, securityRevision, close, changed, clearToken, retired }: {
  tenant: string; organization: string; id?: string; securityRevision: number; close: () => void; changed: (token?: TenantWebhookToken) => void; clearToken: () => void; retired: () => void;
}) {
  const { modal, message } = App.useApp(); const layout = useAdminDetailLayout(); const command = useTenantCommand();
  const state = useIdentityQuery<TenantWebhook>(id ? tenantWebhookApi(tenant, `/${id}`) : null);
  const [name, setName] = useState(''); const [key, setKey] = useState(''); const [unknown, setUnknown] = useState(false); const [attempted, setAttempted] = useState(false);
  const initialized = useRef(false); const [confirming, setConfirming] = useState(false); const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null); const alive = useRef(false);
  const current = state.data; const busy = command.busy || confirming; const dirty = id ? !!current && name !== current.displayName : !!name || !!key;
  const guard = useAccessDraftGuard(dirty, busy, close);
  useEffect(() => { alive.current = true; return () => { alive.current = false; confirmation.current?.destroy(); }; }, []);
  useEffect(() => { if (current && !initialized.current) { initialized.current = true; setName(current.displayName); setKey(current.eventKey); } if (current) setAttempted(false); }, [current]);
  useEffect(() => { if (lostRead(state.error) || lostRead(command.error)) retired(); }, [state.error, command.error, retired]);
  const editable = !id || !!current?.canManage; const blocked = busy || attempted || unknown || command.requiresReload || !!state.error || !!id && (state.loading || !current);
  const valid = name.trim().length > 0 && name.trim().length <= 80 && !/[\u0000-\u001f\u007f-\u009f]/.test(name) && (!!id || /^[a-z](?:[a-z0-9._-]{0,62}[a-z0-9])?$/.test(key));
  const reload = () => { if (busy) return; command.clearError(); state.reload(); };
  const acknowledge = (token?: TenantWebhookToken) => { guard.release(); setName(''); setKey(''); changed(token); message.success(token ? 'Source saved. Store the newly issued token now.' : 'Webhook source saved. Receipt facts remain separate.'); };
  const save = () => {
    if (blocked || !valid || !editable || id && !dirty) return; setAttempted(true); clearToken();
    void command.run(async signal => {
      try {
        if (!id) return createTenantWebhook(tenant, name.trim(), key, securityRevision, signal);
        requireWebhook(await identityRequest<TenantWebhook>(tenantWebhookApi(tenant, `/${id}`), { method: 'PUT', signal, body: { displayName: name.trim(), expectedRevision: current!.securityRevision, expectedSourceRevision: current!.revision } }), id); return undefined;
      } catch (error) { if (uncertain(error)) setUnknown(true); throw error; }
    }, acknowledge);
  };
  const action = (rotate: boolean) => {
    if (!current || !id || blocked || dirty || !current.canManage) return; setConfirming(true);
    const restore = rotate && current.status === 'revoked';
    confirmation.current = confirmAction(modal, { title: restore ? 'Activate this webhook with a new token?' : rotate ? 'Rotate this webhook token?' : 'Revoke this webhook?', okText: restore ? 'Activate source' : rotate ? 'Rotate token' : 'Revoke webhook', danger: true,
      content: `${current.displayName} · ${organization}. ${restore ? 'Reactivate incoming receipts for this exact source using a newly issued token. Previous tokens remain invalid. Save the new token and reconfigure the external sender. Existing receipts remain separate from execution.' : rotate ? 'Invalidate the current token immediately and show a new token once. Reconfigure the external sender. Existing receipt facts remain separate.' : 'Reject all new incoming receipts for this source. Saved receipts remain readable under current access. This does not cancel already admitted work.'}`,
      afterClose: () => { confirmation.current = null; if (alive.current) setConfirming(false); },
      onOk: () => {
        if (!alive.current) return; setAttempted(true); clearToken();
        return command.run(async signal => {
          try {
            const body = { expectedRevision: current.securityRevision, expectedSourceRevision: current.revision };
            if (rotate) return requireWebhookToken(await identityRequest<TenantWebhookToken>(tenantWebhookApi(tenant, `/${id}/rotate`), { method: 'POST', signal, body }), id);
            requireWebhook(await identityRequest<TenantWebhook>(tenantWebhookApi(tenant, `/${id}/revoke`), { method: 'POST', signal, body }), id); return undefined;
          } catch (error) { if (uncertain(error)) setUnknown(true); throw error; }
        }, acknowledge);
      } });
  };
  return <Drawer open title={id ? 'Webhook details' : 'New webhook'} aria-label={id ? 'Webhook details' : 'New webhook'} className="access-editor-drawer" size={640} styles={{ wrapper: { maxWidth: '100vw' } }} destroyOnHidden
    onClose={guard.close} closable={!busy} keyboard={!busy} mask={{ closable: !busy }} footer={<Flex wrap justify="end" gap="var(--ac-space-compact)">
      {id && <Button disabled={busy} onClick={reload}>Reload webhook</Button>}<Button disabled={busy} onClick={guard.close}>{dirty ? 'Discard changes' : 'Close details'}</Button>
      {editable && <Button type="primary" disabled={blocked || !valid || !!id && !dirty} loading={command.busy} onClick={save}>{id ? 'Save name' : 'Create webhook'}</Button>}
    </Flex>}>
    {guard.holder}
    <Flex vertical gap="var(--ac-space-section)">
      <Typography.Text type="secondary">{organization}</Typography.Text>
      {command.error !== undefined && <AccessError error={command.error} retry={id ? reload : undefined} />}
      {unknown && <Alert type="warning" showIcon title="Check current source before another action" description="The change may have committed. The token cannot be recovered by Reload, and this action will not be submitted again. Close this view, inspect the current source, then deliberately start a new action if needed." />}
      {state.error ? <AccessError error={state.error} retry={reload} /> : id && (state.loading || !current) ? <AccessLoading /> : <>
        {current && <Flex wrap gap="var(--ac-space-compact)"><Tag>{current.status}</Tag><Typography.Text type="secondary">Revision {current.revision}</Typography.Text>{!current.canManage && <Typography.Text>Read-only source</Typography.Text>}</Flex>}
        <Form className="admin-config-form" layout="vertical" disabled={busy || !editable}>
          <Form.Item label="Webhook name" extra="A readable name for this organization."><Input aria-label="Webhook name" maxLength={80} value={name} onChange={event => setName(event.target.value)} /></Form.Item>
          <Form.Item label="Event key" extra={id ? 'The saved key is immutable. Renaming a source does not change it.' : 'Lowercase letters, digits, dots, hyphens and underscores; start with a letter and end with a letter or digit.'}><Input aria-label="Webhook event key" value={key} maxLength={64} disabled={!!id || busy || !editable} onChange={event => setKey(event.target.value)} /></Form.Item>
        </Form>
        {current && <>
          <Descriptions {...layout} column={1} size="small" items={[{ key: 'ingress', label: 'Ingress address', children: <Typography.Text copyable className="admin-instance-id">{new URL(current.ingressPath, window.location.origin).href}</Typography.Text> },
            { key: 'created', label: 'Created', children: date(current.createdAtUtc) }, { key: 'updated', label: 'Updated', children: date(current.updatedAtUtc) }]} />
          <Collapse items={[{ key: 'payload', label: 'Sender payload', children: <Flex vertical gap="var(--ac-space-compact)">
            <Typography.Text>POST JSON to the ingress address with an Authorization Bearer header containing the saved token.</Typography.Text>
            <Input.TextArea aria-label="Webhook sender payload example" readOnly autoSize={{ minRows: 4, maxRows: 8 }} value={JSON.stringify({ eventId: 'sender-stable-id', data: { orderReference: 'order-123' } }, null, 2)} />
            <Typography.Text type="secondary">Use a stable eventId of 1–64 printable characters and a data object up to 4 KiB; the full request is limited to 8 KiB. An optional occurredAt must be a UTC timestamp. Reusing an ID with the same saved data returns its original receipt; changed data conflicts. The payload cannot choose a tenant, principal, target or execution root.</Typography.Text>
          </Flex> }]} />
          <Typography.Text type="secondary">The saved token cannot be read here. Incoming data is untrusted evidence and grants no Agent execution authority.</Typography.Text>
          {current.canManage && <Flex data-feedback-actions wrap gap="var(--ac-space-compact)"><Button disabled={blocked || dirty} onClick={() => action(true)}>{current.status === 'revoked' ? 'Issue new token and activate' : 'Rotate token'}</Button><Button danger disabled={blocked || dirty || current.status === 'revoked'} onClick={() => action(false)}>Revoke webhook</Button></Flex>}
          <WebhookReceipts tenant={tenant} id={current.resourceId} revision={current.revision} retired={retired} />
        </>}
      </>}
    </Flex>
  </Drawer>;
}

function WebhookReceipts({ tenant, id, revision, retired }: { tenant: string; id: string; revision: number; retired: () => void }) {
  const [page, setPage] = useState(0); const state = useIdentityQuery<TenantWebhookReceipts>(tenantWebhookApi(tenant, `/${id}/receipts?page=${page}&pageSize=10`), String(revision));
  const [evidence, setEvidence] = useState<string>(); useEffect(() => { if (lostRead(state.error)) { setEvidence(undefined); retired(); } }, [state.error, retired]);
  return <AgentConfigurationPanel title="Accepted receipts" label="Accepted webhook receipts" description="Saved incoming data only. Receipt acceptance does not confirm a matched subscription, admitted Run or completed result." bodyGap="section">
    <Flex wrap justify="end" gap="var(--ac-space-compact)"><Button onClick={() => { setEvidence(undefined); state.reload(); }}>Reload receipts</Button></Flex>
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <>
      <Table className="admin-collection-table" size="small" tableLayout="fixed" rowKey="eventId" dataSource={state.data?.items ?? []} pagination={{ ...adminCollectionPagination, total: state.data?.total ?? 0, current: page + 1, pageSize: 10, onChange: current => { setEvidence(undefined); setPage(current - 1); }, showSizeChanger: false }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No accepted receipts for this source." /> }} columns={[{ title: 'Receipt', render: (_, item) => <Flex vertical gap="var(--ac-space-compact)"><Typography.Text className="admin-instance-id">{item.sourceEventId}</Typography.Text><Typography.Text type="secondary">Occurred {date(item.occurredAtUtc)} · Accepted {date(item.admittedAtUtc)} · source revision {item.sourceRevision}</Typography.Text><Flex data-feedback-actions wrap gap="var(--ac-space-compact)"><Button onClick={() => setEvidence(evidence === item.eventId ? undefined : item.eventId)}>Inspect receipt data</Button></Flex>{evidence === item.eventId && <Input.TextArea aria-label="Untrusted webhook receipt data" value={JSON.stringify(item.data, null, 2)} readOnly autoSize={{ minRows: 3, maxRows: 12 }} />}</Flex> }]} />
    </>}
  </AgentConfigurationPanel>;
}
