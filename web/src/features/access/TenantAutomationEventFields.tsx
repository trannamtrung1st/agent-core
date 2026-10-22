import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Checkbox, Collapse, Flex, Form, Pagination, Select, Typography, theme } from 'antd';
import type { AutomationChild, EventSourceReference, FilterTestResult } from '../../services/adminApi';
import { eventSourceKey } from '../../services/adminApi';
import { identityRequest, tenantApi } from '../../services/identity';
import { newTenantAutomationEvent, tenantAutomationsApi, type TenantAutomationEvent } from '../../services/tenantAutomations';
import { tenantWebhookApi, type TenantWebhook, type TenantWebhookPage } from '../../services/tenantWebhooks';
import { AutomationEventFilter } from '../admin/AutomationEventFilter';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';

type Builtin = { key: string; description: string; example?: Record<string, unknown> };
export function TenantAutomationEventFields({ tenant, instance, automation, events, disabled, reloadKey, onChange, onReady }: {
  tenant: string; instance: string; automation?: string; events: TenantAutomationEvent[]; disabled: boolean; reloadKey: string;
  onChange: (events: TenantAutomationEvent[]) => void; onReady: (ready: boolean) => void;
}) {
  const { token } = theme.useToken(); const [page, setPage] = useState(0); const [available, setAvailable] = useState<Record<string, boolean>>({});
  const root = useRef<HTMLDivElement>(null); const [active, setActive] = useState<string[]>(events[0] ? [events[0].localKey] : []);
  const focusChild = (key: string) => requestAnimationFrame(() => { const group = root.current?.querySelector<HTMLElement>(`[data-event-key="${key}"]`); group?.scrollIntoView({ block: 'nearest' }); group?.querySelector<HTMLInputElement>('input[role=combobox]')?.focus(); });
  const builtins = useIdentityQuery<{ items: Builtin[] }>(tenantApi(tenant, '/events/builtin'), reloadKey);
  const webhooks = useIdentityQuery<TenantWebhookPage>(tenantWebhookApi(tenant, `?page=${page}&pageSize=20`), reloadKey);
  useEffect(() => { onReady(events.some(e => e.enabled) && events.filter(e => e.enabled).every(e => available[e.localKey])); }, [events, available, onReady]);
  const change = (key: string, value: TenantAutomationEvent) => onChange(events.map(e => e.localKey === key ? value : e));
  return <Flex ref={root} vertical gap={token.padding}>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Each source has its own filter and dispatch. Different sources match independently. Received events keep their captured filters; edits apply to new receipts.</Typography.Paragraph>
    <Flex wrap gap={token.paddingXS}><Button disabled={disabled} onClick={() => { builtins.reload(); webhooks.reload(); }}>Reload event sources</Button>
      <Button disabled={disabled || events.length >= 32} onClick={() => { const next = newTenantAutomationEvent(); onChange([...events, next]); setActive([next.localKey]); focusChild(next.localKey); }}>Add event subscription</Button></Flex>
    <Collapse activeKey={active} onChange={keys => setActive(typeof keys === 'string' ? [keys] : keys)} items={events.map((event, index) => ({ key: event.localKey, forceRender: true,
      label: `Subscription ${index + 1} · ${event.source?.kind === 'builtin' ? event.source.key : event.source ? 'Webhook' : 'Choose a source'}`,
      extra: <Button size="small" danger disabled={disabled || events.length <= 1} aria-label={`Remove subscription ${index + 1}`} onClick={e => { e.stopPropagation(); const next = events.filter(row => row.localKey !== event.localKey); onChange(next); const target = next[Math.min(index, next.length - 1)]; if (target) { setActive([target.localKey]); focusChild(target.localKey); } }}>Remove</Button>,
      children: <EventChild tenant={tenant} instance={instance} automation={automation} value={event} index={index} disabled={disabled}
        builtins={builtins} webhooks={webhooks} reloadKey={reloadKey} used={events.filter(e => e.localKey !== event.localKey).flatMap(e => e.source ? [eventSourceKey(e.source)] : [])}
        onChange={next => change(event.localKey, next)} onReady={ready => setAvailable(current => current[event.localKey] === ready ? current : { ...current, [event.localKey]: ready })}
        onReadLost={() => webhooks.reload()} /> }))} />
    {webhooks.data && webhooks.data.total > 20 && <Pagination size="small" aria-label="Webhook source pages" current={page + 1} pageSize={20} total={webhooks.data.total} showSizeChanger={false} disabled={disabled} onChange={value => setPage(value - 1)} />}
    <Typography.Text type="secondary">{events.length} of 32 subscriptions. Saving or enabling a subscription grants no execution or source-read permission.</Typography.Text>
  </Flex>;
}

function EventChild({ tenant, instance, automation, value, index, disabled, builtins, webhooks, used, reloadKey, onChange, onReady, onReadLost }: {
  tenant: string; instance: string; automation?: string; value: TenantAutomationEvent; index: number; disabled: boolean;
  builtins: ReturnType<typeof useIdentityQuery<{ items: Builtin[] }>>; webhooks: ReturnType<typeof useIdentityQuery<TenantWebhookPage>>;
  used: string[]; reloadKey: string; onChange: (value: TenantAutomationEvent) => void; onReady: (value: boolean) => void; onReadLost: () => void;
}) {
  const { token } = theme.useToken(); const [family, setFamily] = useState<'builtin' | 'webhook'>(value.source?.kind ?? 'builtin');
  const source = value.source; const selected = source ? eventSourceKey(source) : '';
  const detail = useIdentityQuery<TenantWebhook>(source?.kind === 'webhook' ? tenantWebhookApi(tenant, `/${encodeURIComponent(source.eventId)}`) : null, reloadKey);
  const builtin = source?.kind === 'builtin' ? builtins.data?.items.find(item => item.key === source.key) : undefined;
  const webhook = source?.kind === 'webhook' && detail.data?.resourceId === source.eventId ? detail.data : undefined;
  const ready = !!source && (source.kind === 'builtin' ? !!builtin : !!webhook);
  useEffect(() => { onReady(ready); }, [ready, selected, onReady]);
  useEffect(() => { if (detail.error) onReadLost(); }, [detail.error]);
  const example = useMemo(() => builtin?.example ?? ({ schemaVersion: 1, type: source?.kind === 'builtin' ? `core.${source.key}` : `webhook.${webhook?.eventKey ?? 'example'}`,
    source: { kind: source?.kind === 'builtin' ? 'core' : 'webhook', key: source?.kind === 'builtin' ? source.key : webhook?.eventKey ?? 'example' },
    scope: { agentInstanceId: instance }, data: {} }), [builtin, selected, webhook?.eventKey, instance]);
  const choices = family === 'builtin' ? (builtins.data?.items ?? []).map(item => ({ value: `builtin:${item.key}`, label: item.key, disabled: used.includes(`builtin:${item.key}`) }))
    : (webhooks.data?.items ?? []).map(item => ({ value: `webhook:${item.resourceId}`, label: `${item.displayName} · ${item.eventKey}${item.status === 'revoked' ? ' · revoked' : ''}`, disabled: used.includes(`webhook:${item.resourceId}`) }));
  if (source && source.kind === family && !choices.some(option => option.value === selected)) choices.unshift({ value: selected,
    label: source.kind === 'builtin' ? source.key : webhook ? `${webhook.displayName} · ${webhook.eventKey}` : 'Unavailable saved webhook', disabled: !ready });
  // The selected option cannot keep a stale private label after its exact read failed.
  if (source?.kind === 'webhook' && detail.error) for (const option of choices) if (option.value === selected) option.label = 'Unavailable saved webhook';
  function select(next: EventSourceReference | undefined) {
    const changed = !source || !next || eventSourceKey(source) !== eventSourceKey(next);
    onChange({ ...value, source: next, ...(changed ? { triggerId: undefined, revision: 0 } : {}) });
  }
  const filterTrigger: Extract<AutomationChild, { kind: 'event' }> | null = source ? { kind: 'event', triggerId: value.triggerId ?? value.localKey,
    revision: value.revision || 1, enabled: value.enabled, source, filterExpression: value.filterExpression, dispatch: value.dispatch } : null;
  return <Flex vertical gap={token.paddingSM} data-event-key={value.localKey} role="group" aria-label={`Event subscription ${index + 1}`}>
    <Checkbox disabled={disabled} checked={value.enabled} onChange={e => onChange({ ...value, enabled: e.target.checked })}>Enable this subscription</Checkbox>
    <Form.Item label="Source family"><Select aria-label={`Subscription ${index + 1} source family`} value={family} disabled={disabled}
      options={[{ value: 'builtin', label: 'Built-in event' }, { value: 'webhook', label: 'Webhook connection' }]} onChange={kind => { setFamily(kind); select(undefined); }} /></Form.Item>
    {(family === 'builtin' ? builtins.loading : webhooks.loading) && <AccessLoading />}
    {(family === 'builtin' ? builtins.error : webhooks.error) && <AccessError error={family === 'builtin' ? builtins.error : webhooks.error} retry={family === 'builtin' ? builtins.reload : webhooks.reload} />}
    <Form.Item label="Event source" required extra={family === 'webhook' ? 'Choices are the readable connections on the current source page.' : builtin?.description}>
      <Select aria-label={`Subscription ${index + 1} event source`} value={source?.kind === family ? selected : undefined} placeholder="Select a readable source" disabled={disabled}
        options={choices} showSearch optionFilterProp="label" onChange={key => select(family === 'builtin' ? { kind: 'builtin', key: key.slice(8) } : { kind: 'webhook', eventId: key.slice(8) })} />
    </Form.Item>
    {source?.kind === 'webhook' && detail.loading && <AccessLoading />}
    {detail.error && source?.kind === 'webhook' && <AccessError error={detail.error} retry={detail.reload} />}
    {source && !ready && !(source.kind === 'builtin' ? builtins.loading : detail.loading) && <Alert type="warning" showIcon title="Source is unavailable" description="The saved choice is retained. You can save a structurally valid disabled draft. Before enabling, restore eligible source access or deliberately replace this subscription." />}
    {webhook?.status === 'revoked' && <Alert type="info" showIcon title="This webhook is revoked" description="It accepts no new receipts. Enabling the Automation does not reactivate the connection." />}
    {filterTrigger && <AutomationEventFilter instanceId={instance} trigger={filterTrigger} example={example} disabled={disabled || !ready}
      onChange={next => onChange({ ...value, filterExpression: next.filterExpression ?? null, dispatch: next.dispatch ?? { mode: 'everyMatch' } })}
      testFilter={(expression, event) => identityRequest<FilterTestResult>(tenantAutomationsApi(tenant, instance, `/filter-test${automation ? `?automationId=${encodeURIComponent(automation)}` : ''}`), { method: 'POST', body: { expression, event } })} />}
  </Flex>;
}
