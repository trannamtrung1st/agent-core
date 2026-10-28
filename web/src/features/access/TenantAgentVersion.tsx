import { useEffect, useRef, useState, type ComponentRef } from 'react';
import { Alert, App, Button, Collapse, Empty, Flex, Form, Pagination, Select, Typography } from 'antd';
import { IdentityCompletionError, IdentityError, identityRequest, type TenantContext } from '../../services/identity';
import { tenantAgentApi, type TenantAgentVersion, type TenantAgentVersionCatalog, type TenantAgentVersionPreview } from '../../services/tenantAgentConfiguration';
import { followAppLink } from '../../app/appLink';
import { confirmAction } from '../../app/confirmAction';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { InstanceVersionChanges } from '../admin/InstanceVersionChanges';
import { AccessError, AccessLoading } from './AccessFeedback';
import { TenantAgentHeading } from './TenantAgentHeading';
import { tenantAccessPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

const lostRead = (error: unknown) => error instanceof IdentityError && [401, 403, 404].includes(error.status);
export function TenantAgentVersion({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const { modal, message } = App.useApp(); const command = useTenantCommand();
  const [page, setPage] = useState(0); const [refresh, setRefresh] = useState(0); const [choice, setChoice] = useState<TenantAgentVersion>();
  const [request, setRequest] = useState<{ version: number; epoch: number; security: number; instance: number }>();
  const [committed, setCommitted] = useState(false); const [confirming, setConfirming] = useState(false); const [readDenied, setReadDenied] = useState<unknown>();
  const reloadButton = useRef<ComponentRef<typeof Button>>(null);
  const epoch = useRef(0); const alive = useRef(false); const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);
  const catalog = useIdentityQuery<TenantAgentVersionCatalog>(tenantAgentApi(tenant, id, `/versions?page=${page}&pageSize=10`), String(refresh));
  const result = useIdentityQuery<TenantAgentVersionPreview>(request ? tenantAgentApi(tenant, id, `/active-version/preview?version=${request.version}`) : null, String(request?.epoch));
  useEffect(() => { alive.current = true; return () => { alive.current = false; epoch.current++; confirmation.current?.destroy(); }; }, []);
  useEffect(() => { if (catalog.data) setCommitted(false); }, [catalog.data]);
  useEffect(() => {
    const error = lostRead(result.error) ? result.error : lostRead(catalog.error) ? catalog.error : undefined;
    if (error) { setReadDenied(error); setChoice(undefined); epoch.current++; confirmation.current?.destroy(); }
  }, [catalog.error, result.error]);
  const guard = useAccessDraftGuard(!!choice && !committed, command.busy || confirming, () => {});
  const view = catalog.data; const preview = result.data; const busy = command.busy || confirming; const active = view?.agent.instance.lifecycle === 'Active';
  const matches = !!preview && !!request && !!view && request.epoch === epoch.current && request.version === choice?.version
    && request.security === view.securityRevision && request.instance === view.agent.instance.revision
    && preview.securityRevision === view.securityRevision && preview.preview.instanceRevision === view.agent.instance.revision
    && preview.preview.currentVersion === view.agent.instance.activeVersion && preview.preview.targetVersion === choice?.version;
  const mismatch = !!request && !!preview && !matches;
  const needsReload = mismatch || command.error instanceof IdentityError && [401, 403, 404, 409].includes(command.error.status) || result.error instanceof IdentityError && result.error.status === 409;
  function retire() { epoch.current++; setRequest(undefined); confirmation.current?.destroy(); confirmation.current = null; }
  function reload() { retire(); command.clearError(); setReadDenied(undefined); setRefresh(value => value + 1); }
  function previewChoice() { if (!view || !choice || busy || !active || committed || needsReload) return; setRequest({ version: choice.version, epoch: ++epoch.current, security: view.securityRevision, instance: view.agent.instance.revision }); }
  function adopt() {
    if (!view || !preview || !request || !matches || !view.canAdopt || !preview.canAdopt || !active || busy || committed || command.requiresReload) return;
    const reviewed = preview; const reviewEpoch = request.epoch; let submitted = false; setConfirming(true);
    confirmation.current = confirmAction(modal, { title: `Adopt Definition v${reviewed.preview.targetVersion}?`, okText: 'Adopt version',
      content: <Flex vertical gap="var(--ac-space-compact)"><Typography.Text>Agent: {view.agent.instance.persona.name}. Organization: {context.tenant.name}.</Typography.Text><Typography.Text>Definition v{reviewed.preview.currentVersion} → v{reviewed.preview.targetVersion}.</Typography.Text><Typography.Text>{reviewed.preview.activationNotice}</Typography.Text></Flex>,
      onOk: async () => {
        if (!alive.current || epoch.current !== reviewEpoch) return;
        submitted = true; epoch.current++; setRequest(undefined);
        await command.run(async signal => {
          try { await identityRequest(tenantAgentApi(tenant, id, '/active-version'), { method: 'PUT', signal, body: { version: reviewed.preview.targetVersion, expectedRevision: reviewed.securityRevision, expectedInstanceRevision: reviewed.preview.instanceRevision } }); }
          catch (error) { if (!(error instanceof IdentityCompletionError)) throw error; }
        }, () => { setCommitted(true); setChoice(undefined); message.success('Definition version adopted. Reading current configuration.'); setRefresh(value => value + 1); });
      }, afterClose: () => { confirmation.current = null; if (alive.current) { setConfirming(false); if (submitted) requestAnimationFrame(() => { if (alive.current) reloadButton.current?.focus(); }); } }
    });
  }
  const denied = readDenied ?? (lostRead(result.error) ? result.error : undefined);
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">{guard.holder}
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Link href={tenantAccessPath(tenant, 'agents')} onClick={followAppLink}>Back to instances</Typography.Link><Button ref={reloadButton} disabled={busy} onClick={reload}>Reload versions</Button></Flex>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {committed && <Alert type="info" showIcon title="Version adoption was saved" description="Read the current configuration before another change. Reload never replays adoption." />}
    {denied ? <AccessError error={denied} retry={reload} /> : catalog.loading ? <AccessLoading /> : catalog.error ? <AccessError error={catalog.error} retry={reload} /> : view && <>
      {view.agent.managedKind && <Alert type="info" showIcon title="System-managed instance" description={`Updates are automatic. Applied Definition v${view.agent.instance.activeVersion}; manual version selection is unavailable.`} />}
      <TenantAgentHeading view={{ agent: view.agent, canUpdate: view.canAdopt }} configuration={!view.agent.managedKind} />
      <AgentConfigurationPanel title={view.agent.managedKind ? "System release history" : "Choose a version"} label="Definition version selection" description={view.agent.managedKind ? "Inspect release differences. System releases are applied automatically; previewing makes no change." : "Choose from this Instance’s Definition only. Previewing makes no change."} bodyGap="section">
        <Collapse items={[{ key: 'source', label: 'Definition source', children: <Typography.Text className="admin-instance-id">{view.agent.instance.definitionId}</Typography.Text> }]} />
        {view.items.length ? <Form layout="vertical" className="admin-config-form"><Form.Item label="Target Definition version"><Select aria-label="Target Definition version" placeholder="Choose an active version" value={choice?.version} disabled={busy || committed || !active} onChange={version => { retire(); setChoice(view.items.find(item => item.version === version)); }} options={view.items.map(item => ({ value: item.version, label: `v${item.version} · ${item.status}${item.version === view.agent.instance.activeVersion ? ' · Current' : ''}`, disabled: item.status !== 'Active' || item.version === view.agent.instance.activeVersion }))} /></Form.Item></Form> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No versions are available on this page." />}
        {view.total > 10 && <Pagination size="small" aria-label="Definition version pages" current={page + 1} total={view.total} pageSize={10} showSizeChanger={false} disabled={busy || committed} onChange={value => { retire(); setChoice(undefined); setPage(value - 1); }} />}
        <Flex wrap gap="var(--ac-space-compact)"><Button type="primary" aria-label="Preview changes" disabled={!choice || busy || committed || !active || needsReload} loading={!!request && result.loading} onClick={previewChoice}>Preview changes</Button><Button disabled={!choice || busy || committed} onClick={() => { retire(); setChoice(undefined); }}>Clear selection</Button></Flex>
        {!view.items.some(item => item.status === 'Active' && item.version !== view.agent.instance.activeVersion) && <Typography.Text type="secondary">No other active version is available on this page. Deprecated versions cannot be adopted.</Typography.Text>}
        {choice && <Typography.Text type="secondary">Selected v{choice.version} is not applied. Leaving discards this review.</Typography.Text>}
      </AgentConfigurationPanel>
      {request && <AgentConfigurationPanel title={`Review Definition v${request.version}`} label="Definition version preview" description="Review current saved facts before an explicit adoption." bodyGap="section">
        {result.loading ? <AccessLoading /> : result.error ? <><AccessError error={result.error} retry={needsReload ? reload : previewChoice} /><Typography.Text type="secondary">Choose another active version, or review the Instance configuration with an administrator.</Typography.Text></> : mismatch ? <AccessError error={new IdentityError(409, 'stale_revision')} retry={reload} /> : matches && preview && <>
          <Typography.Text strong>Definition v{preview.preview.currentVersion} → v{preview.preview.targetVersion}</Typography.Text>
          {(!preview.childAccess.allSkills || !preview.childAccess.allResources) && <Alert type="info" showIcon title="Child details follow your read access" description="Only readable inherited items are listed. This view does not report hidden items or counts." />}
          <InstanceVersionChanges preview={preview.preview} />
          {!view.agent.managedKind && <Button type="primary" disabled={!view.canAdopt || !preview.canAdopt || command.busy || committed || command.requiresReload} loading={command.busy} onClick={adopt}>Adopt Definition v{preview.preview.targetVersion}</Button>}
        </>}
      </AgentConfigurationPanel>}
    </>}
  </Flex>;
}
