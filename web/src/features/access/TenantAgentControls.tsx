import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Flex, Form, Tag, Typography } from 'antd';
import { IdentityCompletionError, IdentityError, identityRequest, tenantApi, type TenantContext } from '../../services/identity';
import { tenantAgentApi, type TenantAgentControlsView } from '../../services/tenantAgentConfiguration';
import type { ExecutionBudgetPolicy } from '../../services/adminApi';
import type { listModels } from '../../services/api';
import { followAppLink } from '../../app/appLink';
import { confirmAction } from '../../app/confirmAction';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { BudgetFields, validExecutionBudgets } from '../admin/ExecutionBudgetsSection';
import { SelfManagementFields, inheritedManagement, managementDraftOf, managementOverridesOf, type SelfManagementDraft } from '../admin/SelfManagementFields';
import { ExecutionModelFields } from '../admin/ExecutionModelFields';
import { AccessError, AccessLoading } from './AccessFeedback';
import { TenantAgentHeading } from './TenantAgentHeading';
import { tenantAccessPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

export type TenantControlPage = 'execution-budgets' | 'self-management' | 'unattended-model';
type Draft = { budgets: ExecutionBudgetPolicy; management: SelfManagementDraft; model: { key: string; effort: string } };
const budgetDraft = (value: ExecutionBudgetPolicy | null): ExecutionBudgetPolicy => ({ standard: value?.standard ?? null, interactiveBrowser: value?.interactiveBrowser ?? null, unattendedBoundBrowser: value?.unattendedBoundBrowser ?? null });
const draftOf = (view: TenantAgentControlsView): Draft => ({ budgets: budgetDraft(view.budgets.overrides), management: managementDraftOf(view.management.policy), model: { key: view.agent.instance.unattendedOfferingId ?? '', effort: view.agent.instance.unattendedReasoningEffort ?? '' } });
const titles: Record<TenantControlPage, string> = { 'execution-budgets': 'Execution budgets', 'self-management': 'Self-management', 'unattended-model': 'Unattended model' };
const draftPart = (draft: Draft, page: TenantControlPage) => page === 'execution-budgets' ? draft.budgets : page === 'self-management' ? draft.management : draft.model;

export function TenantAgentControls({ context, id, page }: { context: TenantContext; id: string; page: TenantControlPage }) {
  const tenant = context.tenant.tenantId; const { message, modal } = App.useApp(); const command = useTenantCommand();
  const state = useIdentityQuery<TenantAgentControlsView>(tenantAgentApi(tenant, id, '/configuration-controls'));
  const catalog = useIdentityQuery<Awaited<ReturnType<typeof listModels>>>(page === 'unattended-model' ? tenantApi(tenant, '/chat/models') : null);
  const [draft, setDraft] = useState<Draft>(); const [saved, setSaved] = useState<Draft>(); const [committed, setCommitted] = useState(false);
  const resetAfterRead = useRef(false); const initialized = useRef(false);
  useEffect(() => {
    if (!state.data) return; const next = draftOf(state.data); setSaved(next);
    if (!initialized.current || resetAfterRead.current) { setDraft(next); initialized.current = true; resetAfterRead.current = false; }
    setCommitted(false);
  }, [state.data]);
  useEffect(() => { if (state.error instanceof IdentityError && [401, 403, 404].includes(state.error.status)) { setDraft(undefined); setSaved(undefined); initialized.current = false; resetAfterRead.current = false; } }, [state.error]);
  const dirty = !!draft && !!saved && JSON.stringify(draftPart(draft, page)) !== JSON.stringify(draftPart(saved, page));
  const guard = useAccessDraftGuard(dirty && !(committed && resetAfterRead.current), command.busy, () => {});
  const view = state.data; const writable = !!view?.canUpdate && view.agent.instance.lifecycle === 'Active'; const disabled = !writable || command.busy || committed || command.requiresReload;
  const reload = () => { command.clearError(); state.reload(); };
  const selected = catalog.data?.models.find(model => model.offeringId === (draft?.model.key || catalog.data?.defaultOfferingId));
  const invalidModel = !!catalog.data && !!draft && (!!draft.model.key && !selected || !!draft.model.effort && !selected?.supportedReasoningEfforts.includes(draft.model.effort));
  const valid = !!draft && !!view && (page === 'execution-budgets' ? validExecutionBudgets(draft.budgets, view.budgets.hostLimits) : page !== 'unattended-model' || !!catalog.data && !invalidModel);
  function save(lock?: boolean) {
    if (!view || !draft || disabled || lock === undefined && (!dirty || !valid)) return;
    const suffix = lock === true ? '/self-management/freeze' : page === 'execution-budgets' ? '/execution-budgets' : page === 'self-management' ? '/self-management' : '/unattended-model';
    const policy = lock === undefined ? draft.management : managementDraftOf(view.management.policy);
    const body = { expectedRevision: view.securityRevision, expectedInstanceRevision: view.agent.instance.revision,
      ...(lock === true ? {} : page === 'execution-budgets' ? { budgets: draft.budgets } : page === 'self-management' ? { overrides: managementOverridesOf(policy), ...(lock === false ? { frozen: false } : {}) } : { offeringId: draft.model.key || null, reasoningEffort: draft.model.effort || null }) };
    void command.run(async signal => {
      try { await identityRequest<unknown>(tenantAgentApi(tenant, id, suffix), { method: lock === true ? 'POST' : 'PUT', body, signal }); }
      catch (error) { if (!(error instanceof IdentityCompletionError)) throw error; }
    }, () => { resetAfterRead.current = lock === undefined; setCommitted(true); message.success(lock === undefined ? 'Configuration saved. Reading current values.' : lock ? 'Self-management frozen.' : 'Self-management unfrozen.'); state.reload(); });
  }
  function changeLock(lock: boolean) {
    if (!view) return;
    confirmAction(modal, { title: lock ? 'Freeze self-management?' : 'Unfreeze self-management?', content: `Agent: ${view.agent.instance.persona.name}. Organization: ${context.tenant.name}. ${lock ? 'Revoke the saved management policy immediately. Normal tasks and retained content remain available.' : 'Remove the saved lock. The saved mode and areas govern management.'} Unsaved policy edits are preserved and are not applied.`, okText: lock ? 'Freeze self-management' : 'Unfreeze self-management', onOk: () => save(lock) });
  }
  const update = (part: Partial<Draft>) => setDraft(current => current ? { ...current, ...part } : current);
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">{guard.holder}
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to instances</Typography.Link><Button disabled={command.busy} onClick={reload}>Reload controls</Button></Flex>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {committed && <Alert type="info" showIcon title="Changes were saved" description="Read the current configuration before making another change. No command is replayed by Reload." />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={reload} /> : view && draft && <>
      <TenantAgentHeading view={view} />
      <AgentConfigurationPanel title={titles[page]} label={titles[page]} description={page === 'execution-budgets' ? 'Independent per-class limits inherit from the Definition. Changes apply to the next new Run.' : page === 'self-management' ? 'This management gate remains separate from the agent’s IAM roles, capabilities and exact approvals.' : 'Select the preference used by future unattended Runs. Current Runs keep their pinned model.'} bodyGap="section">
        {page === 'execution-budgets' ? <BudgetFields limits={view.budgets.hostLimits} value={draft.budgets} inherited={view.budgets.definitionDefaults ?? {}} instance disabled={disabled} onChange={budgets => update({ budgets })} /> : page === 'self-management' ? <>
          <Flex wrap gap="var(--ac-space-compact)" role="status" aria-label="Saved self-management policy"><Tag>Saved mode: {view.management.policy.effective.mode === 'Disabled' ? 'Off' : view.management.policy.effective.mode}</Tag><Tag>{view.management.policy.effective.frozen ? 'Frozen' : 'Not frozen'}</Tag><Typography.Text type="secondary">{view.management.policy.source === 'definition' ? 'Definition defaults' : 'Per-field Instance policy'} · policy revision {view.management.policy.policyRevision}</Typography.Text></Flex>
          {view.management.policy.effective.frozen && <Alert type="info" showIcon title="Self-management is frozen" description="Normal tasks and retained content remain available. An authorized person can explicitly remove this lock." />}
          <Form layout="vertical" className="admin-config-form"><SelfManagementFields value={draft.management} defaults={view.management.policy.definitionDefaults} disabled={disabled} onChange={management => update({ management })} /></Form>
          <Typography.Text type="secondary">Revocation and freeze take effect immediately. Other saved changes apply to the next new Run.</Typography.Text>
        </> : <>
          {catalog.loading ? <AccessLoading /> : catalog.error ? <AccessError error={catalog.error} retry={catalog.reload} /> : <ExecutionModelFields models={catalog.data?.models ?? []} defaultOfferingId={catalog.data?.defaultOfferingId ?? undefined} offeringId={draft.model.key} reasoningEffort={draft.model.effort} disabled={disabled || !catalog.data} showLabels modelLabel="Unattended model" effortLabel="Unattended reasoning effort" defaultLabel="Execution default" onChange={(key, effort) => update({ model: { key, effort } })} />}
          {invalidModel && <Alert type="warning" showIcon title="The saved selection is unavailable" description="Choose a current catalog model and supported reasoning effort, or reset to the execution default before saving." />}
          <Typography.Text type="secondary">Execution default uses the existing agent execution defaults. Model choice grants no additional authority.</Typography.Text>
        </>}
        <Flex wrap gap="var(--ac-space-compact)">
          <Button type="primary" aria-label={`Save ${titles[page]}`} loading={command.busy} disabled={disabled || !dirty || !valid} onClick={() => save()}>Save</Button>
          <Button aria-label={`Discard ${titles[page]} changes`} disabled={command.busy || committed || !dirty} onClick={() => setDraft(saved)}>Discard</Button>
          <Button disabled={disabled} onClick={() => update(page === 'execution-budgets' ? { budgets: budgetDraft(null) } : page === 'self-management' ? { management: inheritedManagement } : { model: { key: '', effort: '' } })}>{page === 'unattended-model' ? 'Reset to execution default' : 'Reset to Definition defaults'}</Button>
          {page === 'self-management' && (view.management.policy.effective.frozen || view.management.policy.effective.mode !== 'Disabled') && <Button disabled={disabled} onClick={() => changeLock(!view.management.policy.effective.frozen)}>{view.management.policy.effective.frozen ? 'Unfreeze self-management' : 'Freeze self-management'}</Button>}
        </Flex>
        {dirty && !committed && <Typography.Text type="secondary">Unsaved changes. Reload keeps your draft; leaving asks before discarding.</Typography.Text>}
      </AgentConfigurationPanel>
    </>}
  </Flex>;
}
