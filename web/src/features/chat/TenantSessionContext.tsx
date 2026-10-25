import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Drawer, Flex, Form, Select, Typography } from 'antd';
import { IdentityCompletionError, IdentityError, identityRequest } from '../../services/identity';
import { sessionContextApi, type ContextOptions, type SessionContextView } from '../../services/tenantSessionContext';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { useAccessDraftGuard } from '../access/useAccessDraftGuard';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { useTenantEditorCommand } from '../access/useTenantEditorCommand';

export function TenantSessionContext({ tenant, session, refreshKey }: { tenant: string; session: string; refreshKey: string }) {
  const view = useIdentityQuery<SessionContextView>(sessionContextApi(tenant, session), refreshKey); const [open, setOpen] = useState(false); const [editingView, setEditingView] = useState<SessionContextView>(); const opener = useRef<HTMLButtonElement>(null);
  useEffect(() => { const refresh = () => view.reload(); window.addEventListener('focus', refresh); return () => window.removeEventListener('focus', refresh); }, [tenant, session]);
  useEffect(() => { if (view.error instanceof IdentityError && [401, 403, 404].includes(view.error.status)) { setOpen(false); setEditingView(undefined); } }, [view.error]);
  const close = () => { setOpen(false); requestAnimationFrame(() => opener.current?.focus()); };
  return <Flex vertical gap="var(--ac-space-compact)" className="conversation-column conversation-banners">
    <Flex wrap align="center" gap="var(--ac-space-compact)"><Button ref={opener} disabled={!view.data} onClick={() => { setEditingView(view.data); setOpen(true); }} aria-label="Session Space">Space: {view.data?.space?.name ?? (view.loading ? 'Loading…' : 'Unavailable')}</Button>
      {view.data?.space?.environmentsEnabled && <Typography.Text type="secondary">Environment: {view.data.environment?.name ?? (view.data.selection.environmentId ? 'Unavailable' : 'None')}</Typography.Text>}
      {view.data?.state === 'pending_next_turn' && <Typography.Text role="status" type="secondary">Pending next turn</Typography.Text>}
    </Flex>
    {view.error && <AccessError error={view.error} retry={view.reload} />}
    {view.data?.state === 'stale' && <Alert showIcon type="warning" title="Saved context is unavailable" description="Choose an available Space or clear the Environment before starting new work. Current Runs keep their original context." />}
    {open && (editingView ? <ContextEditor key={`${tenant}:${session}`} tenant={tenant} session={session} saved={editingView} onClose={close} onSaved={() => { close(); view.reload(); }} /> : <Drawer open title="Session context" onClose={close}>{view.error ? <AccessError error={view.error} retry={view.reload} /> : <AccessLoading />}</Drawer>)}
  </Flex>;
}
function ContextEditor({ tenant, session, saved, onClose, onSaved }: { tenant: string; session: string; saved: SessionContextView; onClose: () => void; onSaved: () => void }) {
  const [space, setSpace] = useState(saved.selection.spaceId); const [environment, setEnvironment] = useState<string | undefined>(saved.selection.environmentId ?? undefined); const [page, setPage] = useState(0); const [transition, setTransition] = useState(() => crypto.randomUUID());
  const optionsPath = sessionContextApi(tenant, session, '/options') + `?${space === saved.selection.spaceId ? '' : `spaceId=${encodeURIComponent(space)}&`}page=${page}`;
  const options = useIdentityQuery<ContextOptions>(optionsPath);
  const [refreshed, setRefreshed] = useState<{ path: string; value: ContextOptions }>();
  useEffect(() => setRefreshed(undefined), [optionsPath]);
  const command = useTenantEditorCommand(); const dirty = space !== saved.selection.spaceId || (environment ?? null) !== saved.selection.environmentId;
  const guard = useAccessDraftGuard(dirty, command.busy, onClose); const current = refreshed?.path === optionsPath ? refreshed.value : options.data;
  const choices = current?.spaces.map(s => ({ value: s.spaceId, label: `${s.name} · ${s.kind}` })) ?? [];
  if (!choices.some(s => s.value === space)) choices.unshift({ value: space, label: space === saved.selection.spaceId ? saved.space?.name ?? 'Saved Space unavailable' : 'Selected Space' });
  return <>{guard.holder}<Drawer open title="Session context" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} onClose={guard.close} keyboard={!command.busy} maskClosable={!command.busy} closable={!command.busy}
    footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={guard.close}>Cancel</Button><Button type="primary" loading={command.busy} disabled={!current?.canSwitch || !dirty || command.blocked || !!options.error || environment !== undefined && !current.environmentSelectionAllowed} onClick={() => current && void command.save(async signal => {
      const result = await identityRequest<{ selection: SessionContextView['selection']; state: string; sessionRevision: number; securityRevision: number }>(sessionContextApi(tenant, session, '/switch'), { method: 'POST', signal, body: { transitionId: transition, spaceId: space, environmentId: environment ?? null, expectedRevision: current.securityRevision, expectedSessionRevision: current.sessionRevision, expectedSelectionRevision: current.selectionRevision } });
      if (!result?.selection || result.selection.tenantId !== tenant || result.selection.transitionId !== transition || result.selection.spaceId !== space || result.selection.environmentId !== (environment ?? null)
        || result.selection.revision !== current.selectionRevision + 1 || !['applied', 'pending_next_turn'].includes(result.state)
        || result.sessionRevision !== current.sessionRevision + 1 || result.securityRevision !== current.securityRevision + 1) throw new IdentityCompletionError(false);
      return result;
    }, onSaved)}>Apply to next turn</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)"><Typography.Paragraph>Changing context keeps this conversation and its agent. Current Runs retain their original Space, files and Environment. Idle sandbox mounts are retired before a switch; executing or unsettled sandbox work must finish first.</Typography.Paragraph>
      {command.error !== undefined && <AccessError error={command.error} />}{options.error && <AccessError error={options.error} />}{command.unknown && <Alert showIcon type="warning" title="Context outcome is unconfirmed" description="Read the saved Session context before another deliberate change. This transition will not be repeated." />}
      <Button style={{ alignSelf: 'flex-start' }} disabled={command.busy || command.requiresReload} onClick={() => void command.refresh(signal => identityRequest<ContextOptions>(optionsPath, { signal }), value => setRefreshed({ path: optionsPath, value }))}>Reload context choices</Button>
      {!current && !options.error ? <AccessLoading /> : current && <>
        {!current.canSwitch && <Alert showIcon type="info" title="Context selection is read-only" description="This Session cannot be changed under its current lifecycle or your access." />}
        <Form layout="vertical" disabled={command.busy || !current.canSwitch}><Form.Item label="Space"><Select aria-label="Space" autoFocus showSearch optionFilterProp="label" value={space} options={choices} onChange={value => { setSpace(value); setEnvironment(undefined); setTransition(crypto.randomUUID()); }} /></Form.Item>
          <Flex wrap gap="var(--ac-space-compact)"><Button disabled={command.busy || !page} onClick={() => setPage(p => p - 1)}>Previous Spaces</Button><Button disabled={command.busy || !current.hasMore} onClick={() => setPage(p => p + 1)}>More Spaces</Button></Flex>
          {current.environmentsEnabled && <Form.Item label="Environment" style={{ marginTop: 'var(--ac-space-section)' }} help="Changing Space clears the Environment. Selecting a profile affects future Runs only."><Select aria-label="Environment" allowClear placeholder="No active Environment" disabled={command.busy || !current.environmentSelectionAllowed} value={environment} options={current.environments.map(e => ({ value: e.environmentId, label: e.name }))} onChange={value => { setEnvironment(value); setTransition(crypto.randomUUID()); }} /></Form.Item>}
          {current.canClearEnvironment && environment && <Button disabled={command.busy} onClick={() => { setEnvironment(undefined); setTransition(crypto.randomUUID()); }}>Clear saved Environment</Button>}
        </Form>
      </>}
    </Flex>
  </Drawer></>;
}
