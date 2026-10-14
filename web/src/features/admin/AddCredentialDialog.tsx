import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Alert, App, Flex, Form, Modal, Select, theme } from 'antd';
import {
  createCredential,
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

type Purpose = 'Drive' | 'Gmail' | 'Custom' | 'General';
type Fields = CredentialFieldsValue & EmailAccountFieldsValue;
const choices = [
  { value: 'Drive', label: 'Google Drive account' },
  { value: 'Gmail', label: 'Gmail account' },
  { value: 'Custom', label: 'Custom email account' },
  { value: 'General', label: 'General credential' },
];
export function AddCredentialDialog({
  open,
  onClose,
  onSaved,
  onClosed,
}: {
  open: boolean;
  onClose: () => void;
  onSaved: () => Promise<void>;
  onClosed: () => void;
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
    select('Drive');
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
  }, [open, form]);
  function close() {
    if (inFlight.current) return;
    form.resetFields();
    setCreated(undefined);
    setError(undefined);
    onClose();
  }
  async function save() {
    if (inFlight.current) return;
    const values = await form.validateFields();
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError(undefined);
    try {
      if (purpose === 'Drive') await createDriveAccount(values.displayName);
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
        describeAdminError(
          e,
          created
            ? 'The account was saved. Retry connecting with Google.'
            : 'Credential could not be saved or connected. Enter the protected value again to retry.',
        ),
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
          ) : purpose === 'General' ? (
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
