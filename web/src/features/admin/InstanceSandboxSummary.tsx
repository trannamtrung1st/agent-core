import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Descriptions, Flex, Spin, Tag, Typography, theme } from 'antd';
import { getInstanceSandbox, verifyInstanceSandbox, type SandboxVerification } from '../../services/sandboxes';
import { SandboxProfileDetails } from './SandboxProfileDetails';
import { SandboxEvidence } from '../sandbox/SandboxEvidence';
import { useAdminDetailLayout } from './useAdminDetailLayout';

export function InstanceSandboxSummary({ instanceId, revision, verifySetup, archived, dirty }: { instanceId:string; revision?:number; verifySetup?:boolean; archived?:boolean; dirty?:boolean }) {
  const { token } = theme.useToken(); const layout = useAdminDetailLayout(); const {message}=App.useApp();
  const [value,setValue]=useState<Awaited<ReturnType<typeof getInstanceSandbox>>|null>(null);
  const [error,setError]=useState<string|null>(null); const [busy,setBusy]=useState(false); const [refresh,setRefresh]=useState(0);
  const [verification,setVerification]=useState<SandboxVerification|null>(null);
  const epoch=useRef(0);
  useEffect(()=>{ epoch.current++;setBusy(false);setValue(null);setError(null);setVerification(null);const controller=new AbortController();
    getInstanceSandbox(instanceId,controller.signal).then(next=>{if(!controller.signal.aborted){setValue(next);setVerification(next.verification?.result??null);}})
      .catch(()=>{if(!controller.signal.aborted)setError('Sandbox configuration unavailable. Retry before verifying setup.');});return()=>{epoch.current++;controller.abort();};
  },[instanceId,revision,refresh]);
  async function verify() {
    if (!value || archived || dirty || busy) return; const current=epoch.current;setBusy(true);setError(null);
    try { const result=await verifyInstanceSandbox(instanceId,value.instanceRevision);if(current!==epoch.current)return;setVerification(result);result.passed?message.success(result.message):message.error(result.message); }
    catch {if(current===epoch.current)setError('Setup verification was not confirmed. Refresh configuration and inspect retained evidence before retrying.');}
    finally{if(current===epoch.current)setBusy(false);}
  }
  const profile=value?.profile;
  return <section className="admin-effective-config-section admin-effective-config-wide" aria-label="Effective sandbox configuration"><Flex vertical gap={token.paddingSM}>
    <Typography.Title level={5}>Effective Sandbox environment</Typography.Title>
    {error && <Alert showIcon type="warning" title={error} action={<Button disabled={busy} onClick={()=>setRefresh(v=>v+1)}>Retry</Button>} />}
    {!error && !value ? <Spin aria-label="Loading effective sandbox" /> : value && <>
      <Flex wrap gap={token.paddingXS} align="center"><Tag color={value.state==='Ready'&&profile?.enabled?'success':'default'}>{profile?.enabled?value.state:'Unavailable'}</Tag><Typography.Text>{value.message}</Typography.Text></Flex>
      <a href="/admin/platform/sandboxes">Manage Global Sandbox Profiles</a>
      <Descriptions {...layout} bordered size="small" column={1}>
        <Descriptions.Item label="Selected profile">{profile?`${profile.name} · revision ${profile.revision}`:'No authorized configured profile'}</Descriptions.Item>
        <Descriptions.Item label="Allowed profiles">{value.policy?.allowedProfileIds.join(', ')||'Historical bounded sandbox.run compatibility only'}</Descriptions.Item>
        <Descriptions.Item label="Configuration">Saved Instance revision {value.instanceRevision}. Existing generations retain their pinned setup until expiry or explicit reset in the Session Sandbox drawer.</Descriptions.Item>
      </Descriptions>
      {profile && <SandboxProfileDetails profile={profile} instanceInitializationScript={value.instanceInitializationScript} />}
      {verifySetup && <Button style={{alignSelf:'flex-start'}} loading={busy} disabled={archived||dirty||!profile?.enabled||value.state!=='Ready'} onClick={()=>void verify()}>Verify Instance setup</Button>}
      {dirty && verifySetup && <Typography.Text type="secondary">Save or discard edits before verifying the saved setup.</Typography.Text>}
      {verification && <Flex vertical gap={token.paddingXS} role="status"><Alert showIcon type={verification.passed?'success':'error'} title={verification.message} />
        <Typography.Text type="secondary">Verified Profile revision {verification.profileRevision} · Instance revision {verification.instanceRevision??value.instanceRevision} · {new Date(verification.verifiedAt).toLocaleString()} · {verification.exitCode===null?'Exit unknown':`Exit ${verification.exitCode}`} · {(verification.durationMs/1000).toFixed(1)}s</Typography.Text>
        <SandboxEvidence stdout={verification.stdout} stderr={verification.stderr} truncated={verification.truncated} label="Setup verification" />
      </Flex>}
    </>}
  </Flex></section>;
}
