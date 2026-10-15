import { showAdminFailure } from './adminFailure';
import { validateAdminForm } from './adminFormFeedback';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, App, DatePicker, Flex, Form, Input, Modal, Select, theme } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import {
  createCredential,
  createContainerRegistry,
  createEmailAccount,
  emailOAuthConfiguration,
  startEmailOAuth,
  type SystemCredential,
} from '../../services/adminApi';
import { createDriveAccount, getDriveOptions } from '../../services/drive';
import {
  CredentialFields,
  safeFields,
  type CredentialFieldsValue,
  ProtectedValueField,
} from './CredentialFields';
import { DriveAccountFields } from './DriveAccountDialog';
import {
  EmailAccountFields,
  gmail,
  custom,
  type EmailAccountFieldsValue,
} from './EmailAccountDialog';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { AdminErrorNotice } from './adminFailure';

type Purpose = 'Drive' | 'Gmail' | 'Custom' | 'General' | 'Registry';
export type RegistryCredentialFieldsValue = {registry:string;username:string;repositories:string;expiresAtUtc?:string|null};
type Fields = CredentialFieldsValue & EmailAccountFieldsValue & RegistryCredentialFieldsValue;
const choices = [
  { value: 'Drive', label: 'Google Drive account' },
  { value: 'Gmail', label: 'Gmail account' },
  { value: 'Custom', label: 'Custom email account' },
  { value: 'Registry', label: 'Container registry' },
  { value: 'General', label: 'General credential' },
];
export function AddCredentialDialog({
  open,
  onClose,
  onSaved,
  onClosed,
  initialPurpose = 'Drive',
}: {
  open: boolean;
  onClose: () => void;
  onSaved: () => Promise<void>;
  onClosed: () => void;
  initialPurpose?:Purpose;
}) {
  const { token } = theme.useToken();
  const { message } = App.useApp();
  const [form] = Form.useForm<Fields>();
  const [purpose, setPurpose] = useState<Purpose>('Drive');
  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);
  const [created, setCreated] = useState<SystemCredential>();
  const [error, setError] = useState<AdminFailureNotice>();
  const [configuration, setConfiguration] = useState<{
    configured: boolean;
    synthetic: boolean;
  }>();
  const [driveEnabled, setDriveEnabled] = useState<boolean>();
  function select(value: Purpose) {
    form.resetFields();
    setError(undefined);
    setCreated(undefined);
    setPurpose(value);
  }
  useLayoutEffect(() => {
    if (!open) return;
    form.setFieldsValue({
      displayName: purpose === 'Gmail' ? 'Gmail account' : undefined,
      kind: 'Password',
      status: 'Active',
      origins: '',
      metadata: [],
      profile:
        purpose === 'Custom' ? structuredClone(custom) : structuredClone(gmail),
      protectedValue: undefined,
    });
  }, [purpose, open, form]);
  useEffect(() => {
    if (!open) return;
    select(initialPurpose);
    setConfiguration(undefined);
    setDriveEnabled(undefined);
    let cancelled = false;
    void Promise.all([emailOAuthConfiguration(), getDriveOptions()])
      .then(([email, drive]) => {
        if (!cancelled) {
          setConfiguration(email);
          setDriveEnabled(drive.enabled);
        }
      })
      .catch((e) => {
        if (!cancelled)
          setError(
            describeAdminError(
              e,
              'Connection settings could not be loaded. Close and retry.',
            ),
          );
      });
    return () => {
      cancelled = true;
      form.resetFields();
    };
  }, [open, form, initialPurpose]);
  function close() {
    if (inFlight.current) return;
    form.resetFields();
    setCreated(undefined);
    setError(undefined);
    onClose();
  }
  async function save() {
    if (inFlight.current) return;
    const values = await validateAdminForm(form, message);
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError(undefined);
    try {
      if (purpose === 'Drive') await createDriveAccount(values.displayName);
      else if (purpose === 'Registry') await createContainerRegistry({displayName:values.displayName, kind:values.kind, protectedValue:values.protectedValue!, profile:{registry:values.registry, username:values.username, repositoryPrefixes:(values.repositories??'').split(/\r?\n/).map(v=>v.trim()).filter(Boolean),expiresAtUtc:values.expiresAtUtc??null}});
      else if (purpose === 'General')
        await createCredential({
          ...safeFields(values),
          kind: values.kind,
          protectedValue: values.protectedValue!,
        });
      else {
        const saved =
          created ??
          (await createEmailAccount({
            displayName: values.displayName,
            profile:
              purpose === 'Gmail'
                ? gmail
                : { ...values.profile, preset: 'Custom' },
            ...(purpose === 'Custom'
              ? { protectedValue: values.protectedValue! }
              : {}),
          }));
        setCreated(saved);
        if (purpose === 'Gmail') {
          const start = await startEmailOAuth(saved);
          window.location.assign(start.authorizationUrl);
          return;
        }
      }
      form.resetFields();
      onClose();
      await onSaved();
      message.success('Credential saved');
    } catch (e) {
      setError(
        showAdminFailure(message,
          e,
          created
            ? 'The account was saved. Retry connecting with Google.'
            : 'Credential could not be saved or connected. Enter the protected value again to retry.',
          'Save failed'),
      );
    } finally {
      form.setFieldValue('protectedValue', undefined);
      inFlight.current = false;
      setBusy(false);
    }
  }
  return (
    <Modal
      aria-label="Add credential"
      className="admin-credential-dialog"
      centered
      width={640}
      open={open}
      title="Add credential"
      onCancel={close}
      afterClose={onClosed}
      onOk={() => void save().catch(() => {})}
      confirmLoading={busy}
      closable={!busy}
      keyboard={!busy}
      mask={{ closable: false }}
      cancelButtonProps={{ disabled: busy }}
      okText={
        purpose === 'Gmail'
          ? 'Connect with Google'
          : purpose === 'Drive'
            ? 'Create account'
            : 'Save credential'
      }
      okButtonProps={{
        disabled: busy || (purpose === 'Gmail' && !configuration?.configured),
      }}
      destroyOnHidden
    >
      <Flex vertical gap={token.padding}>
        <div>
          <label htmlFor="credential-purpose">Credential purpose</label>
          <Select
            id="credential-purpose"
            aria-label="Credential purpose"
            value={purpose}
            disabled={busy || !!created}
            options={choices}
            onChange={select}
            style={{ width: '100%' }}
          />
        </div>
        {error && (
          <Alert
            type="error"
            showIcon
            title={
              <AdminErrorNotice
                message={error.message}
                diagnosticId={error.diagnosticId}
                showDetailsLabel
              />
            }
          />
        )}
        {created && (
          <Alert
            type="info"
            showIcon
            title="Account saved"
            description="Retry connects this saved account. Its purpose and display name are fixed for this attempt."
          />
        )}
        {purpose === 'Drive' && (
          <Alert
            type={driveEnabled === false ? 'warning' : 'info'}
            showIcon
            title={
              driveEnabled === false
                ? 'Google Drive is disabled on this host'
                : 'Connect after saving'
            }
            description="Save the account, then open its details to connect Google. Agents need a separate explicit binding. Only app-authorized files are available."
          />
        )}
        <Form
          name="add-credential"
          initialValues={{
            kind: 'Password',
            status: 'Active',
            metadata: [],
            origins: '',
          }}
          form={form}
          className="admin-config-form"
          layout="vertical"
          disabled={busy || !!created}
          preserve={false}
          autoComplete="off"
        >
          {purpose === 'Drive' ? (
            <DriveAccountFields />
          ) : purpose === 'Registry' ? <ContainerRegistryFields /> : purpose === 'General' ? (
            <CredentialFields create />
          ) : (
            <EmailAccountFields
              fixedPreset
              form={form}
              preset={purpose}
              configuration={configuration}
              onSaved={onSaved}
            />
          )}
        </Form>
      </Flex>
    </Modal>
  );
}

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
