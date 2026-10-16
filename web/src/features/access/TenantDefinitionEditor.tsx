import { useCallback, useEffect, useState } from 'react';
import { Alert, App, Button, Flex, Input, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { navigateToAppPath } from '../../app/appRoute';
import { confirmAction } from '../../app/confirmAction';
import { identityRequest, tenantApi, type TenantContext } from '../../services/identity';
import { definitionApi, draftApi, sourceLabel, type DefinitionAccess, type DefinitionMetadata, type TenantDraft, type TenantDraftState } from '../../services/tenantDefinitions';
import { AdminOwnerHeading } from '../admin/AdminShell';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { type DraftPage } from '../admin/adminNavigation';
import { applyCandidateJson, candidateForPersistence, candidateToJson, candidatesEqual, cloneCandidate, automationNumberErrors, type DefinitionCandidate } from '../admin/definitionCandidate';
import { DefinitionCandidateForm } from '../admin/definitionCandidateEditor';
import { ScopedExecutionBudgetLimits } from '../admin/executionBudgetLimits';
import { validExecutionBudgets } from '../admin/ExecutionBudgetsSection';
import { DefinitionSkillsSection } from '../admin/DefinitionSkillsSection';
import { DefinitionStateIsolationPolicy } from '../admin/DefinitionStateIsolationPolicy';
import { DefinitionSelfManagement } from '../admin/DefinitionSelfManagement';
import { tenantDefinitionPath, tenantDraftPath } from './accessRoute';
import { AccessError, AccessLoading, AccessUnavailable } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useTenantCommand } from './useTenantCommand';
import { TenantDraftResources, type DraftMutation } from './TenantDefinitionResources';
import { TenantDefinitionEvaluations } from './TenantDefinitionEvaluations';
import { TenantDefinitionCapabilities } from './TenantDefinitionCapabilities';

export function TenantDefinitionEditor({ context, id, draftId, page }: { context: TenantContext; id: string; draftId: string; page: DraftPage }) {
  const tenant = context.tenant.tenantId; const { message, modal } = App.useApp(); const command = useTenantCommand();
  const saved = useIdentityQuery<TenantDraftState>(draftApi(tenant, draftId, '/state'));
  const access = useIdentityQuery<DefinitionAccess>(definitionApi(tenant, id, '/access'));
  const metadata = useIdentityQuery<DefinitionMetadata>(tenantApi(tenant, `/definition-authoring-options?definitionId=${encodeURIComponent(id)}`));
  const [snapshot, setSnapshot] = useState<TenantDraftState>(); const [candidate, setCandidate] = useState<DefinitionCandidate>({});
  const [jsonText, setJsonText] = useState(''); const [jsonError, setJsonError] = useState<string>(); const [childDirty, setChildDirty] = useState(false);
  const [revisionKnown, setRevisionKnown] = useState(false); const [commitNotice, setCommitNotice] = useState<string>();
  useEffect(() => { if (saved.data && saved.data.draft.definitionId === id) { setSnapshot(saved.data); setCandidate(cloneCandidate(saved.data.draft.candidate)); setJsonText(candidateToJson(saved.data.draft.candidate)); setJsonError(undefined); setRevisionKnown(true); } }, [saved.data, id]);
  const dirty = !!snapshot && (!candidatesEqual(candidate, snapshot.draft.candidate) || jsonError !== undefined);
  const busy = command.busy || !revisionKnown || access.loading;
  const guard = useAccessDraftGuard(dirty || childDirty, command.busy, () => {}, childDirty ? undefined : `${tenantDefinitionPath(tenant, id)}/drafts/${draftId}/`);
  const editable = !!access.data?.permissions.includes('agent-definitions.update');
  const canPublish = !!access.data?.permissions.includes('agent-definitions.publish') && !!snapshot?.resourceAccess.canPublish;
  const canDelete = !!access.data?.permissions.includes('agent-definitions.delete');
  const change = useCallback((value: DefinitionCandidate) => { setCandidate(value); setJsonText(candidateToJson(value)); setJsonError(undefined); }, []);
  const reload = () => {
    const load = () => { command.clearError(); setRevisionKnown(false); saved.reload(); access.reload(); };
    if (dirty || childDirty) confirmAction(modal, { title: 'Replace unsaved edits with the saved draft?', content: 'Your local edits will be discarded. The saved draft and current access will be loaded.', okText: 'Reload saved draft', danger: true, onOk: load }); else load();
  };
  const mutate: DraftMutation = (suffix, method, body = {}, committed) => {
    if (!snapshot || !access.data || busy || dirty || childDirty) return;
    const witness = { expectedRevision: access.data.revision, expectedDraftRevision: snapshot.draft.revision };
    const path = draftApi(tenant, draftId, suffix) + (method === 'DELETE' ? `?expectedRevision=${witness.expectedRevision}&expectedDraftRevision=${witness.expectedDraftRevision}` : '');
    void command.run(signal => identityRequest(path, { method, signal, ...(method === 'DELETE' ? {} : { body: { ...body, ...witness } }) }), () => {
      setCommitNotice('Change saved. Reloading the saved draft.'); setRevisionKnown(false); committed?.(); saved.reload(); access.reload();
    });
  };
  if (saved.data && saved.data.draft.definitionId !== id) return <AccessUnavailable />;
  if (access.error || (!snapshot && saved.error)) return <AccessError error={access.error ?? saved.error} retry={reload} />;
  if (!snapshot || !access.data) return <AccessLoading />;
  const draft = snapshot.draft;
  const blockingEvaluation = snapshot.scenarios.some(s => s.requirementLevel === 0 && !snapshot.results.some(r => r.scenarioId === s.scenarioId && r.scenarioVersion === s.scenarioVersion && r.draftRevision === draft.revision && r.passed));
  const invalid = jsonError !== undefined || automationNumberErrors(candidate).length > 0 || !validExecutionBudgets((candidate.executionBudgets ?? {}) as import('../../services/adminApi').ExecutionBudgetPolicy, metadata.data?.limits ?? null);
  const jsonEditor = <AgentConfigurationPanel title="Advanced JSON" label="Definition JSON editor"><Input.TextArea aria-label="Advanced JSON" className="admin-draft-instructions" rows={18} spellCheck={false} value={jsonText} readOnly={!editable} disabled={busy} onChange={event => { setJsonText(event.target.value); const parsed = applyCandidateJson(event.target.value); if (parsed.ok) { setCandidate(parsed.candidate); setJsonError(undefined); } else setJsonError(parsed.error); }} /></AgentConfigurationPanel>;
  return <ScopedExecutionBudgetLimits value={{ limits: metadata.data?.limits ?? null, error: metadata.error ? 'Authoring limits are unavailable. Reload authoring options before changing budget values.' : null, reload: metadata.reload }}>
    {guard.holder}<Flex vertical gap="var(--ac-space-section)">
      <Button style={{ alignSelf: 'start' }} href={tenantDefinitionPath(tenant, id)} onClick={followAppLink}>Back to Definition</Button>
      <AdminOwnerHeading name={String((candidate.identity as { name?: string })?.name ?? 'Definition draft')} metadata={<Typography.Text type="secondary">Revision {draft.revision} · {sourceLabel(draft)}</Typography.Text>} />
      <section className="admin-owner-actions" aria-label="Draft actions"><Flex wrap gap="var(--ac-space-compact)" align="center">
        {editable && <Button type={dirty ? 'primary' : 'default'} disabled={busy || !dirty || invalid || childDirty} onClick={() => void command.run(signal => identityRequest<TenantDraft>(draftApi(tenant, draftId), { method: 'PUT', signal, body: { candidate: candidateForPersistence(candidate), expectedRevision: access.data!.revision, expectedDraftRevision: draft.revision } }), updated => {
          setSnapshot(current => current && { ...current, draft: updated }); change(cloneCandidate(updated.candidate)); access.reload(); setCommitNotice(undefined); message.success('Draft saved.');
        })}>Save draft</Button>}
        {canPublish && <Button type={!dirty && !blockingEvaluation ? 'primary' : 'default'} disabled={busy || dirty || childDirty || blockingEvaluation || invalid} onClick={() => confirmAction(modal, { title: 'Publish this saved draft?', content: 'This creates an immutable publication and consumes the draft. Existing Instances keep their pinned version.', okText: 'Publish definition', onOk: () => command.run(signal => identityRequest(draftApi(tenant, draftId, '/publish'), { method: 'POST', signal, body: { expectedRevision: access.data!.revision, expectedDraftRevision: draft.revision } }), () => { guard.release(); message.success('Definition published.'); navigateToAppPath(tenantDefinitionPath(tenant, id)); }) })}>Publish</Button>}
        <Button disabled={command.busy} onClick={reload}>Reload saved draft</Button>
        {canDelete && <Button danger disabled={busy} onClick={() => confirmAction(modal, { title: 'Delete this draft?', content: 'The draft and its evaluation/resource bindings will be removed. Deleting the last unpublished draft also removes the Definition.', danger: true, okText: 'Delete draft', onOk: () => command.run(signal => identityRequest(draftApi(tenant, draftId, `?expectedRevision=${access.data!.revision}&expectedDraftRevision=${draft.revision}`), { method: 'DELETE', signal }), () => { guard.release(); message.success('Draft deleted.'); navigateToAppPath(`/t/${tenant}/admin/definitions`); }) })}>Delete draft</Button>}
        <Typography.Text type="secondary">{dirty || childDirty ? 'Unsaved changes' : 'Saved'}</Typography.Text>
      </Flex>
        {jsonError && <Typography.Text type="danger" role="alert">Advanced JSON is invalid: {jsonError}</Typography.Text>}
        {invalid && !jsonError && <Typography.Text type="danger" role="alert">Review number values and execution limits before saving.</Typography.Text>}
        {dirty && <Typography.Text type="secondary">Save candidate edits before changing resources or running evaluations.</Typography.Text>}
        {blockingEvaluation && <Typography.Link href={tenantDraftPath(tenant, id, draftId, 'test-publish')} onClick={followAppLink}>Run the current required evaluations before publishing.</Typography.Link>}
        {access.data.permissions.includes('agent-definitions.publish') && !snapshot.resourceAccess.canPublish && <Typography.Text type="secondary">Publishing these resources requires organization resource creation access.</Typography.Text>}
        {!editable && <Typography.Text type="secondary">Draft settings are read-only.</Typography.Text>}
      </section>
      {command.error !== undefined && <Flex vertical gap="var(--ac-space-compact)"><AccessError error={command.error} /><Button disabled={command.busy} style={{ alignSelf: 'start' }} onClick={access.reload}>Reload access and keep edits</Button></Flex>}
      {saved.error && <AccessError error={saved.error} retry={reload} />}
      {commitNotice && !revisionKnown && <Alert showIcon type="info" title={commitNotice} description="The change is already committed. Reloading does not submit it again." />}
      {metadata.error && <AccessError error={metadata.error} retry={metadata.reload} />}
      {page === 'overview' ? <Flex vertical gap="var(--ac-space-section)"><Typography.Paragraph>Edit the candidate in the configuration pages, bind resources and run offline evaluations. Publication creates a new immutable version; runtime agent authority stays independent.</Typography.Paragraph><Button href={tenantDraftPath(tenant, id, draftId, 'profile')} onClick={followAppLink} style={{ alignSelf: 'start' }}>Edit identity & goals</Button></Flex>
        : page === 'advanced-json' ? jsonEditor
        : page === 'skills' ? <DefinitionSkillsSection candidate={candidate} busy={busy || !!jsonError} readOnly={!editable} onChange={change} onDirtyChange={setChildDirty} />
        : page === 'state-isolation' ? <DefinitionStateIsolationPolicy candidate={candidate} savedCandidate={draft.candidate} disabled={busy || !!jsonError} readOnly={!editable} onChange={change} />
        : page === 'self-management' ? <DefinitionSelfManagement candidate={candidate} disabled={busy || !editable || !!jsonError} onChange={change} />
        : page === 'resources' ? <TenantDraftResources tenant={tenant} draftId={draftId} items={snapshot.resources} access={snapshot.resourceAccess} busy={busy || dirty || childDirty} mutate={mutate} error={command.error} />
        : page === 'test-publish' ? <TenantDefinitionEvaluations snapshot={snapshot} writable={editable} busy={busy || dirty || childDirty} mutate={mutate} error={command.error} />
        : page === 'capabilities' ? <TenantDefinitionCapabilities candidate={candidate} resources={snapshot.resources} metadata={metadata.data} disabled={busy || !editable || !!jsonError} onChange={change} />
        : page === 'sandbox' ? <Alert type="info" showIcon title="Sandbox selections are preserved in the candidate." description={<Typography.Paragraph>Tenant-owned Sandbox catalog integration is still in progress. Inspect the saved policy in <Typography.Link href={tenantDraftPath(tenant, id, draftId, 'advanced-json')} onClick={followAppLink}>Advanced JSON</Typography.Link>.</Typography.Paragraph>} />
        : <DefinitionCandidateForm page={page} candidate={candidate} busy={busy || !!jsonError} readOnly={!editable} authoring={metadata.data?.authoring ?? null} authoringError={metadata.error ? 'Authoring options are unavailable. Reload them or inspect Advanced JSON.' : null} authoringDiagnosticId={null} onCandidateChange={change} />}
    </Flex>
  </ScopedExecutionBudgetLimits>;
}
