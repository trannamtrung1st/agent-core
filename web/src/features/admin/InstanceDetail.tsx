import { InstanceConfigurationActions, InstanceConfigurationContent, InstanceConfigurationProvider } from './InstanceConfigurationActions';
import { Alert, Button, Flex, Spin, Typography, theme } from "antd";
import { useCallback, useEffect, useRef, useState } from "react";
import { followAppLink } from "../../app/appLink";
import { adminInstancePath, navigateToAppPath } from "../../app/appRoute";
import { type AdminInstanceInventoryItem } from "../../services/adminApi";
import type { AutomationSelection, RunSource } from "../chat/runPresentation";
import { useAdminDraftStatus } from './AdminDraftStatus';
import { AdminRetryAction } from "./adminFailure";
import { instancePages, instanceSettingGroups, type InstancePage } from './adminNavigation';
import { AdminOwnerHeading, AdminPageHeading } from './AdminShell';
import { AgentConfigurationPanel } from "./AgentConfigurationLayout";
import { InstanceCredentialsSection } from "./CredentialsSection";
import { InstanceExecutionBudgets } from './ExecutionBudgetsSection';
import { HarnessManagementSection } from "./HarnessManagementSection";
import { InstanceSessionsSection } from "./InstanceActivitySection";
import { InstanceAutomationsSection } from "./InstanceAutomationsSection";
import { ExperienceSection, IdentityMaintenanceSection, type ExperienceSelection } from "./InstanceContinuitySection";
import { InstanceMemoryAutomationPanel } from "./instanceMemoryAutomation";
import { InstanceOverview } from './InstanceOverview';
import { InstanceResourcesSection } from "./InstanceResourcesSection";
import { InstanceRunsSection } from "./InstanceRunsSection";
import { InstanceSettingsSection } from "./InstanceSettingsSection";
import { InstanceSkillsSection } from "./InstanceSkillsSection";
import { InstanceWorkspaceSection } from "./InstanceWorkspaceSection";
import { RetainedAdminPage } from './RetainedAdminPage';
import { SelfManagementSection } from "./SelfManagementSection";
import { useAdminAnchor } from './useAdminAnchor';
import { useAdminUnsavedChanges } from './useAdminUnsavedChanges';

import { type EffectiveConfigLoadState, type LoadState } from './adminPresentation';
import { InstanceIdentityTags } from './AdminRecordSummaries';
import { EffectiveConfigView } from './EffectiveConfigView';
import { InstanceManagedControls } from './InstanceProfile';
export function InstanceDetail({ instanceId, page = 'overview', instances, effective, onRetryEffective, onInstanceChanged, onInstanceDeleted }: {
  instanceId: string; page?: InstancePage; instances: LoadState<AdminInstanceInventoryItem[]>; effective: EffectiveConfigLoadState;
  onBack: () => void; onRetryEffective: () => void; onInstanceChanged: () => void | Promise<void>; onInstanceDeleted: () => void;
}) {
  const { token } = theme.useToken();
  const [sourceSelection, setSourceSelection] = useState<AutomationSelection>();
  const [experienceSelection, setExperienceSelection] = useState<ExperienceSelection>();
  const [selectedAgentRunId, setSelectedAgentRunId] = useState<string>();
  const [runDetailsOpen, setRunDetailsOpen] = useState(false);
  const [settingsDirty, setSettingsDirty] = useState(false);
  const [editorDirty, setEditorDirty] = useState<Record<string, boolean>>({});
  useAdminDraftStatus(Object.keys(editorDirty).filter(key => editorDirty[key]).map(key => key === 'budgets' ? 'execution' : key === 'management' ? 'self-management' : key === 'unattended' ? 'unattended' : key));
  const dirty = settingsDirty || Object.values(editorDirty).some(Boolean);
  const releaseConsumedInstance = useAdminUnsavedChanges(`instance:${instanceId}`, dirty);
  const reportDirty = useCallback((key: string, value: boolean) => setEditorDirty(current => current[key] === value ? current : { ...current, [key]: value }), []);
  const reportCredentialDirty = useCallback((value: boolean) => reportDirty('credentials', value), [reportDirty]);
  const runOpener = useRef<HTMLElement | null>(null);
  const restoreRunFocus = useRef(false);
  useEffect(() => {
    const sync = () => {
      const params = new URLSearchParams(window.location.search);
      const id = params.get('run'); setSelectedAgentRunId(id ?? undefined); setRunDetailsOpen(!!id);
      const automation = params.get('automation');
      setSourceSelection(automation && /^[0-9a-f-]{36}$/i.test(automation) ? { kind: 'automation', automationId: automation, request: Date.now() } : undefined);
    };
    sync(); window.addEventListener('popstate', sync);
    return () => window.removeEventListener('popstate', sync);
  }, [instanceId]);
  const closeRun = () => {
    restoreRunFocus.current = true;
    const params = new URLSearchParams(window.location.search); params.delete('run');
    navigateToAppPath(`${window.location.pathname}${params.size ? `?${params}` : ''}${window.location.hash}`, true);
  };
  const go = (next: InstancePage) => navigateToAppPath(adminInstancePath(instanceId, next));
  const viewRun = (id?: string) => {
    if (!id) { go('runs'); return; }
    runOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    restoreRunFocus.current = false;
    const params = new URLSearchParams(window.location.search); params.set('run', id);
    navigateToAppPath(`${window.location.pathname}?${params}${window.location.hash}`);
  };
  const viewSource = (source: RunSource) => {
    restoreRunFocus.current = false; setRunDetailsOpen(false);
    if (source.kind === 'experience') {
      setExperienceSelection({ agentRunId: source.agentRunId, request: Date.now() }); go('experience'); return;
    }
    setSourceSelection({ ...source, request: Date.now() });
    navigateToAppPath(`${adminInstancePath(instanceId, 'automations')}${source.kind === 'automation' ? `?automation=${source.automationId}` : ''}`);
  };
  const row = instances.kind === 'ready' ? instances.data.find(item => item.instanceId.toLowerCase() === instanceId) : undefined;
  const resolved = effective.data?.instanceId === instanceId ? effective.data : null;
  const archived = resolved?.instanceLifecycle !== 'Active';
  useEffect(() => {
    if (resolved && archived && (page === 'automations' || page === 'unattended')) navigateToAppPath(`${adminInstancePath(instanceId, 'lifecycle')}?notice=archived-automations`, true);
  }, [resolved?.instanceLifecycle, instanceId, page]);
  useAdminAnchor(page, effective.kind === 'ready');
  return <Flex vertical gap={token.padding}>
    <AdminOwnerHeading name={resolved?.persona.name ?? row?.personaName ?? 'Agent Instance'}
      metadata={<><Typography.Text type="secondary">{resolved ? `${resolved.definitionId} · v${resolved.definitionVersion}` : row ? `${row.definitionId} · v${row.activeVersion}` : null}</Typography.Text><Typography.Text type="secondary" className="admin-instance-id" copyable={{ text: instanceId }}>Instance {instanceId}</Typography.Text></>}
      status={resolved && <InstanceIdentityTags lifecycle={resolved.instanceLifecycle} definitionStatus={resolved.definitionStatus} />}
      notice={dirty && <Typography.Text type="warning" aria-live="polite">Unsaved changes retained in this Instance</Typography.Text>} />
    <AdminPageHeading page={instancePages.find(item => item.id === page)} />
    {resolved && archived && new URLSearchParams(window.location.search).get('notice') === 'archived-automations' && <Alert type="info" showIcon title="Automations are unavailable for this archived Instance" description="Unarchive from Lifecycle before enabling or executing work." />}
    {effective.kind === 'loading' && <Spin aria-label="Loading effective configuration" />}
    {effective.kind === 'error' && <Alert type={effective.unauthorized ? 'warning' : 'error'} showIcon title={effective.message} action={<AdminRetryAction onRetry={onRetryEffective} diagnosticId={effective.diagnosticId} />} />}
    {resolved && <InstanceConfigurationProvider key={instanceId} revision={resolved.instanceRevision} disabled={archived || effective.kind !== 'ready'} onUpdated={onInstanceChanged}><div inert={effective.kind !== 'ready'}><Flex vertical gap={token.padding}><InstanceConfigurationActions /><InstanceConfigurationContent><Flex vertical gap={token.padding}>
        {archived && <Alert type="info" showIcon title="Archived Instance" description="Configuration is read-only. Automations cannot execute; unarchive from Lifecycle to resume owner operations." />}
        <RetainedAdminPage active={page === 'overview'}><InstanceOverview config={resolved} /></RetainedAdminPage>
        <RetainedAdminPage active={page === 'profile' || page === 'version' || page === 'lifecycle'}><InstanceManagedControls page={page === 'version' || page === 'lifecycle' ? page : 'profile'} config={resolved} onUpdated={onInstanceChanged} onDeleted={() => { releaseConsumedInstance(); onInstanceDeleted(); }} onDirtyChange={value => reportDirty('profile', value)} /></RetainedAdminPage>
        <RetainedAdminPage active={!!instanceSettingGroups[page]}>
          <div hidden={!instanceSettingGroups[page]}><AgentConfigurationPanel title="Instance settings" label="Instance settings" bodyGap="section">
            <InstanceSettingsSection instanceId={instanceId} archived={archived} page={page} active={!!instanceSettingGroups[page]} onDirtyChange={setSettingsDirty} onUpdated={onInstanceChanged} />
          </AgentConfigurationPanel></div>
        </RetainedAdminPage>
        <RetainedAdminPage active={page === 'execution'}><Flex vertical gap={token.padding}><Typography.Text type="secondary">Interactive defaults apply to the next new Run. Unattended defaults are on the Unattended model page.</Typography.Text><a href={`${adminInstancePath(instanceId, 'unattended')}#execution-defaults`} onClick={followAppLink}>Edit unattended execution defaults</a><InstanceExecutionBudgets instanceId={instanceId} archived={archived} onUpdated={onInstanceChanged} onDirtyChange={value => reportDirty('budgets', value)} /></Flex></RetainedAdminPage>
        <RetainedAdminPage active={page === 'skills'}><InstanceSkillsSection instanceId={instanceId} archived={archived} active={page === 'skills'} onUpdated={onInstanceChanged} onDirtyChange={value => reportDirty('skills', value)} /></RetainedAdminPage>
        <RetainedAdminPage active={page === 'resources'}><InstanceResourcesSection instanceId={instanceId} archived={archived} active={page === 'resources'} onUpdated={onInstanceChanged} onDirtyChange={value => reportDirty('resources', value)} /></RetainedAdminPage>
        <RetainedAdminPage active={page === 'workspace'}><InstanceWorkspaceSection instanceId={instanceId} archived={archived} /></RetainedAdminPage>
        <RetainedAdminPage active={page === 'credentials'}><section id="credentials" tabIndex={-1} className="admin-settings-anchor"><InstanceCredentialsSection instanceId={instanceId} revision={resolved.instanceRevision} archived={archived} onUpdated={onInstanceChanged} onDirtyChange={reportCredentialDirty} /></section></RetainedAdminPage>
        <RetainedAdminPage active={page === 'self-management'}><Flex vertical gap={token.padding}><SelfManagementSection instanceId={instanceId} archived={archived} onUpdated={onInstanceChanged} onDirtyChange={value => reportDirty('management', value)} />{!archived && <HarnessManagementSection instanceId={instanceId} eligibleTools={resolved.effectiveToolAllowlist} onUpdated={onInstanceChanged} />}</Flex></RetainedAdminPage>
        <RetainedAdminPage active={page === 'automations'}>{!archived ? <Flex vertical gap={token.padding}>
          <section id="registrations" tabIndex={-1}><InstanceAutomationsSection instanceId={instanceId} active={page === 'automations'} onWork={viewRun} selection={page === 'automations' ? sourceSelection : undefined} onDirtyChange={value => reportDirty('automations', value)} /></section>
        </Flex> : <Button href={adminInstancePath(instanceId, 'lifecycle')} onClick={followAppLink}>Open Lifecycle to unarchive</Button>}</RetainedAdminPage>
        <RetainedAdminPage active={page === 'unattended'}><section id="execution-defaults" tabIndex={-1} className="admin-settings-anchor"><InstanceMemoryAutomationPanel config={resolved} section="automation" onUpdated={onInstanceChanged} onDirtyChange={value => reportDirty('unattended', value)} /></section></RetainedAdminPage>
        <RetainedAdminPage active={page === 'memory'}><Flex vertical gap={token.padding}>{!archived && <IdentityMaintenanceSection instanceId={instanceId} />}<InstanceMemoryAutomationPanel config={resolved} section="memory" /></Flex></RetainedAdminPage>
        <RetainedAdminPage active={page === 'experience'}>{!archived ? <ExperienceSection instanceId={instanceId} active={page === 'experience'} onWork={viewRun} selection={page === 'experience' ? experienceSelection : undefined} /> : <Alert type="info" showIcon title="Unarchive from Lifecycle to inspect Experience." />}</RetainedAdminPage>
        <RetainedAdminPage active={page === 'sessions'}><InstanceSessionsSection instanceId={instanceId} archived={archived} open={page === 'sessions'} /></RetainedAdminPage>
        <RetainedAdminPage active={page === 'runs'}><InstanceRunsSection instanceId={instanceId} open={page === 'runs'} inline onRun={viewRun} onClose={() => go('sessions')} /></RetainedAdminPage>
        <RetainedAdminPage active={page === 'effective'}><AgentConfigurationPanel title="Resolved configuration" label="Effective configuration" description="Saved Definition and Instance values. Draft overrides are not included."><EffectiveConfigView config={resolved} hidePersona /></AgentConfigurationPanel></RetainedAdminPage>
      </Flex></InstanceConfigurationContent></Flex></div></InstanceConfigurationProvider>}
    {resolved && <InstanceRunsSection instanceId={instanceId} open={runDetailsOpen} detailsOnly selectedAgentRunId={selectedAgentRunId} onSource={viewSource} onClose={closeRun} afterClose={() => { if (restoreRunFocus.current && runOpener.current?.isConnected) runOpener.current.focus({ preventScroll: true }); }} />}
  </Flex>;
}
