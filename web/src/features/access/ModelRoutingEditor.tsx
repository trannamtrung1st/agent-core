import { useEffect, useState } from 'react';
import { Alert, Button, Checkbox, Flex, Form, InputNumber, Typography } from 'antd';
import { identityRequest, type TenantContext } from '../../services/identity';
import { modelApi, readModels, type ModelAdmin } from '../../services/models';
import { AccessError } from './AccessFeedback';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { DecisionServiceReadiness } from './DecisionServiceReadiness';
import { useTenantCommand } from './useTenantCommand';

export function ModelRoutingEditor({ context, catalog, saved, dirtyChanged }: { context: TenantContext; catalog: ModelAdmin; saved: () => void; dirtyChanged: (dirty: boolean) => void }) {
  const [baseline, setBaseline] = useState(catalog);
  const current = baseline.settings.routing ?? { enabled: false, allowConfiguredDefault: false, minimumProbability: .7 };
  const [draft, setDraft] = useState(current); const [fresh, setFresh] = useState(true);
  const command = useTenantCommand(); const reader = useTenantCommand(); const dirty = JSON.stringify(draft) !== JSON.stringify(current);
  const guard = useAccessDraftGuard(dirty, command.busy || reader.busy, () => {});
  useEffect(() => { dirtyChanged(dirty || command.busy); return () => dirtyChanged(false); }, [dirty, command.busy, dirtyChanged]);
  const disabled = !baseline.access.canManageDefault || command.busy || reader.busy || !fresh || command.requiresReload;
  const eligible = catalog.availability.filter(item => item.available).length;
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">
    {guard.holder}
    <Typography.Title level={4}>Auto routing</Typography.Title>
    <Typography.Paragraph>Auto chooses an eligible model and supported reasoning effort once for each new Run. Fixed choices and admitted Runs keep their selection.</Typography.Paragraph>
    <Alert type="info" showIcon title={`${eligible} available model registrations`} description="Balanced uses declared capabilities. Cost, latency and context capacity are unknown. No quality or performance benchmark is recorded for this installation. Host inference is optional and disabled by default." />
    <DecisionServiceReadiness path={catalog.access.canManageDefault ? modelApi(context.tenant.tenantId, '/models/routing/status') : null} />
    {!catalog.access.canManageDefault && <Alert type="info" title="Read-only routing" description="Organization model management permission is required." />}
    {command.error !== undefined && <AccessError error={command.error} />}
    {reader.error !== undefined && <AccessError error={reader.error} />}
    {!fresh && <Alert type="warning" showIcon title="Reload before saving again" description="The configuration changed or the result is unknown. Keep this draft and deliberately reload current settings." />}
    <Form layout="vertical" onFinish={() => void command.run(async signal => {
      try { return await identityRequest(modelApi(context.tenant.tenantId, '/models/routing'), { method: 'PUT', signal, body: { routing: draft, expectedRevision: baseline.securityRevision, expectedSettingsRevision: baseline.settings.revision } }); }
      catch (error) { setFresh(false); throw error; }
    }, () => { guard.release(); saved(); })}>
      <Form.Item><Checkbox checked={draft.enabled} disabled={disabled} onChange={event => setDraft({ ...draft, enabled: event.target.checked })}>Allow Auto for new Runs</Checkbox></Form.Item>
      <Form.Item><Checkbox checked={draft.allowConfiguredDefault} disabled={disabled} onChange={event => setDraft({ ...draft, allowConfiguredDefault: event.target.checked })}>Allow an eligible configured default when inference is unavailable, uncertain or in shadow mode</Checkbox></Form.Item>
      <Form.Item label="Minimum option probability" extra="An acceptance threshold, not a measured probability of correctness."><InputNumber aria-label="Minimum option probability" min={.5} max={1} step={.05} value={draft.minimumProbability} disabled={disabled} onChange={value => value !== null && setDraft({ ...draft, minimumProbability: value })} /></Form.Item>
      <Flex wrap gap="var(--ac-space-compact)"><Button type="primary" htmlType="submit" disabled={disabled || !dirty} loading={command.busy}>Save routing</Button><Button disabled={command.busy || reader.busy} onClick={() => void reader.run(signal => readModels(context.tenant.tenantId, signal), value => { setBaseline(value); setFresh(true); command.clearError(); })}>Reload routing</Button></Flex>
    </Form>
    <Typography.Paragraph type="secondary">Without an explicitly allowed eligible default, an unavailable Auto decision refuses new work and offers model repair. Configure the internal decision service through the operator runbook.</Typography.Paragraph>
  </Flex>;
}
