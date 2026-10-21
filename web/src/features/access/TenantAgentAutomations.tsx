import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, App, Button, Checkbox, Collapse, Descriptions, Drawer, Empty, Flex, Form, Grid, Input, Pagination, Select, Table, Tabs, Tag, Typography, theme } from 'antd';
import { followAppLink } from '../../app/appLink';
import { confirmAction } from '../../app/confirmAction';
import { IdentityCompletionError, IdentityError, identityRequest, tenantApi, type TenantContext } from '../../services/identity';
import { automationDraft, automationWitness, tenantAutomationConversations, newTenantAutomationEvent, validTenantAutomationEvents, saveTenantAutomation, tenantAutomationsApi, type TenantAutomationDraft, type TenantAutomationEditor, type TenantAutomationItem } from '../../services/tenantAutomations';
import type { listModels } from '../../services/api';
import { AdminSessionPicker } from '../admin/AdminSessionPicker';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { AdminCollectionToolbar } from '../admin/AdminCollectionToolbar';
import { AutomationScheduleFields, defaultAutomationTiming, validAutomationTiming } from '../admin/AutomationScheduleFields';
import { ExecutionModelFields } from '../admin/ExecutionModelFields';
import { scheduleTimingLabel } from '../admin/AutomationScheduleFields';
import { AccessError, AccessLoading } from './AccessFeedback';
import { TenantAutomationEventFields } from './TenantAutomationEventFields';
import { TenantAutomationEventActivity } from './TenantAutomationEventActivity';
import { TenantAgentHeading } from './TenantAgentHeading';
import { tenantAccessPath, tenantAgentPath, tenantApprovalPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

const date = (value: string | null) => value ? new Date(value).toLocaleString() : 'Not scheduled';
const nextOccurrence = (value: { status: string; nextOccurrenceAtUtc: string | null }) => value.status === 'active' ? date(value.nextOccurrenceAtUtc) : 'Not scheduled';
const lostRead = (error: unknown) => error instanceof IdentityError && [401, 403, 404].includes(error.status);
type Selection = { id?: string; opener: HTMLElement };
export function TenantAgentAutomations({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const { modal, message } = App.useApp(); const command = useTenantCommand();
  const collection = useRef<HTMLDivElement>(null); const newButton = useRef<HTMLButtonElement>(null); const reloadButton = useRef<HTMLButtonElement>(null);
  const pendingFocus = useRef<{ id?: string; opener: HTMLElement } | null>(null);
  const [page, setPage] = useState(0); const [search, setSearch] = useState(''); const [selection, setSelection] = useState<Selection>();
  const [attempted, setAttempted] = useState(false); const [unknown, setUnknown] = useState(false); const [admitted, setAdmitted] = useState<string>();
  const [approval, setApproval] = useState<string>();
  const [confirming, setConfirming] = useState(false); const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  const state = useIdentityQuery<TenantAutomationEditor>(tenantAutomationsApi(tenant, id, `/editor?page=${page}&pageSize=10`));
  const view = state.data; const alive = useRef(false); const busy = command.busy || confirming;
  useAccessDraftGuard(false, busy, () => {});
  useEffect(() => { alive.current = true; return () => { alive.current = false; confirmation.current?.destroy(); }; }, []);
  useEffect(() => {
    if (!state.data) return; setAttempted(false);
    if (pendingFocus.current) {
      const target = pendingFocus.current;
      const row = [...(collection.current?.querySelectorAll<HTMLButtonElement>('[data-automation-id]') ?? [])].find(button => button.dataset.automationId === target.id);
      (row ?? (target.opener.isConnected ? target.opener : newButton.current && !newButton.current.disabled ? newButton.current : reloadButton.current))?.focus(); pendingFocus.current = null;
    }
  }, [state.data]);
  useEffect(() => { if (lostRead(state.error)) { setSelection(undefined); setAdmitted(undefined); setApproval(undefined); } }, [state.error]);
  const reload = () => { if (busy) return; command.clearError(); state.reload(); };
  const close = () => { const opener = selection?.opener; setSelection(undefined); if (opener?.isConnected) opener.focus(); };
  const blocked = busy || attempted || unknown || command.requiresReload || !!selection;
  function act(item: TenantAutomationItem, run: boolean) {
    if (!view || blocked || (run ? !item.canRun : !item.canCancel)) return;
    const value = item.automation;
    setConfirming(true);
    confirmation.current = confirmAction(modal, { title: run ? 'Run saved Automation now?' : 'Cancel this Automation?', okText: run ? 'Run now' : 'Cancel Automation', danger: !run,
      content: `${value.name} · ${context.tenant.name}. ${run ? 'Run the saved instructions in separate Background work. Admission is not completion.' : 'Stop future occurrences permanently. Already admitted work keeps its own Run and must be stopped separately.'}`,
      afterClose: () => { confirmation.current = null; if (alive.current) setConfirming(false); },
      onOk: () => {
        if (!alive.current) return; setAttempted(true);
        void command.run(async signal => {
          try {
            if (run) {
              const result = await identityRequest<{ runId: string | null; sessionId: string | null; approvalRequestId?: string | null }>(tenantAutomationsApi(tenant, id, `/${value.automationId}/run`), { method: 'POST', signal, body: automationWitness(view, value) });
              if (result?.approvalRequestId && !result.runId && !result.sessionId) return { approval: result.approvalRequestId };
              if (!result?.runId || !result.sessionId) throw new IdentityCompletionError();
              return { run: result.runId };
            }
            const witness = new URLSearchParams(Object.entries(automationWitness(view, value)).map(([key, number]) => [key, String(number)]));
            await identityRequest(tenantAutomationsApi(tenant, id, `/${value.automationId}?${witness}`), { method: 'DELETE', signal }); return undefined;
          } catch (error) { if (!(error instanceof IdentityError)) setUnknown(true); throw error; }
        }, result => { if (result?.run) { setAdmitted(result.run); setApproval(undefined); } if (result?.approval) setApproval(result.approval); message.success(run ? result?.approval ? 'Occurrence is waiting for approval. No work has been created.' : 'Run admitted. Open Activity for its outcome.' : 'Automation cancelled. Existing work remains separate.'); state.reload(); });
      } });
  }
  const rows = view?.items.filter(item => `${item.automation.name} ${item.automation.instructions}`.toLocaleLowerCase().includes(search.toLocaleLowerCase())) ?? [];
  return <Flex ref={collection} vertical gap="var(--ac-space-section)">
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to instances</Typography.Link><Button ref={reloadButton} disabled={busy || !!selection} onClick={reload}>Reload Automations</Button></Flex>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {unknown && <Alert type="warning" showIcon title="Check the outcome before another action" description="This action may have been admitted. Inspect current Automations and Activity before leaving and starting another deliberate action. Reload never repeats it." />}
    {approval && <Alert type="info" showIcon title="Waiting for admission approval" description={<><Typography.Paragraph>No Session or Run has been created for this occurrence. Approval and admission remain separate steps.</Typography.Paragraph><Typography.Link href={tenantApprovalPath(tenant, approval)} onClick={followAppLink}>Review the exact admission request</Typography.Link></>} />}
    {admitted && <Alert type="success" showIcon title="Run admitted" description={<Typography.Link href={`${tenantAgentPath(tenant, id, 'activity-runs')}?run=${encodeURIComponent(admitted)}`} onClick={followAppLink}>Open the admitted Run in Activity</Typography.Link>} />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : view && <>
      <TenantAgentHeading view={{ agent: view.agent, canUpdate: view.canCreate || view.items.some(i => i.canUpdate || i.canCancel || i.canRun) }} readOnlyDescription="Automation actions require independent access on the exact registration." />
      <AgentConfigurationPanel title="Automations" label="Automations" description="Run saved tasks from a schedule, received events, or an explicit action. Results stay in Background work, with an optional report to a selected conversation." bodyGap="section">
        <AdminCollectionToolbar label="Automations on this page" value={search} onChange={setSearch} showScrollHint={false} filters={<Button ref={newButton} type="primary" disabled={blocked || !view.canCreate} onClick={event => setSelection({ opener: event.currentTarget })}>New Automation</Button>} />
        <Table<TenantAutomationItem> className="admin-collection-table" size="small" tableLayout="fixed" rowKey={item => item.automation.automationId} dataSource={rows} pagination={false}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching readable Automations on this page.' : 'No readable Automations on this page.'} /> }} columns={[{ title: 'Saved task and triggers', render: (_, item) => {
            const a = item.automation; return <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong className="admin-instance-id">{a.name}</Typography.Text><Typography.Paragraph className="admin-instance-id" style={{ margin: 0 }}>{a.instructions}</Typography.Paragraph>
              <Flex wrap gap="var(--ac-space-compact)"><Tag>{a.status}</Tag><Typography.Text type="secondary">{a.triggers[0]?.schedule ? scheduleTimingLabel(a.triggers[0].schedule) : `${a.triggers.length} event subscriptions`}</Typography.Text></Flex>
              <Typography.Text type="secondary">Next: {nextOccurrence(a)} · Occurrences: {a.occurrenceCount}</Typography.Text>
              {a.suspensionReason && <Typography.Text type="warning" className="admin-instance-id">{a.suspensionReason}</Typography.Text>}
              <Flex data-feedback-actions wrap gap="var(--ac-space-compact)"><Button data-automation-id={a.automationId} disabled={busy || !!selection} onClick={event => setSelection({ id: a.automationId, opener: event.currentTarget })}>{item.canUpdate ? 'Edit Automation' : 'Inspect Automation'}</Button>
                {item.canRun && <Button disabled={blocked} onClick={() => act(item, true)}>Run now</Button>}{item.canCancel && <Button danger disabled={blocked} onClick={() => act(item, false)}>Cancel Automation</Button>}</Flex>
            </Flex>;
          } }]} />
        {view.total > 10 && <Pagination size="small" aria-label="Automation pages" current={page + 1} pageSize={10} total={view.total} showSizeChanger={false} disabled={busy || !!selection} onChange={value => { setPage(value - 1); setSearch(''); }} />}
      </AgentConfigurationPanel>
    </>}
    {selection && <TenantAutomationDrawer key={selection.id ?? 'new'} context={context} instance={id} selection={selection} onClose={close} onReadLost={() => { pendingFocus.current = selection; close(); state.reload(); }} onSaved={savedId => { pendingFocus.current = { ...selection, id: savedId ?? selection.id }; close(); state.reload(); }} onUncertain={() => setUnknown(true)} />}
  </Flex>;
}

function TenantAutomationDrawer({ context, instance, selection, onClose, onReadLost, onSaved, onUncertain }: { context: TenantContext; instance: string; selection: Selection; onClose: () => void; onReadLost: () => void; onSaved: (savedId?: string) => void; onUncertain: () => void }) {
  const tenant = context.tenant.tenantId; const { token } = theme.useToken(); const { message } = App.useApp(); const command = useTenantCommand();
  const compact = !Grid.useBreakpoint().md; const [draft, setDraft] = useState<TenantAutomationDraft>(); const [baseline, setBaseline] = useState<TenantAutomationDraft>();
  const [sourcesReady, setSourcesReady] = useState(false); const [tab, setTab] = useState('configuration');
  const [attempted, setAttempted] = useState(false); const [unknown, setUnknown] = useState(false); const initialized = useRef(false);
  const state = useIdentityQuery<TenantAutomationEditor>(tenantAutomationsApi(tenant, instance, selection.id ? `/${selection.id}/editor` : '/editor?page=0&pageSize=1'));
  const models = useIdentityQuery<Awaited<ReturnType<typeof listModels>>>(tenantApi(tenant, '/chat/models'));
  const conversations = useMemo(() => tenantAutomationConversations(tenant), [tenant, state.data?.securityRevision]);
  const view = state.data; const item = view?.items[0]; const value = selection.id ? item?.automation : undefined;
  useEffect(() => {
    if (!state.data) return; setAttempted(false);
    if (!initialized.current) {
      const next = selection.id ? automationDraft(state.data.items[0]?.automation) : { enabled: false, name: '', instructions: '', schedule: defaultAutomationTiming(), modelKey: null, reasoningEffort: null, requiresTools: true, requiresVision: false };
      setDraft(next); setBaseline(next); initialized.current = true;
    }
  }, [state.data]);
  useEffect(() => { if (lostRead(state.error)) { setDraft(undefined); setBaseline(undefined); onReadLost(); } }, [state.error]);
  const dirty = !!draft && JSON.stringify(draft) !== JSON.stringify(baseline); const guard = useAccessDraftGuard(dirty, command.busy, onClose);
  const allowed = !!view && (selection.id ? !!item?.canUpdate : view.canCreate);
  const disabled = !allowed || command.busy || attempted || unknown || command.requiresReload || state.loading || !!state.error;
  const model = models.data?.models.find(m => m.key === (draft?.modelKey || models.data?.defaultKey));
  const modelValid = !!models.data && (!draft?.modelKey || !!model) && (!draft?.reasoningEffort || !!model?.supportedReasoningEfforts.includes(draft.reasoningEffort));
  const policy = view?.policy; const timing = draft?.schedule;
  const scheduleAllowed = !!view?.canSchedule && !!policy && !!timing && ({ oneShot: policy.allowOneShot, daily: policy.allowDaily, weekly: policy.allowWeekly, fixedInterval: policy.allowFixedInterval })[timing.kind]
    && (timing.kind === 'oneShot' || policy.allowIndefiniteRecurrence || !!timing.maxOccurrences || !!timing.endDate || !!timing.endAtUtc);
  const eventsMode = draft?.mode === 'events';
  const enabledAllowed = eventsMode ? !!draft?.events?.every(event => event.source?.kind === 'builtin' ? view?.canCoreEvents : event.source?.kind === 'webhook' && view?.canWebhookEvents) : scheduleAllowed;
  const triggerValid = eventsMode ? validTenantAutomationEvents(draft?.events) && sourcesReady : !!draft && validAutomationTiming(draft.schedule);
  const valid = !!draft && !!draft.name.trim() && draft.name.trim().length <= 120 && !!draft.instructions.trim() && draft.instructions.trim().length <= 2000 && triggerValid && modelValid && draft.reportToSessionId !== '' && (!draft.enabled || enabledAllowed);
  const update = (part: Partial<TenantAutomationDraft>) => setDraft(current => current ? { ...current, ...part } : current);
  const reload = () => { if (command.busy) return; command.clearError(); state.reload(); models.reload(); };
  function save() {
    if (!view || !draft || disabled || !dirty || !valid) return; setAttempted(true);
    void command.run(async signal => {
      try { return await saveTenantAutomation(tenant, instance, view, draft, value, signal); }
      catch (error) {
        if (error instanceof IdentityCompletionError) return;
        if (!(error instanceof IdentityError) && !signal.aborted) { setUnknown(true); onUncertain(); }
        throw error;
      }
    }, saved => { guard.release(); message.success('Automation saved. Reading current registrations.'); onSaved(saved?.automationId); });
  }
  const configuration = <>
        {value && <Descriptions column={1} size="small" items={[{ key: 'status', label: 'Status', children: value.status }, { key: 'next', label: 'Next occurrence', children: nextOccurrence(value) }, { key: 'count', label: 'Occurrences', children: value.occurrenceCount }]} />}
        {draft ? <Form layout="vertical" className="admin-config-form automation-editor-form"><Flex vertical gap={token.padding}>
          <Form.Item label="Name" required><Input aria-label="Automation name" maxLength={120} value={draft.name} disabled={disabled} onChange={e => update({ name: e.target.value })} /></Form.Item>
          <Form.Item label="Instructions" required><Input.TextArea aria-label="Automation instructions" maxLength={2000} autoSize={{ minRows: 3, maxRows: 10 }} value={draft.instructions} disabled={disabled} onChange={e => update({ instructions: e.target.value })} /></Form.Item>
          <Form.Item label="Trigger type"><Select<'events' | 'schedule'> aria-label="Automation trigger type" disabled={disabled} value={eventsMode ? 'events' : 'schedule'} options={[{ value: 'schedule', label: 'Schedule' }, { value: 'events', label: 'Events' }]} onChange={mode => update({ mode, ...(mode === 'events' && !draft.events?.length ? { events: [newTenantAutomationEvent()] } : {}) })} /></Form.Item>
          <Checkbox checked={draft.enabled} disabled={disabled} onChange={e => update({ enabled: e.target.checked })}>Enable automatic occurrences</Checkbox>
          {draft.enabled && !enabledAllowed && <Alert type="warning" showIcon title="The current Definition does not permit these triggers" description="Choose permitted sources or timing, or save the Automation disabled." />}
          {eventsMode ? <TenantAutomationEventFields tenant={tenant} instance={instance} automation={selection.id} events={draft.events ?? []} disabled={disabled} reloadKey={String(view?.securityRevision ?? 0)} onChange={events => update({ events })} onReady={setSourcesReady} /> : <AutomationScheduleFields timing={draft.schedule} setTiming={patch => update({ schedule: { ...draft.schedule, ...patch } })} disabled={disabled} policy={policy} />}
          <Flex component="section" vertical gap={token.padding} className="automation-editor-section" aria-label="Completion">
            <Typography.Title level={5} style={{ margin: 0 }}>Completion</Typography.Title>
            <Typography.Text type="secondary">Runs independently in Background work. A report is a separate delivery and may require approval.</Typography.Text>
            <Form.Item label="Report completion to"><Select aria-label="Automation completion report" disabled={disabled} value={draft.reportToSessionId == null ? 'none' : 'toSession'}
              options={[{ value: 'none', label: 'None' }, { value: 'toSession', label: 'Selected conversation' }]} onChange={kind => update({ reportToSessionId: kind === 'none' ? null : '' })} /></Form.Item>
            {draft.reportToSessionId != null && <Form.Item label="Report destination" extra="Choose an active conversation for this instance. Current access is checked again before delivery.">
              <AdminSessionPicker instanceId={instance} value={draft.reportToSessionId} disabled={disabled} eligibleOnly label="Report destination" source={conversations} onChange={reportToSessionId => update({ reportToSessionId })} />
            </Form.Item>}
          </Flex>
          <Collapse ghost items={[{ key: 'model', label: 'Execution options', children: <Flex vertical gap={token.padding}>
            {models.loading ? <AccessLoading /> : models.error ? <AccessError error={models.error} retry={models.reload} /> : <ExecutionModelFields models={models.data?.models ?? []} modelKey={draft.modelKey ?? ''} reasoningEffort={draft.reasoningEffort ?? ''} modelLabel="Automation execution model" effortLabel="Automation reasoning effort" defaultLabel="Unattended default" disabled={disabled} onChange={(key, effort) => update({ modelKey: key || null, reasoningEffort: effort || null })} />}
            {!modelValid && !models.loading && <Alert type="warning" showIcon title="Check the current execution model" description="Reload the model catalog, then select an available model and reasoning effort or use the unattended default." />}
            <Checkbox checked={draft.requiresTools} disabled={disabled} onChange={e => update({ requiresTools: e.target.checked })}>Task requires tools</Checkbox><Checkbox checked={draft.requiresVision} disabled={disabled} onChange={e => update({ requiresVision: e.target.checked })}>Task requires vision</Checkbox>
          </Flex> }]} />
          {dirty && <Typography.Text type="secondary">Unsaved changes. Reload keeps your draft and refreshes current witnesses.</Typography.Text>}
        </Flex></Form> : <Alert type="info" showIcon title="This saved trigger or destination requires its own editor" description={<Typography.Text className="admin-instance-id">{value?.name}: {value?.instructions}</Typography.Text>} />}
  </>;
  return <Drawer open title={selection.id ? 'Saved Automation' : 'New Automation'} size={compact ? '100%' : 640} rootClassName="admin-automation-drawer access-editor-drawer" onClose={guard.close} styles={{ body: { padding: token.padding } }}
    footer={<Flex data-feedback-actions wrap gap={token.paddingXS}>{tab === 'configuration' && <Button type="primary" aria-label="Save Automation" loading={command.busy} disabled={disabled || !dirty || !valid} onClick={save}>Save Automation</Button>}<Button disabled={command.busy} onClick={guard.close}>Close</Button><Button disabled={command.busy} onClick={reload}>Reload current Automation</Button></Flex>}>
    {guard.holder}<Flex vertical gap={token.padding}>
      {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
      {unknown && <Alert type="warning" showIcon title="Outcome unknown" description="The Automation may have been saved. Close this editor and check current registrations before starting another deliberate change. Reload does not repeat the save." />}
      {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : view && <>
        <Typography.Text type="secondary">{context.tenant.name} · {view.agent.instance.persona.name}</Typography.Text>
        {!allowed && <Alert type="info" showIcon title="Read-only Automation" description="Editing requires current update access on this registration and an active instance." />}
        {selection.id ? <Tabs activeKey={tab} onChange={setTab} items={[{ key: 'configuration', label: 'Configuration', children: configuration }, { key: 'activity', label: 'Event Activity', children: <TenantAutomationEventActivity tenant={tenant} instance={instance} automation={selection.id} /> }]} /> : configuration}
      </>}
    </Flex>
  </Drawer>;
}
