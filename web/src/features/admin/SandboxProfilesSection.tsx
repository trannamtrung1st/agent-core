import { SandboxEvidence } from "../sandbox/SandboxEvidence";
import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Collapse, Descriptions, Empty, Flex, Form, Input, InputNumber, Modal, Select, Spin, Switch, Table, Tag, Typography, theme } from 'antd';
import { getSandboxPlatform, getSandboxProfile, sandboxSourceLabel, sandboxImageRegistry, sandboxStatusLabel, sandboxEligible, saveSandboxProfile, verifySandboxProfile, checkSandboxImage, type SandboxPlatform, type SandboxProfile } from '../../services/sandboxes';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { AdminErrorNotice, showAdminFailure } from './adminFailure';
import { isAdminFormValidationFailure, showAdminFormValidation, validateAdminForm } from './adminFormFeedback';
import { useAdminUnsavedChanges } from './useAdminUnsavedChanges';
import { useAdminDetailLayout } from './useAdminDetailLayout';
import { AddCredentialDialog } from './AddCredentialDialog';
import { AdminCollectionToolbar, useAdminCollectionSearch } from './AdminCollectionToolbar';
import { SandboxProfileDetails } from './SandboxProfileDetails';
import { listCredentials, type SystemCredential } from '../../services/adminApi';
import { followAppLink } from '../../app/appLink';
import { formatAdminTimestamp } from './adminPresentation';
import { confirmAction } from '../../app/confirmAction';

const blank = (): SandboxProfile => ({ id: '', revision: 0, name: '', description: '', enabled: true, imageReference: '', initializationScript: null,
  cpu: 1, memoryMiB: 1024, pids: 64, maxCommandSeconds: 120, idleTtlSeconds: 1800, maxLifetimeSeconds: 7200, storageBytes: 250 * 1024 * 1024, lastVerifiedAt: null, verificationReason: null });
const editable = ['id','name','description','enabled','imageReference','registryCredentialId','initializationScript','cpu','memoryMiB','maxCommandSeconds','idleTtlSeconds','maxLifetimeSeconds','storageBytes'] as const;
const draftShape = (value: Partial<SandboxProfile>) => JSON.stringify(editable.map(key => value[key] ?? null));
export function SandboxProfilesSection() {
  const { token } = theme.useToken(); const { message, modal } = App.useApp(); const detail = useAdminDetailLayout();
  const {search,setSearch,pagination}=useAdminCollectionSearch();
  const [credentials,setCredentials]=useState<SystemCredential[]>([]); const [credentialDialog,setCredentialDialog]=useState(false); const [credentialError,setCredentialError]=useState(false);
  const [historyRevision,setHistoryRevision]=useState<number>(); const [historicalProfile,setHistoricalProfile]=useState<SandboxProfile>();
  const [form] = Form.useForm<SandboxProfile>(); const fields = Form.useWatch([], form);
  const [platform, setPlatform] = useState<SandboxPlatform | null>(null); const [baseline, setBaseline] = useState<SandboxProfile | null>(null);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false); const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [verificationOutput,setVerificationOutput] = useState<{stdout:string;stderr:string;truncated:boolean}|null>(null);
  const [creating, setCreating] = useState(false);
  const beforeCreate = useRef<SandboxProfile | null>(null);
  const [saveFailed, setSaveFailed] = useState(false);
  const [advancedKeys,setAdvancedKeys] = useState<string[]>([]);
  const alive=useRef(true);
  const displayed=historicalProfile??platform?.profiles.find(profile=>profile.id===baseline?.id&&profile.revision===baseline.revision)??baseline;
  const baselineRef = useRef(baseline); baselineRef.current = baseline;
  const historyRef = useRef(historyRevision); historyRef.current = historyRevision;
  const credentialRead = useRef(0);
  const active = useRef<AbortController | null>(null);
  const dirty = (!historyRevision||historyRevision===baseline?.revision) && !!baseline && !!fields && draftShape(fields) !== draftShape(baseline);
  const dirtyRef = useRef(dirty); dirtyRef.current = dirty;
  function accept(profile: SandboxProfile) { baselineRef.current=profile; historyRef.current=undefined; setHistoryRevision(undefined); setHistoricalProfile(undefined); setBaseline(profile); setVerificationOutput(profile.verification??null); form.resetFields(); form.setFieldsValue(profile); setError(null); setSaveFailed(false); }
  function finishCreate() {
    setCreating(false); setAdvancedKeys([]); setError(null);
    if (beforeCreate.current) accept(beforeCreate.current);
    else { setBaseline(null); baselineRef.current = null; form.resetFields(); }
  }
  function cancelCreate() {
    if (busy) return;
    if (!dirty) { finishCreate(); return; }
    confirmAction(modal, { title: 'Discard unsaved profile?', content: 'This prebuilt image has not been saved. Your profile edits will be discarded.', okText: 'Discard changes', cancelText: 'Keep editing', onOk: finishCreate });
  }
  useAdminUnsavedChanges('global:sandboxes', dirty, () => { if (creating) finishCreate(); else if (baseline) accept(baseline); });
  async function load(initial = false) {
    active.current?.abort(); const controller = new AbortController(); active.current = controller;
    const selected = baselineRef.current;
    setLoading(true); setError(null); setSaveFailed(false);
    try {
      const next = await getSandboxPlatform(controller.signal); if (controller.signal.aborted) return;
      setPlatform(next);
      if (!dirtyRef.current && !historyRef.current && baselineRef.current === selected) {
        if (initial && !selected && next.profiles.length) accept(next.profiles[0]);
        else if (selected?.revision) {
          const current = next.profiles.find(profile => profile.id === selected.id);
          if (current) accept(current);
        }
      }
    } catch (e) { if (!controller.signal.aborted) setError(describeAdminError(e, 'Unable to load sandbox profiles. Retry.')); }
    finally { if (!controller.signal.aborted) setLoading(false); }
  }
  async function loadCredentials() {
    const read=++credentialRead.current;
    try {const next=await listCredentials();if(!alive.current||read!==credentialRead.current)return;setCredentials(next.filter(c=>c.purpose==='ContainerRegistry'));setCredentialError(false);}
    catch {if(alive.current&&read===credentialRead.current)setCredentialError(true);}
  }
  useEffect(() => { alive.current=true; void loadCredentials(); void load(true); return () => {alive.current=false;active.current?.abort();}; }, []);
  async function save() {
    if (!baseline || busy || historyRevision&&historyRevision!==baseline.revision) return;
    try {
      const value = await validateAdminForm(form, message); if(!alive.current)return; setBusy(true); setError(null);
      const saved = await saveSandboxProfile({ ...baseline, ...value }); if(!alive.current)return; accept(saved); setCreating(false); await load(); if(alive.current)message.success('Profile saved · Needs verification. Verify this revision before using commands.');
    } catch (e) { if (!alive.current || isAdminFormValidationFailure(e)) return; setSaveFailed(true); setError(showAdminFailure(message, e, 'Profile was not saved. Your edits are retained.', 'Save failed')); }
    finally { if(alive.current)setBusy(false); }
  }
  async function verify() {
    if (!baseline || dirty || busy || !baseline.revision) return;
    setBusy(true); setError(null);
    try { const result = await verifySandboxProfile(baseline); if(!alive.current)return; accept(result.profile); setVerificationOutput({stdout:result.stdout??"",stderr:result.stderr??"",truncated:result.truncated??false}); await load(); if(alive.current)result.passed ? message.success(result.message) : message.error({key:'sandbox-verification',content:result.message,duration:6}); }
    catch (e) { if(alive.current)setError(describeAdminError(e, 'Profile verification failed. Reload status before retrying.')); }
    finally { if(alive.current)setBusy(false); }
  }
  async function checkImage() {
    if (!baseline || dirty || busy || !baseline.revision) return;setBusy(true);
    try {const result=await checkSandboxImage(baseline);if(alive.current)result.passed?message.success(result.message):message.error({key:'sandbox-verification',content:result.message,duration:6});}
    catch(e){if(alive.current)setError(describeAdminError(e,'Image readiness could not be checked. Retry.'));}finally{if(alive.current)setBusy(false);}
  }
  async function readHistory(revision:number) {
    if(!baseline||dirty||busy)return;const selected=baseline;setBusy(true);
    try {const previous=await getSandboxProfile(selected.id,revision);if(!alive.current||baselineRef.current!==selected)return;if(revision===selected.revision)accept(previous);else{setHistoryRevision(revision);setHistoricalProfile(previous);setVerificationOutput(previous.verification??null);form.setFieldsValue(previous);}}
    catch(e){if(alive.current)setError(describeAdminError(e,'Historical revision could not be read. Retry.'));}
    finally{if(alive.current)setBusy(false);}
  }
  const editor = baseline && platform && <Form form={form} layout="vertical" className="admin-config-form admin-settings-form sandbox-profile-form" disabled={busy||!!historyRevision&&historyRevision!==baseline.revision} onFinish={() => void save()}
        onFinishFailed={({errorFields})=>{if(errorFields.some(field=>['cpu','memoryMiB','maxCommandSeconds','idleTtlSeconds','maxLifetimeSeconds','storageBytes','initializationScript'].includes(String(field.name[0]))))setAdvancedKeys(['advanced']);showAdminFormValidation(message,form,{errorFields});}}>
        <div className="admin-settings-field-grid sandbox-settings-fields">
          <Form.Item name="name" label="Name" rules={[{ required: true, max: 120 }]}><Input aria-label="Sandbox profile name" onChange={event=>{if(!baseline.revision&&!form.isFieldTouched('id'))form.setFieldValue('id',event.target.value.toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/^-+|-+$/g,'').slice(0,64));}} /></Form.Item>
          <Form.Item name="id" label="Profile ID" rules={[{ required: true, pattern: /^[a-z][a-z0-9-]{0,63}$/, message: 'Use lowercase letters, digits and hyphens.' }]}><Input aria-label="Profile ID" disabled={busy || baseline.revision > 0} /></Form.Item>
        </div>
        <Form.Item name="description" label="Description"><Input.TextArea aria-label="Sandbox profile description" maxLength={2000} autoSize={{ minRows: 2, maxRows: 5 }} /></Form.Item>
        <Form.Item name="imageReference" label="Image" extra="Enter a tag such as mcr.microsoft.com/dotnet/sdk:10.0 or ubuntu:24.04, or registry/repository@sha256 with 64 lowercase hex characters. Save resolves tags to a fixed digest. Names without a registry use Docker Hub; names without a tag use latest. A local sha256 image ID belongs only to this daemon." rules={[{ required: true, whitespace: true, max: 1024 }]}><Input.TextArea aria-label="Sandbox image reference" autoSize={{minRows:2,maxRows:5}} /></Form.Item>
        <Form.Item name="registryCredentialId" label="Registry authentication" extra={<Flex vertical gap={token.paddingXS}><Typography.Text type="secondary">Public images need no credential. Local image IDs cannot use registry authentication.</Typography.Text><Flex align="center" wrap gap={token.paddingXS}><Button disabled={busy} onClick={()=>setCredentialDialog(true)}>Add registry credential</Button><a href="/admin/connections/credentials" onClick={followAppLink}>Manage System Credentials</a></Flex></Flex>}>
          <Select aria-label="Registry credential" allowClear placeholder="Public image · no credential" options={credentials.map(c=>({value:c.credentialId,label:`${c.displayName} · ${c.containerRegistry?.registry} · ${c.status}`,disabled:c.status!=='Active'||!!c.containerRegistry?.expiresAtUtc&&Date.parse(c.containerRegistry.expiresAtUtc)<=Date.now()||!!fields?.imageReference&&sandboxImageRegistry(fields.imageReference)!==c.containerRegistry?.registry}))} />
        </Form.Item>
        {credentialError && <Alert type="warning" showIcon title="Credential inventory unavailable" description="Saved selection is preserved. Retry before selecting authentication." action={<Button onClick={()=>void loadCredentials()}>Retry credentials</Button>} />}
        <Form.Item name="enabled" label="Enabled intent" valuePropName="checked" extra="Enabled profiles still require verification of each saved revision. Disabling immediately fences commands and schedules cleanup of live generations. Ordinary edits apply to new generations."><Switch aria-label="Sandbox profile enabled" /></Form.Item>
        <Collapse activeKey={advancedKeys} onChange={keys=>setAdvancedKeys(Array.isArray(keys)?keys:[keys])} items={[{key:'advanced',forceRender:true,label:'Advanced settings · limits and initialization',children:<Flex vertical gap={token.padding}>
        <div className="admin-settings-field-grid sandbox-settings-fields">
          {([['cpu','CPU cores',0.25,2,0.25],['memoryMiB','Memory (MiB)',256,2048,256],['maxCommandSeconds','Maximum command (seconds)',1,180,1],['idleTtlSeconds','Idle expiry (seconds)',60,3600,60],['maxLifetimeSeconds','Maximum lifetime (seconds)',60,86400,60]] as const).map(([key,label,min,max,step]) =>
            <Form.Item key={key} name={key} label={label} dependencies={key==='maxLifetimeSeconds'?['idleTtlSeconds']:undefined}
              rules={[{ required: true, type: 'number', min, max },...(key==='maxLifetimeSeconds'?[{validator:(_:unknown,value:number)=>value<form.getFieldValue('idleTtlSeconds')?Promise.reject(new Error('Maximum lifetime must be at least the idle expiry.')):Promise.resolve()}]:[])]}><InputNumber aria-label={label} min={min} max={max} step={step} style={{ width: '100%' }} /></Form.Item>)}
          <Form.Item name="storageBytes" label="Canonical /working storage (MiB)"
            extra="Limits canonical /working files, not the container writable layer. Maximum 250 MiB."
            getValueProps={(bytes?:number)=>({value:bytes===undefined?null:bytes/1048576})} normalize={(mib:number|null)=>mib===null?null:Math.round(mib*1048576)}
            rules={[{required:true,type:'number',min:1,max:250*1048576,message:'Set canonical /working storage between 1 byte and 250 MiB.'}]}>
            <InputNumber aria-label="Canonical /working storage (MiB)" min={1/1048576} max={250} step={1} style={{width:'100%'}} />
          </Form.Item>
        </div>
        <Form.Item name="initializationScript" label="Optional initialization script" extra="Runs once after readiness and scratch hydration, as the non-root workload user. Verify profile tests this script in a separate disposable environment without changing Session files. Reconnect does not rerun it. Preinstall packages in the image; default commands have no external network. Limit: 16,384 UTF-8 bytes."
          rules={[{validator:(_:unknown,value:string|null)=>{
            try { encodeURIComponent(value??''); } catch { return Promise.reject(new Error('Remove invalid Unicode from the script.')); }
            if(value?.includes('\0'))return Promise.reject(new Error('Remove null characters from the script.'));
            return new TextEncoder().encode(value??'').length>16384?Promise.reject(new Error('Initialization script must fit within 16,384 UTF-8 bytes. Shorten the script, including multibyte characters.')):Promise.resolve();
          }}]}><Input.TextArea aria-label="Sandbox initialization script" maxLength={16384} autoSize={{ minRows: 4, maxRows: 14 }} /></Form.Item>
        </Flex>}]} />
        <Typography.Text type="secondary">Approved registries: {platform.allowedImagePrefixes.join(', ')||'None'} · Local image IDs {platform.localImageIdsAllowed?'permitted by host':'disabled'}</Typography.Text>
        {!creating && <Flex vertical gap={token.padding}>
          <Flex justify="space-between" align="center" wrap gap={token.padding}>
            <Flex wrap gap={token.paddingXS}>
              <Button type="primary" htmlType="submit" aria-label="Save profile" aria-busy={busy} loading={busy} disabled={!dirty||!!historyRevision&&historyRevision!==baseline.revision}>Save profile</Button>
              <Button disabled={!dirty || busy} onClick={() => accept(baseline)}>Discard edits</Button>
            </Flex>
            <Flex wrap gap={token.paddingXS}>
              <Button disabled={dirty||busy||!baseline.revision||platform.state!=="Ready"||!!historyRevision&&historyRevision!==baseline.revision} onClick={()=>void checkImage()}>Check image readiness</Button>
              <Button aria-label="Verify profile" aria-busy={busy} disabled={dirty || busy || !baseline.revision || platform.state !== 'Ready'||!!historyRevision&&historyRevision!==baseline.revision} onClick={() => void verify()}>Verify profile</Button>
            </Flex>
          </Flex>
          {verificationOutput && <Flex vertical gap={token.paddingXS}><SandboxEvidence {...verificationOutput} label="Profile verification" />{displayed?.verification && <Typography.Text type="secondary">{displayed!.verification!.exitCode===null?"Exit unknown":`Exit ${displayed!.verification!.exitCode}`} · {(displayed!.verification!.durationMs/1000).toFixed(1)}s{displayed!.verification!.diagnosticId?` · Diagnostic ${displayed!.verification!.diagnosticId}`:""}</Typography.Text>}</Flex>}
          {displayed?.lastVerifiedAt && <Typography.Text role="status" type="secondary">{displayed?.verificationReason} · {formatAdminTimestamp(displayed!.lastVerifiedAt)}</Typography.Text>}
        </Flex>}
      </Form>;
  return <Flex vertical gap={token.padding}>
    {error && !creating && <Alert type="error" showIcon title={<AdminErrorNotice {...error} />} action={saveFailed ? undefined : <Button onClick={() => void load()}>Retry</Button>} />}
    {loading && !platform ? <Spin aria-label="Loading sandboxes" /> : platform && <>
      <Flex align="center" justify="space-between" wrap gap={token.paddingXS}>
        <Flex align="center" gap={token.paddingXS} style={{flex:'1 1 32rem',minWidth:0}}><Tag color={platform.state === 'Ready' ? 'success' : 'default'}>{platform.state}</Tag><Typography.Text>{platform.message}</Typography.Text></Flex>
        <Button type="text" aria-label="Refresh status" aria-busy={loading} loading={loading} onClick={() => void load()}>Refresh status</Button>
      </Flex>
      <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Profiles choose a prebuilt environment. Definition capabilities grant command access; a profile alone grants no tools. The same Session retains its environment until expiry or reset.</Typography.Paragraph>
      <AdminCollectionToolbar label="sandbox profiles" value={search} onChange={setSearch} filters={<Button type="primary" disabled={dirty||busy} onClick={()=>{beforeCreate.current=baseline;accept(blank());form.setFields([{name:'id',touched:false}]);setAdvancedKeys([]);setCreating(true);}}>Add prebuilt image</Button>} />
      <Table<SandboxProfile> rowKey="id" size="small" tableLayout="fixed" className="admin-collection-table" scroll={{x:1080}} pagination={pagination}
        dataSource={platform.profiles.filter(p=>[p.name,p.id,p.imageReference,sandboxStatusLabel(p),sandboxSourceLabel(p)].join(' ').toLowerCase().includes(search.trim().toLowerCase()))}
        locale={{emptyText:<Empty description={platform.profiles.length?'No matching profiles. Clear the search to inspect the catalog.':'No profiles yet. Add a prebuilt image, verify it, then authorize it on a Definition.'} />}}
        columns={[
          {title:'Profile',key:'name',width:190,ellipsis:true,render:(_,p)=><Button type="link" size="small" className="admin-collection-name" title={p.name} disabled={dirty||busy} onClick={()=>accept(p)}>{p.name}</Button>},
          {title:'Profile ID',dataIndex:'id',width:170,ellipsis:true,render:(id:string)=><Typography.Text type="secondary" title={id}>{id}</Typography.Text>},
          {title:'Revision',dataIndex:'revision',width:85,align:'right'},
          {title:'Source',key:'source',width:115,render:(_,p)=><Typography.Text title={p.imageReference.startsWith('sha256:')?'Installed on this daemon':p.imageReference.split('@')[0]}>{sandboxSourceLabel(p)}</Typography.Text>},
          {title:'Image',key:'image',width:330,render:(_,p)=>{
            const repository=p.imageReference.startsWith('sha256:')?'':`${p.imageReference.split('@')[0]}@`;
            const digest=`sha256:${p.imageReference.split('sha256:')[1]?.slice(0,12)}…`;
            return <Flex align="center" gap={token.paddingXXS} wrap={false}>
              {repository && <Typography.Text type="secondary" ellipsis title={p.imageReference} style={{minWidth:0}}>{repository}</Typography.Text>}
              <Typography.Text code title={p.imageReference} copyable={{text:p.imageReference}} style={{whiteSpace:'nowrap',flexShrink:0}}>{digest}</Typography.Text>
            </Flex>;
          }},
          {title:'Availability',key:'status',width:190,render:(_,p)=><Tag color={sandboxEligible(p)?'success':p.availability==='VerificationFailed'?'error':'default'} style={{whiteSpace:'nowrap',marginInlineEnd:0}}>{sandboxStatusLabel(p)}</Tag>}
        ]} />
      {baseline && !creating && <Flex vertical gap={token.paddingXS}>
        <Typography.Title level={5} style={{margin:0}}>{baseline.revision?baseline.name:'Add prebuilt image'}</Typography.Title>
        {baseline.revision>0 && <><Typography.Text>{sandboxStatusLabel(displayed!)} · revision {displayed!.revision}</Typography.Text><Typography.Text type="secondary">{displayed?.availabilityMessage}</Typography.Text></>}
        {baseline.revision>1 && <Select aria-label="Profile revision history" value={historyRevision??baseline.revision} disabled={dirty||busy} style={{width:'100%',maxWidth:'20rem'}} options={Array.from({length:baseline.revision},(_,i)=>({value:i+1,label:`Revision ${i+1}${i+1===baseline.revision?' · Current':''}`}))} onChange={revision=>void readHistory(revision)} />}
      </Flex>}
      {!creating && editor}
      <Modal open={creating} title="Add prebuilt image" aria-label="Add prebuilt image" className="sandbox-profile-dialog" centered width={800} destroyOnHidden
        onCancel={cancelCreate} onOk={()=>form.submit()} okText="Save profile" confirmLoading={busy}
        closable={!busy} keyboard={!busy&&!credentialDialog} mask={{closable:false}} cancelButtonProps={{disabled:busy}}
        okButtonProps={{disabled:!dirty||busy}}>
        <Flex vertical gap={token.padding}>
          {error && <Alert type="error" showIcon title={<AdminErrorNotice {...error} />} />}
          {creating && editor}
        </Flex>
      </Modal>
      {baseline && !creating && baseline.revision>0 && <SandboxProfileDetails profile={historicalProfile??baseline} />}
      <AddCredentialDialog open={credentialDialog} initialPurpose="Registry" onClose={()=>setCredentialDialog(false)} onClosed={()=>{}} onSaved={loadCredentials} />
      <Descriptions {...detail} bordered size="small" column={1}>
        <Descriptions.Item label="Network">Offline · deny-default egress</Descriptions.Item><Descriptions.Item label="Workload protections">Non-root · 64 PIDs · capabilities dropped · no Docker socket</Descriptions.Item>
        <Descriptions.Item label="Storage">{platform.storageScope}</Descriptions.Item><Descriptions.Item label="Deployment scope">{platform.securityScope}</Descriptions.Item>
      </Descriptions>
    </>}
  </Flex>;
}
