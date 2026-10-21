import { useEffect, useState } from 'react';
import { Alert, Button, Flex, Form, Input, Typography } from 'antd';
import { identityRequest, type TenantContext } from '../../services/identity';
import { tenantProfileApi, type TenantUserProfile as Profile } from '../../services/tenantMemory';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';
const fields = [ ['preferredName', 'Preferred name', 128], ['language', 'Language', 32], ['locale', 'Locale', 35], ['timeZone', 'Time zone', 64] ] as const;
export function TenantUserProfile({ context }: { context: TenantContext }) {
  return <ProfileEditor key={`${context.tenant.tenantId}:${context.principalId}`} context={context} />;
}
function ProfileEditor({ context }: { context: TenantContext }) {
  const path = tenantProfileApi(context.tenant.tenantId); const view = useIdentityQuery<Profile>(path);
  const [draft, setDraft] = useState<Record<string, string>>({}); const [changed, setChanged] = useState<string[]>([]);
  const [busy, setBusy] = useState(false); const [epoch, setEpoch] = useState(0); const [notice, setNotice] = useState(false);
  const guard = useAccessDraftGuard(changed.length > 0, busy, () => {});
  useEffect(() => { if (view.data) setDraft(previous => Object.fromEntries(fields.map(([key]) => [key, changed.includes(key) ? previous[key] ?? '' : view.data!.profile.preferences[key]?.value ?? '']))); }, [view.data]);
  const reload = () => { setEpoch(e => e + 1); view.reload(); setNotice(false); };
  const discard = () => { setDraft(Object.fromEntries(fields.map(([key]) => [key, view.data?.profile.preferences[key]?.value ?? '']))); setChanged([]); setNotice(false); };
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">{guard.holder}
    <AgentConfigurationPanel title="Your organization profile" label="Your organization profile" bodyGap="default"
      description={`These preferences belong to you in ${context.tenant.name}. Other organizations keep separate values. Agents use them only when their current access and Profile evidence policy allow it.`}
      extra={<Button disabled={busy} onClick={reload}>Reload saved profile</Button>}>
      {notice && <Alert type="success" showIcon role="status" title="Profile saved" />}
      {view.loading ? <AccessLoading /> : view.error ? <AccessError error={view.error} retry={reload} /> : view.data && <>
        <Typography.Paragraph type="secondary">Blank fields are cleared when saved. These preferences do not change your Account name, a conversation’s speech locale, or learned Memory.</Typography.Paragraph>
        <Form layout="vertical" disabled={busy}>
          {fields.map(([key, label, max]) => <Form.Item key={key} label={label} htmlFor={`user-profile-${key}`} extra={changed.includes(key) ? `Saved: ${view.data!.profile.preferences[key]?.value ?? 'Not set'}` : undefined}>
            <Input id={`user-profile-${key}`} value={draft[key] ?? ''} maxLength={max} autoComplete="off" onChange={e => { setDraft(d => ({ ...d, [key]: e.target.value })); setChanged(c => c.includes(key) ? c : [...c, key]); setNotice(false); }}
              placeholder={key === 'language' ? 'For example, en' : key === 'locale' ? 'For example, en-GB' : key === 'timeZone' ? 'For example, Asia/Ho_Chi_Minh' : undefined} />
          </Form.Item>)}
        </Form>
        <ProfileActions key={epoch} path={path} view={view.data} values={Object.fromEntries(changed.map(key => [key, draft[key]?.trim() || null]))} busy={setBusy}
          onSaved={() => { setChanged([]); setNotice(true); view.reload(); }} onDiscard={discard} dirty={changed.length > 0} />
      </>}
    </AgentConfigurationPanel>
  </Flex>;
}
function ProfileActions({ path, view, values, busy, onSaved, onDiscard, dirty }: { path: string; view: Profile; values: Record<string, string | null>; busy: (v: boolean) => void; onSaved: () => void; onDiscard: () => void; dirty: boolean }) {
  const command = useTenantCommand(); useEffect(() => { busy(command.busy); return () => busy(false); }, [command.busy]);
  return <Flex vertical gap="var(--ac-space-default)">
    {command.error !== undefined && <><AccessError error={command.error} /><Alert type="warning" showIcon title="Read current saved values before another save" description="Your draft is retained. Reload saved profile, compare each changed field with its saved value, then choose Save deliberately. The request is not repeated." /></>}
    <Flex wrap gap="var(--ac-space-compact)"><Button type="primary" loading={command.busy} disabled={!dirty || command.error !== undefined} onClick={() => void command.run(signal => identityRequest(path, { method: 'PATCH', signal, body: { expectedRevision: view.securityRevision, expectedProfileRevision: view.profile.revision, values } }), onSaved)}>Save profile</Button>
      <Button disabled={!dirty || command.busy} onClick={onDiscard}>Discard changes</Button></Flex>
  </Flex>;
}
