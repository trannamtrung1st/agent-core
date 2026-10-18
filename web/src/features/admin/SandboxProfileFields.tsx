import type { ReactNode } from 'react';
import { Collapse, Flex, Form, Input, InputNumber, Switch, theme, type FormInstance } from 'antd';
import type { SandboxProfile } from '../../services/sandboxes';

/** Shared saved-profile authoring; each scope owns transport, credentials and command witnesses. */
export function SandboxProfileFields({ form, maxWorkingStorageBytes, advancedKeys, onAdvancedChange, identityField, registryField, onNameChange, storageLimitDescription }: {
  form: FormInstance<SandboxProfile>; maxWorkingStorageBytes: number; advancedKeys: string[]; onAdvancedChange: (keys: string[]) => void;
  storageLimitDescription?: string; identityField?: ReactNode; registryField: ReactNode; onNameChange?: (name: string) => void;
}) {
  const { token } = theme.useToken();
  return <>
        <div className="admin-settings-field-grid sandbox-settings-fields">
          <Form.Item name="name" label="Name" rules={[{ required: true, max: 120 }]}><Input aria-label="Sandbox profile name" onChange={event=>onNameChange?.(event.target.value)} /></Form.Item>
          {identityField}
        </div>
        <Form.Item name="description" label="Description"><Input.TextArea aria-label="Sandbox profile description" maxLength={2000} autoSize={{ minRows: 2, maxRows: 5 }} /></Form.Item>
        <Form.Item name="imageReference" label="Image" extra="Enter a tag such as mcr.microsoft.com/dotnet/sdk:10.0 or ubuntu:24.04, or registry/repository@sha256 with 64 lowercase hex characters. Save resolves tags to a fixed digest. Names without a registry use Docker Hub; names without a tag use latest. A local sha256 image ID belongs only to this daemon. Compatible Linux images need /bin/sh, /proc, Git and standard Unix file utilities. Agent Core prepares private filesystem access automatically. Verify checks the image, workspace access and saved setup in a disposable environment. Python and pre-created workspace directories are optional." rules={[{ required: true, whitespace: true, max: 1024 }]}><Input.TextArea aria-label="Sandbox image reference" autoSize={{minRows:2,maxRows:5}} /></Form.Item>
        {registryField}
        <Form.Item name="enabled" label="Enabled intent" valuePropName="checked" extra="Enabled profiles still require verification of each saved revision. Disabling immediately fences commands and schedules cleanup of live generations. Ordinary edits apply to new generations."><Switch aria-label="Sandbox profile enabled" /></Form.Item>
        <Collapse activeKey={advancedKeys} onChange={keys=>onAdvancedChange(Array.isArray(keys)?keys:[keys])} items={[{key:'advanced',forceRender:true,label:'Advanced settings · limits and initialization',children:<Flex vertical gap={token.padding}>
        <div className="admin-settings-field-grid sandbox-settings-fields">
          {([['cpu','CPU cores',0.25,2,0.25],['memoryMiB','Memory (MiB)',256,2048,256],['maxCommandSeconds','Maximum command (seconds)',1,180,1],['idleTtlSeconds','Idle expiry (seconds)',60,3600,60],['maxLifetimeSeconds','Maximum lifetime (seconds)',60,86400,60]] as const).map(([key,label,min,max,step]) =>
            <Form.Item key={key} name={key} label={label} dependencies={key==='maxLifetimeSeconds'?['idleTtlSeconds']:undefined}
              rules={[{ required: true, type: 'number', min, max },...(key==='maxLifetimeSeconds'?[{validator:(_:unknown,value:number)=>value<form.getFieldValue('idleTtlSeconds')?Promise.reject(new Error('Maximum lifetime must be at least the idle expiry.')):Promise.resolve()}]:[])]}><InputNumber aria-label={label} min={min} max={max} step={step} style={{ width: '100%' }} /></Form.Item>)}
          <Form.Item name="storageBytes" label="Canonical /working storage (MiB)"
            extra={`Sampled /working budget; excess or unknown usage stops execution and retains written files. Separate from shared /home and container-only storage. ${storageLimitDescription ?? `Host maximum ${(maxWorkingStorageBytes / 1048576).toLocaleString()} MiB.`}`}
            getValueProps={(bytes?:number)=>({value:bytes===undefined?null:bytes/1048576})} normalize={(mib:number|null)=>mib===null?null:Math.round(mib*1048576)}
            rules={[{required:true,type:'number',min:1,max:maxWorkingStorageBytes,message:'Choose a positive budget within this host’s maximum.'}]}>
            <InputNumber aria-label="Canonical /working storage (MiB)" min={1/1048576} max={maxWorkingStorageBytes / 1048576} step={1} style={{width:'100%'}} />
          </Form.Item>
        </div>
        <Form.Item name="initializationScript" label="Optional initialization script" extra="Runs once on canonical files after automatic filesystem checks. Failed setup may leave partial files. Verify profile tests this script in a separate disposable environment without changing Session files. Reconnect does not rerun it. Preinstall packages in the image; default commands have no external network. Limit: 16,384 UTF-8 bytes."
          rules={[{validator:(_:unknown,value:string|null)=>{
            try { encodeURIComponent(value??''); } catch { return Promise.reject(new Error('Remove invalid Unicode from the script.')); }
            if(value?.includes('\0'))return Promise.reject(new Error('Remove null characters from the script.'));
            return new TextEncoder().encode(value??'').length>16384?Promise.reject(new Error('Initialization script must fit within 16,384 UTF-8 bytes. Shorten the script, including multibyte characters.')):Promise.resolve();
          }}]}><Input.TextArea aria-label="Sandbox initialization script" maxLength={16384} autoSize={{ minRows: 4, maxRows: 14 }} /></Form.Item>
        </Flex>}]} />
  </>;
}
