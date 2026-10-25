import { useEffect, useState } from 'react';
import { Alert, Button, Flex, Form, Select, Typography } from 'antd';
import { tenantApi } from '../../services/identity';
import type { ContextOptions, SessionContextView } from '../../services/tenantSessionContext';
import { sessionContextApi } from '../../services/tenantSessionContext';
import type { TenantAutomationDraft } from '../../services/tenantAutomations';
import { useIdentityQuery } from './useIdentityQuery';
import { AccessError, AccessLoading } from './AccessFeedback';

type ExecutionOptions = Pick<ContextOptions, 'spaces' | 'page' | 'hasMore' | 'environmentsEnabled' | 'environmentSelectionAllowed' | 'environments'> & { targetAvailable: boolean };
export function TenantAutomationContextFields({ tenant, instance, draft, disabled, reloadKey, onChange, onReady }: { tenant: string; instance: string; draft: TenantAutomationDraft; disabled: boolean; reloadKey: string; onChange: (value: Partial<TenantAutomationDraft>) => void; onReady: (ready: boolean) => void }) {
  const [page, setPage] = useState(0); const space = draft.pinnedSpaceId; const environment = draft.pinnedEnvironmentId;
  const state = useIdentityQuery<ExecutionOptions>(tenantApi(tenant, `/agents/${encodeURIComponent(instance)}/context-options?${new URLSearchParams({ page: String(page), ...(space ? { spaceId: space } : {}) })}`), reloadKey);
  const validSession = !!draft.targetSessionId && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(draft.targetSessionId);
  const conversation = useIdentityQuery<SessionContextView>(validSession ? sessionContextApi(tenant, draft.targetSessionId!) : null, reloadKey);
  const selected = conversation.data; const actualSpace = space ?? (selected?.space?.kind === 0 ? selected.selection.spaceId : undefined);
  const matches = !draft.targetSessionId || selected?.state !== 'stale' && !!selected && actualSpace === selected.selection.spaceId && (environment ?? null) === selected.selection.environmentId;
  const pinAvailable = !space || !!state.data?.targetAvailable && (!environment || state.data.environmentSelectionAllowed && state.data.environments.some(e => e.environmentId === environment));
  const ready = !!state.data && !state.error && pinAvailable && matches;
  useEffect(() => { onReady(ready); }, [ready, onReady]);
  const choices = state.data?.spaces.map(s => ({ value: s.spaceId, label: `${s.name} · ${s.kind}` })) ?? [];
  if (space && !choices.some(c => c.value === space)) choices.unshift({ value: space, label: state.loading ? 'Loading pinned Space…' : 'Pinned Space · unavailable or on another page' });
  return <Flex vertical gap="var(--ac-space-default)">
    <Typography.Text strong>Execution context</Typography.Text><Typography.Paragraph type="secondary" style={{ margin: 0 }}>Without pins, independent work uses the agent’s Private Space with no Environment. Pins select exact saved identities for every occurrence. Report destinations remain separate.</Typography.Paragraph>
    {state.error && <AccessError error={state.error} retry={state.reload} />}
    <Form.Item label="Pinned Space" style={{ margin: 0 }}><Select aria-label="Automation pinned Space" allowClear placeholder="Agent Private Space · no Environment" showSearch optionFilterProp="label" value={space ?? undefined} loading={state.loading} disabled={disabled} options={choices} onChange={value => onChange({ pinnedSpaceId: value ?? null, pinnedEnvironmentId: null })} /></Form.Item>
    <Flex wrap gap="var(--ac-space-compact)"><Button disabled={disabled || !page} onClick={() => setPage(p => p - 1)}>Previous Spaces</Button><Button disabled={disabled || !state.data?.hasMore} onClick={() => setPage(p => p + 1)}>More Spaces</Button><Button disabled={disabled} onClick={state.reload}>Reload context choices</Button></Flex>
    {space && state.data?.environmentsEnabled && <Form.Item label="Pinned Environment" style={{ margin: 0 }}><Select aria-label="Automation pinned Environment" allowClear placeholder="No Environment" value={environment ?? undefined} disabled={disabled || !state.data.environmentSelectionAllowed} options={state.data.environments.map(e => ({ value: e.environmentId, label: e.name }))} onChange={value => onChange({ pinnedEnvironmentId: value ?? null })} /></Form.Item>}
    {space && !pinAvailable && !state.loading && <Alert type="warning" showIcon title="Saved context is unavailable" description="Choose an available Space and Environment, or clear the pins. Membership and current permissions are checked again when work starts." />}
    {environment && !state.data?.environmentsEnabled && <Button disabled={disabled} onClick={() => onChange({ pinnedEnvironmentId: null })}>Clear unavailable Environment pin</Button>}
    {draft.targetSessionId && (!validSession ? <Typography.Text type="secondary">Enter a complete Session ID to check its saved context.</Typography.Text> : conversation.error ? <AccessError error={conversation.error} retry={conversation.reload} /> : !selected ? <AccessLoading /> : <>
      <Typography.Text type="secondary">Conversation context: {selected.space?.name ?? 'Unavailable'}{selected.space?.environmentsEnabled ? ` · ${selected.environment?.name ?? (selected.selection.environmentId ? 'Environment unavailable' : 'No Environment')}` : ''}</Typography.Text>
      {!matches && <Alert type="warning" showIcon title="Execution context must match the conversation" description="An occurrence cannot change the selected conversation’s Space or Environment." action={<Button disabled={disabled || selected.state === 'stale'} onClick={() => onChange({ pinnedSpaceId: selected.selection.spaceId, pinnedEnvironmentId: selected.selection.environmentId })}>Use conversation context</Button>} />}
    </>)}
  </Flex>;
}
