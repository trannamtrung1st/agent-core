import { Alert,DatePicker,Form,Input,Select } from 'antd';
import dayjs,{ type Dayjs } from 'dayjs';
import { ProtectedValueField } from './CredentialFields';
export type RegistryCredentialFieldsValue = {registry:string;username:string;repositories:string;expiresAtUtc?:string|null};

export function ContainerRegistryFields({edit=false}:{edit?:boolean}) {
  return <>
    <Form.Item name="displayName" label="Display name" rules={[{required:true,max:120}]}><Input autoComplete="off" /></Form.Item>
    {edit ? <Form.Item name="status" label="Status"><Select options={['Active','Disabled'].map(value=>({value,label:value}))} /></Form.Item> : null}
    <>
      <Form.Item name="registry" label="Registry host" extra="Exact lowercase host and optional port, such as ghcr.io. No URL, path or wildcard." rules={[{required:true}]}><Input autoComplete="off" /></Form.Item>
      <Form.Item name="username" label="Registry username" rules={[{required:true,max:256}]}><Input autoComplete="off" /></Form.Item>
      <Form.Item name="repositories" label="Repository scopes" extra="Optional narrower scopes, one registry/repository prefix per line. Empty permits repositories on this exact host, within the host image policy."><Input.TextArea autoSize={{minRows:2,maxRows:5}} /></Form.Item>
      <Form.Item name="expiresAtUtc" label="Expiry (optional)"
        extra={`Times shown in ${Intl.DateTimeFormat().resolvedOptions().timeZone}. Saved in UTC. Clear to remove expiry; renewing requires profile reverification.`}
        getValueProps={(value?:string|null)=>({value:value?dayjs(value):null})}
        normalize={(value:Dayjs|null)=>value?.toISOString()??null}>
        <DatePicker aria-label="Registry credential expiry" classNames={{popup:{root:'registry-date-picker-popup'}}} showTime format="YYYY-MM-DD HH:mm:ss" style={{width:'100%'}} />
      </Form.Item>
      {!edit && <Form.Item name="kind" label="Authentication kind"><Select options={['Password','Token'].map(value=>({value,label:value}))} /></Form.Item>}
      {!edit && <ProtectedValueField />}
    </>
    <Alert type="info" showIcon title="Host-side image pulls only" description="Agents cannot bind or read this credential. Rotation or disabling requires profile reverification before another generation." />
  </>;
}
