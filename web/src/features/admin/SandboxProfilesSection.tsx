import { SandboxEvidence } from "../sandbox/SandboxEvidence";
import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Empty, Flex, Form, Input, InputNumber, Select, Spin, Switch, Tag, Typography, theme } from 'antd';
import { getSandboxPlatform, saveSandboxProfile, verifySandboxProfile, checkSandboxImage, type SandboxPlatform, type SandboxProfile } from '../../services/sandboxes';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { AdminErrorNotice } from './adminFailure';
import { useAdminUnsavedChanges } from './useAdminUnsavedChanges';
import { useAdminDetailLayout } from './useAdminDetailLayout';
import { formatAdminTimestamp } from './adminPresentation';

const blank = (): SandboxProfile => ({ id: '', revision: 0, name: '', description: '', enabled: true, imageReference: '', initializationScript: null,
  cpu: 1, memoryMiB: 1024, pids: 64, maxCommandSeconds: 120, idleTtlSeconds: 1800, maxLifetimeSeconds: 7200, storageBytes: 250 * 1024 * 1024, lastVerifiedAt: null, verificationReason: null });
const editable = ['id','name','description','enabled','imageReference','initializationScript','cpu','memoryMiB','maxCommandSeconds','idleTtlSeconds','maxLifetimeSeconds'] as const;
const draftShape = (value: Partial<SandboxProfile>) => JSON.stringify(editable.map(key => value[key] ?? null));
export function SandboxProfilesSection() {
  const { token } = theme.useToken(); const { message } = App.useApp(); const detail = useAdminDetailLayout();
  const [form] = Form.useForm<SandboxProfile>(); const fields = Form.useWatch([], form);
  const [platform, setPlatform] = useState<SandboxPlatform | null>(null); const [baseline, setBaseline] = useState<SandboxProfile | null>(null);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false); const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [verificationOutput,setVerificationOutput] = useState<{stdout:string;stderr:string;truncated:boolean}|null>(null);
  const active = useRef<AbortController | null>(null);
  const dirty = !!baseline && !!fields && draftShape(fields) !== draftShape(baseline);
  const dirtyRef = useRef(dirty); dirtyRef.current = dirty;
  function accept(profile: SandboxProfile) { setBaseline(profile); setVerificationOutput(profile.verification??null); form.resetFields(); form.setFieldsValue(profile); setError(null); }
  useAdminUnsavedChanges('global:sandboxes', dirty, () => { if (baseline) accept(baseline); });
  async function load(initial = false) {
    active.current?.abort(); const controller = new AbortController(); active.current = controller;
    setLoading(true); setError(null);
    try {
      const next = await getSandboxPlatform(controller.signal); if (controller.signal.aborted) return;
      setPlatform(next); if (initial && !dirtyRef.current && next.profiles.length) accept(next.profiles[0]);
    } catch (e) { if (!controller.signal.aborted) setError(describeAdminError(e, 'Unable to load sandbox profiles. Retry.')); }
    finally { if (!controller.signal.aborted) setLoading(false); }
  }
  useEffect(() => { void load(true); return () => active.current?.abort(); }, []);
  async function save() {
    if (!baseline || busy) return;
    try {
      const value = await form.validateFields(); setBusy(true); setError(null);
      const saved = await saveSandboxProfile({ ...baseline, ...value }); accept(saved); await load(); message.success('Profile saved. New generations use this revision.');
    } catch (e) { if (e && typeof e === 'object' && 'errorFields' in e) return; setError(describeAdminError(e, 'Profile was not saved. Your edits are retained.')); }
    finally { setBusy(false); }
  }
  async function verify() {
    if (!baseline || dirty || busy || !baseline.revision) return;
    setBusy(true); setError(null);
    try { const result = await verifySandboxProfile(baseline); accept(result.profile); setVerificationOutput({stdout:result.stdout??"",stderr:result.stderr??"",truncated:result.truncated??false}); await load(); result.passed ? message.success(result.message) : message.error(result.message); }
    catch (e) { setError(describeAdminError(e, 'Profile verification failed. Reload status before retrying.')); }
    finally { setBusy(false); }
  }
  async function checkImage() {
    if (!baseline || dirty || busy || !baseline.revision) return;setBusy(true);
    try {const result=await checkSandboxImage(baseline);result.passed?message.success(result.message):message.error(result.message);}
    catch(e){setError(describeAdminError(e,'Image readiness could not be checked. Retry.'));}finally{setBusy(false);}
  }
  return <Flex vertical gap={token.padding}>
    {error && <Alert type="error" showIcon title={<AdminErrorNotice {...error} />} action={<Button onClick={() => void load()}>Retry</Button>} />}
    {loading && !platform ? <Spin aria-label="Loading sandboxes" /> : platform && <>
      <Flex align="center" wrap gap={token.paddingXS}><Tag color={platform.state === 'Ready' ? 'success' : 'default'}>{platform.state}</Tag><Typography.Text>{platform.message}</Typography.Text><Button type="text" loading={loading} onClick={() => void load()}>Refresh status</Button></Flex>
      <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Profiles choose a prebuilt environment. Definition capabilities grant command access; a profile alone grants no tools. The same Session retains its environment until expiry or reset.</Typography.Paragraph>
      <Flex wrap gap={token.paddingXS} align="center">
        <Select aria-label="Sandbox profile" value={baseline?.revision ? baseline.id : undefined} disabled={dirty || busy} placeholder="Choose a profile" className="instance-settings-control"
          options={platform.profiles.map(p => ({ value: p.id, label: `${p.name} · revision ${p.revision}${p.enabled ? '' : ' · Disabled'}` }))} onChange={id => { const profile = platform.profiles.find(p => p.id === id); if (profile) accept(profile); }} />
        <Button disabled={dirty || busy} onClick={() => accept(blank())}>New profile</Button>
      </Flex>
      {!baseline && <Empty description="No sandbox profiles. Build the Workbench and configure its immutable image, or add an approved prebuilt image." />}
      {baseline && <Form form={form} layout="vertical" className="admin-config-form admin-settings-form" disabled={busy} onFinish={() => void save()}>
        <Form.Item name="id" label="Profile ID" rules={[{ required: true, pattern: /^[a-z][a-z0-9-]{0,63}$/, message: 'Use lowercase letters, digits and hyphens.' }]}><Input aria-label="Profile ID" disabled={busy || baseline.revision > 0} /></Form.Item>
        <Form.Item name="name" label="Name" rules={[{ required: true, max: 120 }]}><Input aria-label="Sandbox profile name" /></Form.Item>
        <Form.Item name="description" label="Description"><Input.TextArea aria-label="Sandbox profile description" maxLength={2000} autoSize={{ minRows: 2, maxRows: 5 }} /></Form.Item>
        <Form.Item name="imageReference" label="Immutable image" extra="Use an image pinned by sha256 from the host's approved registry. Local image IDs require explicit host configuration." rules={[{ required: true }]}><Input aria-label="Sandbox image reference" /></Form.Item>
        <Form.Item name="enabled" label="Available for commands" valuePropName="checked" extra="Disabling immediately fences commands and schedules cleanup of live generations. Ordinary edits apply to new generations."><Switch aria-label="Sandbox profile enabled" /></Form.Item>
        <Flex className="admin-form-row" wrap gap={token.padding}>
          {([['cpu','CPU cores',0.25,2,0.25],['memoryMiB','Memory (MiB)',256,2048,256],['maxCommandSeconds','Maximum command (seconds)',1,180,1],['idleTtlSeconds','Idle expiry (seconds)',60,3600,60],['maxLifetimeSeconds','Maximum lifetime (seconds)',60,86400,60]] as const).map(([key,label,min,max,step]) =>
            <Form.Item key={key} name={key} label={label} style={{ flex: '1 1 12rem' }} rules={[{ required: true, type: 'number', min, max }]}><InputNumber aria-label={label} min={min} max={max} step={step} style={{ width: '100%' }} /></Form.Item>)}
        </Flex>
        <Form.Item name="initializationScript" label="Optional initialization script" extra="Runs once after readiness and scratch hydration, as the non-root workload user. Verify profile tests this script in a separate disposable environment without changing Session files. Reconnect does not rerun it. Preinstall packages in the image; default commands have no external network."><Input.TextArea aria-label="Sandbox initialization script" maxLength={16384} autoSize={{ minRows: 4, maxRows: 14 }} /></Form.Item>
        <Flex vertical gap={token.padding}>
          <Flex wrap gap={token.paddingXS}>
            <Button type="primary" htmlType="submit" aria-label="Save profile" aria-busy={busy} loading={busy} disabled={!dirty}>Save profile</Button>
            <Button disabled={!dirty || busy} onClick={() => accept(baseline)}>Discard edits</Button>
            <Button disabled={dirty||busy||!baseline.revision||platform.state!=="Ready"} onClick={()=>void checkImage()}>Check image readiness</Button>
            <Button aria-label="Verify profile" aria-busy={busy} disabled={dirty || busy || !baseline.revision || platform.state !== 'Ready'} onClick={() => void verify()}>Verify profile</Button>
          </Flex>
          {verificationOutput && <Flex vertical gap={token.paddingXS}><SandboxEvidence {...verificationOutput} label="Profile verification" />{baseline.verification && <Typography.Text type="secondary">{baseline.verification.exitCode===null?"Exit unknown":`Exit ${baseline.verification.exitCode}`} · {(baseline.verification.durationMs/1000).toFixed(1)}s{baseline.verification.diagnosticId?` · Diagnostic ${baseline.verification.diagnosticId}`:""}</Typography.Text>}</Flex>}
          {baseline.lastVerifiedAt && <Typography.Text role="status" type="secondary">{baseline.verificationReason} · {formatAdminTimestamp(baseline.lastVerifiedAt)}</Typography.Text>}
        </Flex>
      </Form>}
      <Descriptions {...detail} bordered size="small" column={1}>
        <Descriptions.Item label="Network">Offline · deny-default egress</Descriptions.Item><Descriptions.Item label="Workload protections">Non-root · 64 PIDs · capabilities dropped · no Docker socket</Descriptions.Item>
        <Descriptions.Item label="Storage">{platform.storageScope}</Descriptions.Item><Descriptions.Item label="Deployment scope">{platform.securityScope}</Descriptions.Item>
      </Descriptions>
    </>}
  </Flex>;
}
