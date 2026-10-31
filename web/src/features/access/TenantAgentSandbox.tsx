import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Flex, Select, Tag, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { confirmAction } from '../../app/confirmAction';
import { IdentityCompletionError, IdentityError, identityRequest, type TenantContext } from '../../services/identity';
import { tenantAgentApi, patchTenantAgentSettings } from '../../services/tenantAgentConfiguration';
import { instanceSandboxVerificationApi, readTenantInstanceSandbox, type TenantInstanceSandbox, type TenantInstanceSandboxProfile, type TenantInstanceSandboxVerification } from '../../services/tenantAgentSandbox';
import { sandboxStatusLabel } from '../../services/sandboxes';
import { InstanceSettingsSection } from '../admin/InstanceSettingsSection';
import type { InstanceSettingsTransport } from '../admin/useInstanceSettingsController';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { SandboxVerificationDetails } from '../sandbox/SandboxVerificationDetails';
import { TenantAgentHeading } from './TenantAgentHeading';
import { tenantSandboxPath } from './accessRoute';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

export function TenantAgentSandbox({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const pane = useRef<HTMLDivElement>(null); const focusAfterRead = useRef(false);
  const [view, setView] = useState<TenantInstanceSandbox>(); const [fresh, setFresh] = useState(false); const [stale, setStale] = useState(false); const [dirty, setDirty] = useState(false);
  const [settingsBusy, setSettingsBusy] = useState(false); const [verifying, setVerifying] = useState(false); const [refresh, setRefresh] = useState(0);
  const guard = useAccessDraftGuard(dirty, settingsBusy || verifying, () => {});
  const transport = useMemo<InstanceSettingsTransport>(() => ({
    list: async (_, signal) => {
      setView(undefined); setFresh(false);
      const next = await readTenantInstanceSandbox(tenant, id, signal);
      if (!signal?.aborted) { setView(next); setFresh(true); setStale(false); if (focusAfterRead.current) { focusAfterRead.current = false; requestAnimationFrame(() => pane.current?.focus({ preventScroll: true })); } }
      return next.section ? [{ ...next.section, securityRevision: next.securityRevision }] : [];
    },
    patch: async (_, section, set, clear, signal) => {
      setFresh(false);
      try { return await patchTenantAgentSettings(tenant, id, section, set, clear, signal); }
      catch (error) {
        if (error instanceof IdentityError && error.status === 409) setStale(true);
        if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) setView(undefined);
        throw error;
      }
    },
    models: async () => ({ defaultOfferingId: '', models: [] })
  }), [tenant, id]);
  const reloadSaved = () => { focusAfterRead.current = true; setRefresh(value => value + 1); };
  return <Flex ref={pane} tabIndex={-1} aria-label="Instance Sandbox configuration" vertical gap="var(--ac-space-section)" className="access-bounded-form">{guard.holder}

    {view && <TenantAgentHeading view={{ agent: { instance: { persona: { name: view.instanceName }, lifecycle: view.lifecycle, activeVersion: view.definitionVersion } }, canUpdate: view.canManage }} readOnlyDescription="Changing this saved Sandbox setup requires update and Sandbox management access on this exact agent. Verification has its own permission check." />}
    {view && !view.completeCatalog && <Alert type="info" showIcon title="Profile access is limited" description="Only readable Definition-authorized profiles are shown. Hidden profiles and their defaults are omitted." />}
    {view && !view.section && <Alert type="info" showIcon title="Saved Sandbox settings are restricted" description="The complete saved setup is unavailable with your current profile access. Readable profiles and separately authorized verification remain available below." />}
    {stale && <Alert type="warning" showIcon title="Reload before saving" description="The saved setup or access changed. Reload settings to refresh exact witnesses; your deliberate draft is retained." />}
    <InstanceSettingsSection instanceId={id} page="sandbox" archived={!view?.canManage || view.lifecycle !== 'Active'} onUpdated={() => {}} transport={transport} refreshKey={refresh} saveBlocked={stale} externalBusy={verifying} onDirtyChange={setDirty} onBusyChange={setSettingsBusy}
      confirmationContext={view ? `Agent: ${view.instanceName}. Organization: ${context.tenant.name}.` : undefined}
      sandboxContext={{ profiles: view?.profiles ?? null, summary: state => view && <InstanceSandboxEvidence key={`${tenant}:${id}`} tenant={tenant} organization={context.tenant.name} view={view} dirty={state.dirty} busy={state.busy} fresh={fresh} onBusy={setVerifying} reloadSaved={reloadSaved} /> }} />
  </Flex>;
}
function InstanceSandboxEvidence({ tenant, organization, view, dirty, busy, fresh, onBusy, reloadSaved }: { tenant: string; organization: string; view: TenantInstanceSandbox; dirty: boolean; busy: boolean; fresh: boolean; onBusy: (value: boolean) => void; reloadSaved: () => void }) {
  const [selection, setSelection] = useState<string>();
  const preferred = view.section?.effective.profileId;
  const selected = view.profiles.find(profile => profile.id === selection) ?? view.profiles.find(profile => profile.id === preferred) ?? view.profiles[0];
  return <section aria-label="Saved Instance Sandbox setup"><Flex vertical gap="var(--ac-space-section)">
    <Typography.Title level={5}>Saved setup verification</Typography.Title>
    <Typography.Paragraph type="secondary">Check the saved Profile and Instance initialization in a disposable environment. Profile compatibility, this Instance’s evidence and Session execution remain separate. Verification grants no agent permission.</Typography.Paragraph>
    {view.profiles.length ? <Select aria-label="Profile to verify" value={selected?.id} disabled={busy} options={view.profiles.map(profile => ({ value: profile.id, label: `${profile.name} · revision ${profile.revision} · ${sandboxStatusLabel(profile)}` }))} onChange={setSelection} /> : <Typography.Text type="secondary">No readable Definition-authorized Sandbox profiles. Configure the Definition and profile access before verifying setup.</Typography.Text>}
    {selected && <SavedInstanceVerification key={`${view.instanceId}:${selected.id}`} tenant={tenant} organization={organization} view={view} profile={selected} dirty={dirty} pageBusy={busy} fresh={fresh} onBusy={onBusy} reloadSaved={reloadSaved} />}
  </Flex></section>;
}
function SavedInstanceVerification({ tenant, organization, view, profile, dirty, pageBusy, fresh, onBusy, reloadSaved }: { tenant: string; organization: string; view: TenantInstanceSandbox; profile: TenantInstanceSandboxProfile; dirty: boolean; pageBusy: boolean; fresh: boolean; onBusy: (value: boolean) => void; reloadSaved: () => void }) {
  const [refresh, setRefresh] = useState(0); const [exact, setExact] = useState<string>(); const [confirming, setConfirming] = useState(false); const [blocked, setBlocked] = useState(false);
  const state = useIdentityQuery<TenantInstanceSandboxVerification>(instanceSandboxVerificationApi(tenant, view.instanceId, profile.id, exact), `${view.securityRevision}:${view.instanceRevision}:${refresh}`);
  const command = useTenantCommand(); const { modal } = App.useApp(); const layout = useAdminDetailLayout();
  const confirmation = useRef<ReturnType<typeof modal.confirm> | null>(null); const active = useRef(true);
  useEffect(() => { active.current = true; return () => { active.current = false; confirmation.current?.destroy(); onBusy(false); }; }, [onBusy]);
  const pending = command.busy || confirming;
  useEffect(() => { onBusy(pending); }, [pending, onBusy]);
  const data = state.data; const attempt = data?.attempt;
  const coherent = !!data && data.instanceId === view.instanceId && data.currentInstanceRevision === view.instanceRevision && data.currentProfileRevision === profile.revision && data.securityRevision === view.securityRevision;
  const reload = () => { command.clearError(); setBlocked(false); setRefresh(value => value + 1); };
  const status = data ? ({ Verified: 'Verified saved setup', VerificationPending: 'Verification or cleanup pending', VerificationFailed: 'Verification failed', NeedsVerification: 'Needs verification', Inactive: 'Inactive instance', Disabled: 'Profile disabled', ServerUnavailable: 'Server unavailable', SourceUnavailable: 'Source unavailable', SourceAuthorized: 'Source available' } as Record<string, string>)[data.status] ?? 'Setup unavailable' : '';
  const verify = () => {
    if (!data || !fresh || dirty || pageBusy || pending || blocked || !coherent || !data.canVerify || attempt?.active || command.requiresReload) return;
    setConfirming(true);
    const saved = data;
    confirmation.current = confirmAction(modal, { title: 'Verify saved Instance setup?', okText: 'Verify setup', content: `${view.instanceName} in ${organization}. Profile ${profile.name}, revision ${saved.currentProfileRevision}; Instance revision ${saved.currentInstanceRevision}. Run saved Profile initialization followed by saved Instance initialization in a disposable environment. Session files are not used. Failed or uncertain cleanup remains recorded; no agent permission is granted.`,
      afterClose: () => { if (active.current) setConfirming(false); }, onOk: () => { setBlocked(true); return command.run(async signal => {
        try { await identityRequest<TenantInstanceSandboxVerification>(tenantAgentApi(tenant, view.instanceId, '/sandbox/verify'), { method: 'POST', signal, body: { expectedRevision: saved.securityRevision, expectedInstanceRevision: saved.currentInstanceRevision, profileId: profile.id, expectedProfileRevision: saved.currentProfileRevision } }); }
        catch (error) { if (!(error instanceof IdentityCompletionError)) throw error; }
      }, () => { reloadSaved(); }); }
    });
  };
  return <Flex vertical gap="var(--ac-space-section)">
    <Descriptions {...layout} column={1} size="small" items={[{ label: 'Readable source', children: profile.name }, { label: 'Profile availability', children: <Flex vertical align="start" gap="var(--ac-space-tight)"><Tag>{sandboxStatusLabel(profile)}</Tag><Typography.Text type="secondary">{profile.message}</Typography.Text></Flex> }, { label: 'Immutable image', children: <Typography.Text code style={{ overflowWrap: 'anywhere' }}>{profile.imageReference}</Typography.Text> }, { label: 'Saved Instance', children: `Revision ${view.instanceRevision}` }]} />
    <Typography.Link href={tenantSandboxPath(tenant, profile.id)} onClick={followAppLink}>Inspect reusable profile</Typography.Link>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {state.error ? <AccessError error={state.error} retry={reload} /> : state.loading ? <AccessLoading /> : data && <>
      <Tag style={{ alignSelf: 'flex-start' }}>{status}</Tag>
      {!coherent && <Alert type="warning" showIcon title="Saved setup changed" description="Reload settings before verification. Current evidence cannot authorize an older saved setup." action={<Button disabled={pending || pageBusy} onClick={reloadSaved}>Refresh saved setup</Button>} />}
      {!data.canVerify && <Typography.Text type="secondary">Verification is unavailable under current Instance, profile or source access. Configuration management and verification are authorized independently.</Typography.Text>}
      {data.observation ? <SandboxVerificationDetails value={data.observation} label="Instance setup verification" /> : <Typography.Text type="secondary">No current accepted observation for this exact saved setup and source.</Typography.Text>}
      {attempt && <section aria-label="Retained verification attempt"><Typography.Title level={5}>{exact ? 'Exact retained attempt' : 'Latest retained attempt'}</Typography.Title><Typography.Paragraph>Profile revision {attempt.profileRevision} · Instance revision {attempt.instanceRevision} · {new Date(attempt.startedAt).toLocaleString()}. {attempt.active ? 'Verification or cleanup is still pending. Reload retained evidence before another attempt.' : attempt.accepted ? 'This attempt was accepted for its saved revisions.' : 'This attempt was not accepted.'}</Typography.Paragraph>{attempt.instanceRevision !== view.instanceRevision || attempt.profileRevision !== profile.revision ? <Typography.Paragraph type="secondary">Historical evidence. Saved configuration changed after this attempt.</Typography.Paragraph> : null}{!data.observation && attempt.result && <SandboxVerificationDetails value={attempt.result} label="Retained setup attempt" />}
        {!exact && <Button disabled={pending || pageBusy} onClick={() => setExact(attempt.attemptId)}>Inspect exact saved attempt</Button>}{exact && <Button disabled={pending || pageBusy} onClick={() => setExact(undefined)}>Return to latest evidence</Button>}
      </section>}
    </>}
    {(dirty || !fresh) && <Typography.Text role="status" type="secondary">Save or discard edits and reload the saved setup before verification.</Typography.Text>}
    <Flex wrap gap="var(--ac-space-compact)"><Button disabled={pending || pageBusy} onClick={reload}>Reload verification</Button><Button type="primary" aria-label="Verify Instance setup" loading={command.busy} disabled={pending || pageBusy || dirty || !fresh || blocked || command.requiresReload || state.loading || !!state.error || !coherent || !data?.canVerify || !!attempt?.active} onClick={verify}>Verify Instance setup</Button></Flex>
    {blocked && <Typography.Text type="secondary">A failed response never retries initialization. Read retained evidence and current setup before starting another attempt.</Typography.Text>}
  </Flex>;
}
