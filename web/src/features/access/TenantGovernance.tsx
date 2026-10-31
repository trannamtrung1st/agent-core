import { AgentConfigurationPanel, AgentSettingRow } from '../admin/AgentConfigurationLayout';
import { EvaluationGovernanceSettings } from './EvaluationGovernanceSettings';
import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Checkbox, Descriptions, Drawer, Empty, Flex, Form, Grid, InputNumber, Select, Table, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { IdentityCompletionError, IdentityError, identityRequest, tenantApi, type TenantContext } from '../../services/identity';
import { AdminCollectionToolbar, adminCollectionPagination } from '../admin/AdminCollectionToolbar';
import { permissionFamily, permissionLabel } from './Roles';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useTenantCommand } from './useTenantCommand';

type Mode = 'none' | 'whenRequesterLacksPermission' | 'always';
type Policy = { mode: Mode; independentReview: boolean };
export type GovernancePolicy = { permission: string; approvalClass: string; minimum: Policy; effective: Policy;
  policyRevision: number; securityRevision: number; humanOnly: boolean; delegatable: boolean; canManage: boolean };
type PolicyPage = { items: GovernancePolicy[]; total: number; page: number; pageSize: number; securityRevision: number };
const modes: Mode[] = ['none', 'whenRequesterLacksPermission', 'always'];
const labels: Record<Mode, string> = { none: 'None', whenRequesterLacksPermission: 'When requester lacks permission', always: 'Always review' };
const summary = (policy: Policy) => `${labels[policy.mode]}${policy.independentReview ? ' · Independent reviewer' : ''}`;
const same = (a: Policy, b: Policy) => a.mode === b.mode && a.independentReview === b.independentReview;
const permissionTitle = (key: string) => `${permissionFamily(key)} · ${permissionLabel(key)}`;

export function TenantGovernance({ context }: { context: TenantContext }) {
  const [search, setSearch] = useState(''); const [page, setPage] = useState(0); const [pageSize, setPageSize] = useState(20);
  const [selected, setSelected] = useState<string | null>(null); const opener = useRef<HTMLElement | null>(null);
  const { message } = App.useApp(); const screens = Grid.useBreakpoint();
  const state = useIdentityQuery<PolicyPage>(tenantApi(context.tenant.tenantId, `/operation-policies?page=${page}&pageSize=${pageSize}&q=${encodeURIComponent(search.trim())}`));
  const close = () => { setSelected(null); requestAnimationFrame(() => (opener.current?.isConnected ? opener.current : document.querySelector<HTMLElement>('[data-admin-page-heading]'))?.focus({ preventScroll: true })); };
  return <Flex vertical gap="var(--ac-space-section)">
    <EvaluationGovernanceSettings context={context} />
    <ChatRetentionSettings key={context.tenant.tenantId} context={context} />
    <Alert type="info" showIcon title="Review policy does not grant permission" description="Agents use their own authority. Organization policy can require more human review; Core minimums and people-only restrictions always apply." />
    <AdminCollectionToolbar label="operation permissions" showScrollHint={false} value={search} onChange={value => { setSearch(value.slice(0, 160)); setPage(0); }} filters={<Button disabled={state.loading} onClick={state.reload}>Refresh policies</Button>} />
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <Table<GovernancePolicy> className="admin-collection-table" rowKey="permission" dataSource={state.data?.items}
      pagination={{ ...adminCollectionPagination, current: page + 1, pageSize, total: state.data?.total ?? 0, onChange: (next, size) => { setPage(size === pageSize ? next - 1 : 0); setPageSize(size); } }}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No operation permissions match this search."><Button onClick={() => { setSearch(''); setPage(0); }}>Clear search</Button></Empty> }} columns={[
        { title: 'Permission', render: (_, item) => <Flex vertical gap="var(--ac-space-tight)"><Typography.Text strong>{permissionTitle(item.permission)}</Typography.Text><Typography.Text type="secondary">{item.permission}</Typography.Text>{!screens.md && <Typography.Text>{summary(item.effective)}</Typography.Text>}<Flex wrap gap="var(--ac-space-tight)"><Tag>{item.policyRevision === 0 ? 'Core default' : 'Organization setting'}</Tag>{item.humanOnly && <Tag>People only</Tag>}</Flex></Flex> },
        { title: 'Current review', responsive: ['md'], render: (_, item) => summary(item.effective) },
        { title: 'Action', width: 104, render: (_, item) => <Button aria-label={`Inspect ${permissionTitle(item.permission)}`} onClick={event => { opener.current = event.currentTarget; setSelected(item.permission); }}>Inspect</Button> }
      ]} />}
    {selected && <PolicyInspector key={`${context.tenant.tenantId}:${selected}`} context={context} permission={selected} onClose={close} onSaved={() => { close(); state.reload(); void message.success('Review policy saved. The next protected action checks current policy.'); }} />}
  </Flex>;
}

type ChatRetention = { savedChatRetentionDays: number | null; securityRevision: number; canManage: boolean };
function ChatRetentionSettings({ context }: { context: TenantContext }) {
  const url = tenantApi(context.tenant.tenantId, '/governance/chat-retention');
  const state = useIdentityQuery<ChatRetention>(url);
  const command = useTenantCommand(); const { message } = App.useApp();
  const [draft, setDraft] = useState<number | null | undefined>(undefined);
  const saved = state.data; const value = draft === undefined ? saved?.savedChatRetentionDays ?? null : draft;
  const editable = saved?.canManage && context.permissions.includes('tenant.governance.manage');
  const stale = command.requiresReload || command.error instanceof IdentityError && [403, 404, 409].includes(command.error.status);
  const valid = value === null || Number.isInteger(value) && value >= 1 && value <= 3650;
  return <AgentConfigurationPanel title="Saved Chat retention" label="Saved Chat retention" bodyGap="default" description="Organization default · pinned when each chat is created. Existing chats keep their policy. Temporary chats always expire after 24 hours.">
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : saved ? <>
      <div className="agent-settings-rows"><AgentSettingRow label={<label htmlFor="retention-forever">Keep saved chats forever</label>} description="Applies to new saved chats only."><Checkbox id="retention-forever" aria-label="Forever" checked={value === null} disabled={!editable || command.busy || stale} onChange={event => setDraft(event.target.checked ? null : 30)} /></AgentSettingRow>
        {value !== null ? <AgentSettingRow label={<label htmlFor="retention-days">Days after completion</label>}><Form.Item validateStatus={!valid ? 'error' : undefined} help={!valid ? 'Enter a whole number from 1 to 3650.' : undefined}>
          <InputNumber id="retention-days" aria-label="Saved Chat retention days" min={1} max={3650} precision={0} value={value} disabled={!editable || command.busy || stale} onChange={next => setDraft(next ?? 0)} />
        </Form.Item></AgentSettingRow> : null}</div>
      <Flex className="agent-settings-actions" wrap gap="var(--ac-space-compact)">
        <Button type="primary" loading={command.busy} disabled={!editable || stale || !valid || value === saved.savedChatRetentionDays}
          onClick={() => void command.run(signal => identityRequest<ChatRetention>(url, { method: 'PUT', signal, body: { savedChatRetentionDays: value, expectedRevision: saved.securityRevision } }), result => {
            if (result.savedChatRetentionDays !== value || result.securityRevision !== saved.securityRevision + 1) throw new IdentityCompletionError();
            setDraft(undefined); state.reload(); void message.success('Chat retention saved for future chats.');
          })}>Save retention</Button>
        <Button disabled={command.busy} onClick={() => { setDraft(undefined); command.clearError(); state.reload(); }}>Refresh retention</Button>
      </Flex>
      {!editable ? <Typography.Text type="secondary">Organization governance permission is required to change retention.</Typography.Text> : null}
    </> : null}
    {command.error ? <AccessError error={command.error} /> : null}
    {stale ? <Typography.Text type="secondary">Refresh retention to review the current policy before saving again.</Typography.Text> : null}
  </AgentConfigurationPanel>;
}

function PolicyInspector({ context, permission, onClose, onSaved }: { context: TenantContext; permission: string; onClose: () => void; onSaved: () => void }) {
  const state = useIdentityQuery<GovernancePolicy>(tenantApi(context.tenant.tenantId, `/operation-policies/${encodeURIComponent(permission)}`));
  if (state.loading || state.error || !state.data) return <Drawer open title="Review policy" aria-label="Review policy" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={onClose}>
    {state.error ? <AccessError error={state.error} retry={state.reload} /> : <AccessLoading />}
  </Drawer>;
  return <PolicyEditor key={`${state.data.securityRevision}:${state.data.policyRevision}`} context={context} saved={state.data} onClose={onClose} reload={state.reload} onSaved={onSaved} />;
}

function PolicyEditor({ context, saved, onClose, reload, onSaved }: { context: TenantContext; saved: GovernancePolicy; onClose: () => void; reload: () => void; onSaved: () => void }) {
  const [draft, setDraft] = useState<Policy>(saved.effective); const [confirming, setConfirming] = useState(false);
  const command = useTenantCommand(); const { modal } = App.useApp(); const screens = Grid.useBreakpoint();
  const active = useRef(true); const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  useEffect(() => { active.current = true; return () => { active.current = false; confirmation.current?.destroy(); }; }, []);
  const dirty = !same(draft, saved.effective); const busy = confirming || command.busy;
  const guard = useAccessDraftGuard(dirty, busy, onClose);
  const editable = saved.canManage && context.permissions.includes('tenant.governance.manage');
  const needsReload = command.requiresReload || command.error instanceof IdentityError && [403, 404, 409].includes(command.error.status);
  const valid = modes.indexOf(draft.mode) >= modes.indexOf(saved.minimum.mode) && (!saved.minimum.independentReview || draft.independentReview);
  const save = () => {
    if (!editable || !dirty || busy || needsReload || !valid) return;
    const value = { ...draft };
    const apply = () => command.run(async signal => {
      const reply = await identityRequest<GovernancePolicy>(tenantApi(context.tenant.tenantId, `/operation-policies/${encodeURIComponent(saved.permission)}`), {
        method: 'PUT', signal, body: { ...value, expectedRevision: saved.securityRevision, expectedPolicyRevision: saved.policyRevision }
      });
      if (!reply || reply.permission !== saved.permission || reply.policyRevision !== saved.policyRevision + 1 || reply.securityRevision !== saved.securityRevision + 1
        || !reply.effective || !same(reply.effective, value) || !reply.minimum || !same(reply.minimum, saved.minimum)) throw new IdentityCompletionError();
      return reply;
    }, () => { setDraft(saved.effective); guard.release(); onSaved(); });
    if (modes.indexOf(value.mode) < modes.indexOf(saved.effective.mode) || saved.effective.independentReview && !value.independentReview) {
      setConfirming(true);
      confirmation.current = confirmAction(modal, { title: 'Relax organization review policy?', okText: 'Save policy', cancelText: 'Keep editing', danger: true,
        content: <Flex vertical gap="var(--ac-space-compact)"><Typography.Text>{permissionTitle(saved.permission)} in {context.tenant.name}.</Typography.Text><Typography.Text>{summary(saved.effective)} → {summary(value)}.</Typography.Text><Typography.Text>Core minimums remain enforced. This change grants no permission and does not authorize a saved action.</Typography.Text></Flex>,
        onOk: apply, afterClose: () => { if (active.current) setConfirming(false); } });
    } else void apply();
  };
  const refresh = () => {
    if (busy) return;
    if (!dirty) { reload(); return; }
    setConfirming(true);
    confirmation.current = confirmAction(modal, { title: 'Discard changes and reload?', content: 'Reload the current saved policy. Your unsaved choices will be discarded.', okText: 'Discard and reload', cancelText: 'Keep editing', danger: true,
      onOk: reload, afterClose: () => { if (active.current) setConfirming(false); } });
  };
  return <Drawer open title="Review policy" aria-label="Review policy" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }}
    onClose={guard.close} closable={!busy} keyboard={!busy} mask={{ closable: !busy }} footer={<Flex justify="end" wrap gap="var(--ac-space-compact)">
      <Button disabled={busy} onClick={guard.close}>{editable ? 'Cancel' : 'Close'}</Button><Button disabled={busy} onClick={refresh}>Reload saved policy</Button>
      {editable && <Button type="primary" form="governance-policy-form" htmlType="submit" loading={command.busy} disabled={busy || !dirty || needsReload || !valid}>Save policy</Button>}
    </Flex>}>
    {guard.holder}<Flex vertical gap="var(--ac-space-section)">
      <Descriptions layout={screens.sm ? 'horizontal' : 'vertical'} column={1} size="small" items={[
        { key: 'organization', label: 'Organization', children: context.tenant.name },
        { key: 'permission', label: 'Permission', children: <Flex vertical gap="var(--ac-space-tight)"><Typography.Text strong>{permissionTitle(saved.permission)}</Typography.Text><Typography.Text type="secondary">{saved.permission}</Typography.Text></Flex> },
        { key: 'minimum', label: 'Core minimum', children: summary(saved.minimum) },
        { key: 'effective', label: 'Current review', children: summary(saved.effective) },
        { key: 'source', label: 'Policy source', children: saved.policyRevision === 0 ? 'Core default' : 'Organization setting' },
        { key: 'authority', label: 'Authority restrictions', children: saved.humanOnly ? 'People only. Agents cannot perform this action, including through approval.' : saved.delegatable ? 'Review grants no permission. Any exceptional authorization requires a separate eligible exact action.' : 'Not delegatable. Review requires the agent to already hold the underlying permission.' }
      ]} />
      {!editable && <Alert type="info" showIcon title="Read-only policy" description="Changing organization policy requires separate Governance manage access." />}
      {command.error !== undefined && <AccessError error={command.error} retry={refresh} />}
      <Form id="governance-policy-form" layout="vertical" disabled={!editable || busy || needsReload} onFinish={save}>
        <Form.Item label="Review mode" htmlFor="governance-review-mode" extra="None adds no requester-based review. Mandatory Core controls and the agent’s own permissions still apply.">
          <Select id="governance-review-mode" value={draft.mode} options={modes.map(mode => ({ value: mode, label: labels[mode], disabled: modes.indexOf(mode) < modes.indexOf(saved.minimum.mode) }))} onChange={mode => setDraft({ ...draft, mode })} />
        </Form.Item>
        <Form.Item extra="When review is required, the original requester cannot decide an independent review."><Checkbox checked={draft.independentReview} disabled={!editable || busy || needsReload || saved.minimum.independentReview} onChange={event => setDraft({ ...draft, independentReview: event.target.checked })}>Require an independent reviewer</Checkbox></Form.Item>
        {editable && <Button disabled={busy || needsReload || same(draft, saved.minimum)} onClick={() => setDraft(saved.minimum)}>Use Core minimum</Button>}
      </Form>
    </Flex>
  </Drawer>;
}
