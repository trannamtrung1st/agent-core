import { AgentConfigurationPanel, AgentSettingRow } from '../admin/AgentConfigurationLayout';
import { useEffect, useState, type RefObject } from 'react';
import { Alert, Button, Flex, Form, Select, Typography } from 'antd';
import { identityRequest, type TenantContext } from '../../services/identity';
import { availabilityLabel, modelApi, offeringLabel, readModels, type ModelAdmin } from '../../services/models';
import { ModelImpactConfirmation } from './ModelReferences';
import { AccessError } from './AccessFeedback';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useTenantCommand } from './useTenantCommand';

export function ModelDefaultEditor({ context, catalog, saved, dirtyChanged, reloadRef }: {
  context: TenantContext; catalog: ModelAdmin; saved: () => void; dirtyChanged: (dirty: boolean) => void; reloadRef?: RefObject<HTMLButtonElement | null>;
}) {
  const tenant = context.tenant.tenantId;
  const [referenceError, setReferenceError] = useState<unknown>();
  const [pending, setPending] = useState<{ selected: string | null; effort: string | null }>();
  const [baseline, setBaseline] = useState(catalog);
  const [selected, setSelected] = useState(catalog.settings.defaultOfferingId), [effort, setEffort] = useState(catalog.settings.defaultReasoningEffort);
  const [dirty, setDirty] = useState(false), [fresh, setFresh] = useState(true);
  const command = useTenantCommand(), reader = useTenantCommand(); const busy = command.busy || reader.busy || !!pending;
  const guard = useAccessDraftGuard(dirty, busy, () => {});
  useEffect(() => { dirtyChanged(dirty || busy); return () => dirtyChanged(false); }, [dirty, busy, dirtyChanged]);
  const model = baseline.offerings.find(item => item.offeringId === selected);
  const availability = baseline.availability.find(item => item.offeringId === selected);
  const choices = baseline.offerings.map(item => ({ value: item.offeringId, label: `${offeringLabel(item, baseline)}${baseline.availability.find(a => a.offeringId === item.offeringId)?.available ? '' : ' · Unavailable'}`, disabled: !baseline.availability.find(a => a.offeringId === item.offeringId)?.available }));
  if (selected && !model) choices.unshift({ value: selected, label: 'Selected model unavailable', disabled: true });
  const reload = () => void reader.run(signal => readModels(tenant, signal), current => { setBaseline(current); setFresh(true); setReferenceError(undefined); command.clearError(); });
  const save = (choice: { selected: string | null; effort: string | null }) => command.run(async signal => {
    try { return await identityRequest(modelApi(tenant, '/models/default'), { method: 'PUT', signal, body: { offeringId: choice.selected,
      reasoningEffort: choice.selected ? choice.effort : null, expectedRevision: baseline.securityRevision, expectedSettingsRevision: baseline.settings.revision } }); }
    catch (error) { setFresh(false); throw error; }
  }, () => { guard.release(); setDirty(false); saved(); });
  const choiceLabel = (id: string | null) => { const offering = baseline.offerings.find(item => item.offeringId === id); return offering ? offeringLabel(offering, baseline) : id ? 'Current default unavailable' : 'No default'; };
  return <><AgentConfigurationPanel title="Organization default" label="Organization default" bodyGap="section">
    {guard.holder}
    <Typography.Paragraph type="secondary">Definitions, Instances and new conversations inherit this choice unless they have an explicit override. Admitted Runs and continuations keep their exact captured route. A shared system preset is never an execution default.</Typography.Paragraph>
    {!baseline.access.canManageDefault && <Alert type="info" showIcon title="Read-only default" description="Organization-wide model management permission is required to change this default." />}
    {!selected && <Alert type="info" showIcon title="No readable default selected" description="Choose an available offering to start conversations that inherit the organization default. No provider is chosen automatically." />}
    {selected && !availability?.available && <Alert type="warning" showIcon title="Selected default unavailable" description={availabilityLabel(availability?.reason ?? '')} />}
    {referenceError !== undefined && <AccessError error={referenceError} />}{command.error !== undefined && <AccessError error={command.error} />}{reader.error !== undefined && <AccessError error={reader.error} />}
    {!fresh && <Alert type="warning" showIcon title="Reload before saving again" description="Your choice is retained. Reload the current default, compare it below, then deliberately save again." />}
    <Form layout="vertical" className="agent-settings-rows" disabled={busy || !baseline.access.canManageDefault}>
      <AgentSettingRow label={<label htmlFor="organization-default-model">Default model</label>}><Form.Item><Select id="organization-default-model" aria-label="Default model" value={selected ?? ''} options={[{ value: '', label: 'No default — refuse inherited model execution', disabled: false }, ...choices]} onChange={value => { setSelected(value || null); setEffort(null); setDirty(true); }} /></Form.Item></AgentSettingRow>
      {model && model.capabilities.reasoningEfforts.length > 0 && <AgentSettingRow label={<label htmlFor="organization-default-effort">Default reasoning effort</label>}><Form.Item><Select id="organization-default-effort" aria-label="Default reasoning effort" value={effort ?? ''} options={[
        { value: '', label: `Use model default (${model.capabilities.defaultReasoningEffort ?? 'unspecified'})` }, ...model.capabilities.reasoningEfforts.map(value => ({ value, label: value }))
      ]} onChange={value => { setEffort(value || null); setDirty(true); }} /></Form.Item></AgentSettingRow>}
    </Form>
    <Typography.Text type="secondary">Current saved default: {baseline.offerings.find(item => item.offeringId === baseline.settings.defaultOfferingId) ? offeringLabel(baseline.offerings.find(item => item.offeringId === baseline.settings.defaultOfferingId)!, baseline) : 'No readable default'} · {baseline.settings.defaultReasoningEffort ?? 'Model default effort'} · revision {baseline.settings.revision}.</Typography.Text>
    <Flex className="agent-settings-actions" wrap gap="var(--ac-space-compact)"><Button ref={reloadRef} disabled={busy} onClick={reload}>Reload current default</Button><Button type="primary" loading={command.busy} disabled={busy || !dirty || !fresh || command.requiresReload || !baseline.access.canManageDefault || !!selected && !availability?.available} onClick={() => {
      if (baseline.settings.defaultOfferingId) { setPending({ selected, effort }); return; }
      void save({ selected, effort });
    }}>Save organization default</Button></Flex>
  </AgentConfigurationPanel>{pending && baseline.settings.defaultOfferingId && <ModelImpactConfirmation tenant={tenant}
    target={{ kind: 'offering', id: baseline.settings.defaultOfferingId, label: 'Organization default' }} revision={baseline.securityRevision}
    changes={[`Default model: ${choiceLabel(baseline.settings.defaultOfferingId)} → ${choiceLabel(pending.selected)}`,
      `Default effort: ${baseline.settings.defaultReasoningEffort ?? 'Model default'} → ${pending.effort ?? 'Model default'}`,
      'New inherited Runs use the saved default. Explicit choices and admitted Run pins retain their model.']}
    busy={command.busy} failed={reason => { setReferenceError(reason); setFresh(false); setPending(undefined); }} close={() => setPending(undefined)} confirm={() => { void save(pending).finally(() => setPending(undefined)); }} />}</>;
}
