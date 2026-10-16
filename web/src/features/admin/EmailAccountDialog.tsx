import { showAdminFailure } from './adminFailure';
import { validateAdminForm } from './adminFormFeedback';
import { useEffect, useRef, useState } from 'react';
import {
  Alert,
  App,
  Flex,
  Form,
  Input,
  InputNumber,
  Modal,
  Select,
  Typography,
  theme,
} from 'antd';
import {
  createEmailAccount,
  emailOAuthConfiguration,
  startEmailOAuth,
  updateEmailAccount,
  type EmailAccountProfile,
  type SystemCredential,
} from '../../services/adminApi';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { AdminErrorNotice } from './adminFailure';
import { GmailConnectionPanel, googleSetupUrl } from './GmailConnectionPanel';

export type EmailAccountFieldsValue = {
  displayName: string;
  status: string;
  profile: EmailAccountProfile;
  protectedValue?: string;
};
export const gmail: EmailAccountProfile = {
  preset: 'Gmail',
  emailAddress: '',
  imapUsername: '',
  smtpUsername: '',
  imapEndpoint: null,
  smtpEndpoint: null,
};
export const custom: EmailAccountProfile = {
  preset: 'Custom',
  emailAddress: '',
  imapUsername: '',
  smtpUsername: '',
  imapEndpoint: { host: '', port: 993, tlsMode: 'ImplicitTls' },
  smtpEndpoint: { host: '', port: 465, tlsMode: 'ImplicitTls' },
};
export function EmailAccountDialog({
  open,
  account,
  onClose,
  onSaved,
  onClosed,
}: {
  open: boolean;
  account?: SystemCredential;
  onClose: () => void;
  onSaved: () => Promise<void>;
  onClosed?: () => void;
}) {
  const { token } = theme.useToken();
  const { message } = App.useApp();
  const [form] = Form.useForm<EmailAccountFieldsValue>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AdminFailureNotice>();
  const pendingRestoration = useRef<
    { displayName?: string; status?: string } | undefined
  >(undefined);
  const [restored, setRestored] = useState(false);
  const [created, setCreated] = useState<SystemCredential>();
  const [configuration, setConfiguration] = useState<{
    configured: boolean;
    synthetic: boolean;
  }>();
  const preset =
    Form.useWatch(['profile', 'preset'], form) ??
    account?.emailAccount?.preset ??
    'Gmail';
  const current = account ?? created;
  useEffect(() => {
    if (open) {
      setError(undefined);
      void emailOAuthConfiguration()
        .then(setConfiguration)
        .catch((e) =>
          setError(
            describeAdminError(
              e,
              'Unable to load Google connection configuration. Close and retry.',
            ),
          ),
        );
    }
  }, [open]);
  useEffect(() => {
    if (!open) return;
    form.setFieldsValue(
      account
        ? {
            displayName: account.displayName,
            status: account.status,
            profile: account.emailAccount!,
          }
        : { displayName: 'Gmail account', status: 'Active', profile: gmail },
    );
    setRestored(false);
    if (account?.emailAccount?.preset === 'Gmail') {
      const key = 'agent-core.gmail-detail-draft:' + account.credentialId;
      try {
        const saved = sessionStorage.getItem(key);
        sessionStorage.removeItem(key);
        if (saved) {
          const value = JSON.parse(saved) as {
            displayName?: string;
            status?: string;
          };
          if (
            value.displayName &&
            value.displayName.length <= 120 &&
            (value.status === 'Active' || value.status === 'Disabled')
          ) {
            pendingRestoration.current = value;
          }
        }
      } catch {
        /* Storage failure never grants authority or blocks connection. */
      }
    }
  }, [open, account?.credentialId, form]);
  async function preserveDetails() {
    if (!current || !form.isFieldsTouched(['displayName', 'status'])) return;
    const value = await validateAdminForm(form, message, ['displayName', 'status']);
    // Safe name/status only, same-tab only. Never store provider authorization or secrets.
    try {
      sessionStorage.setItem(
        'agent-core.gmail-detail-draft:' + current.credentialId,
        JSON.stringify({
          displayName: value.displayName,
          status: value.status,
        }),
      );
    } catch {
      throw new Error(
        'Pending account details could not be preserved. Save them before reconnecting.',
      );
    }
  }
  function clearStoredDetails() {
    if (current) {
      try {
        sessionStorage.removeItem(
          'agent-core.gmail-detail-draft:' + current.credentialId,
        );
      } catch {
        /* Cleanup cannot block a committed save or dismissal. */
      }
    }
  }
  function close() {
    if (busy) return;
    clearStoredDetails();
    form.resetFields();
    setCreated(undefined);
    setError(undefined);
    onClose();
  }
  async function save() {
    const values = await validateAdminForm(form, message);
    setBusy(true);
    setError(undefined);
    const profile: EmailAccountProfile =
      preset === 'Gmail' ? (current?.emailAccount ?? gmail) : values.profile;
    try {
      if (current) {
        await updateEmailAccount(current, {
          displayName: values.displayName,
          status: values.status,
          profile,
        });
        clearStoredDetails();
      } else {
        const saved = await createEmailAccount({
          displayName: values.displayName,
          profile,
          ...(preset === 'Custom'
            ? { protectedValue: values.protectedValue! }
            : {}),
        });
        setCreated(saved);
        if (preset === 'Gmail') {
          const start = await startEmailOAuth(saved);
          window.location.assign(start.authorizationUrl);
          return;
        }
      }
      form.resetFields();
      onClose();
      await onSaved();
      message.success('Email account saved');
    } catch (e) {
      setError(
        showAdminFailure(message,
          e,
          'Account could not be saved or connected. Refresh after a conflict and retry.',
          'Save failed'),
      );
    } finally {
      form.setFieldValue('protectedValue', undefined);
      setBusy(false);
    }
  }
  return (
    <Modal
      className="admin-credential-dialog"
      centered
      width={640}
      open={open}
      title={account ? 'Edit email account' : 'Add email account'}
      afterOpenChange={(shown) => {
        if (shown && pendingRestoration.current) {
          form.setFieldsValue(pendingRestoration.current);
          pendingRestoration.current = undefined;
          setRestored(true);
        }
      }}
      afterClose={onClosed}
      onCancel={close}
      onOk={() => void save().catch(() => {})}
      confirmLoading={busy}
      closable={!busy}
      keyboard={!busy}
      cancelButtonProps={{ disabled: busy }}
      okText={
        preset === 'Gmail' && !current
          ? 'Connect with Google'
          : 'Save email account'
      }
      okButtonProps={{
        disabled: preset === 'Gmail' && !current && !configuration?.configured,
        type:
          current?.emailAccount?.preset === 'Gmail' &&
          current.emailAccount.connectionStatus !== 'Connected'
            ? 'default'
            : 'primary',
      }}
      mask={{ closable: false }}
      destroyOnHidden
    >
      <Flex vertical gap={token.padding}>
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
        {restored && (
          <Alert
            type="info"
            showIcon
            title="Unsaved account details restored"
            description="Save applies the display name and credential status. Connection actions take effect immediately."
          />
        )}
        <Form
          name="email-account"
          form={form}
          initialValues={
            account
              ? {
                  displayName: account.displayName,
                  status: account.status,
                  profile: account.emailAccount!,
                }
              : {
                  displayName: 'Gmail account',
                  status: 'Active',
                  profile: gmail,
                }
          }
          className="admin-config-form"
          layout="vertical"
          disabled={busy}
          autoComplete="off"
          preserve={false}
        >
          <EmailAccountFields
            form={form}
            preset={preset}
            account={account}
            current={current}
            configuration={configuration}
            beforeConnect={preserveDetails}
            onSaved={onSaved}
          />
        </Form>
      </Flex>
    </Modal>
  );
}

export function EmailAccountFields({
  form,
  preset,
  account,
  current,
  configuration,
  beforeConnect,
  onSaved,
  fixedPreset = false,
}: {
  form: Pick<import('antd').FormInstance, 'setFieldValue'>;
  preset: string;
  account?: SystemCredential;
  current?: SystemCredential;
  configuration?: { configured: boolean; synthetic: boolean };
  beforeConnect?: () => Promise<void>;
  onSaved: () => Promise<void>;
  fixedPreset?: boolean;
}) {
  const { token } = theme.useToken();
  return (
    <>
      {' '}
      {!fixedPreset && (
        <Form.Item
          name={['profile', 'preset']}
          label="Provider"
          rules={[{ required: true }]}
        >
          <Select
            disabled={!!current}
            options={[
              { value: 'Gmail', label: 'Gmail · Connect with Google' },
              { value: 'Custom', label: 'Custom IMAP/SMTP · Advanced' },
            ]}
            onChange={(value) => {
              form.setFieldValue('profile', value === 'Gmail' ? gmail : custom);
              form.setFieldValue('protectedValue', undefined);
            }}
          />
        </Form.Item>
      )}
      <Form.Item
        name="displayName"
        label="Display name"
        rules={[{ required: true, max: 120 }]}
      >
        <Input />
      </Form.Item>
      {account && (
        <Form.Item name="status" label="Credential status">
          <Select
            options={[
              { value: 'Active', label: 'Active' },
              { value: 'Disabled', label: 'Disabled' },
            ]}
          />
        </Form.Item>
      )}
      {preset === 'Gmail' ? (
        <>
          {current ? (
            <Flex vertical gap={token.paddingSM}>
              <Typography.Text type="secondary">
                Save applies the display name and credential status. Connection
                actions below take effect immediately.
              </Typography.Text>
              <GmailConnectionPanel
                beforeConnect={beforeConnect}
                showCredentialStatus={false}
                account={current}
                onChanged={onSaved}
              />
            </Flex>
          ) : (
            <>
              <Flex vertical gap={token.paddingSM}>
                <Typography.Paragraph>
                  Authorize Google once. Agent Core securely refreshes access as
                  needed. Your verified Google address supplies the sender.
                </Typography.Paragraph>
                <Alert
                  type="info"
                  showIcon
                  title="Read mailbox; send only after a separate Agent Core approval"
                  description="Drafts are kept in Agent Core. Connecting never sends email or grants an agent access; grant an agent access separately."
                />
                {configuration?.synthetic && (
                  <Alert
                    type="info"
                    showIcon
                    title="Synthetic — no Google connection"
                    description="The next page simulates consent offline. No real mailbox is accessed or email delivered."
                  />
                )}
                {configuration && !configuration.configured && (
                  <Alert
                    type="warning"
                    showIcon
                    title="Google connection not configured"
                    description={
                      <span>
                        Ask the operator to complete{' '}
                        <a
                          href={googleSetupUrl}
                          target="_blank"
                          rel="noreferrer"
                        >
                          Google OAuth setup
                        </a>
                        .
                      </span>
                    }
                  />
                )}
              </Flex>
            </>
          )}
        </>
      ) : (
        <>
          <Form.Item
            name={['profile', 'emailAddress']}
            label="Sender email address"
            rules={[{ required: true, type: 'email', max: 254 }]}
            extra="This account supplies From. The agent cannot choose another sender."
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Alert
            type="info"
            showIcon
            title="Custom endpoints require the operator’s host/port allowlist"
            description="Only public DNS destinations and verified TLS are allowed. Local/private mail servers are blocked."
          />
          {(['imap', 'smtp'] as const).map((protocol) => (
            <Flex vertical gap={token.paddingXS} key={protocol}>
              <Typography.Text strong>
                {protocol === 'imap' ? 'Incoming (IMAP)' : 'Outgoing (SMTP)'}
              </Typography.Text>
              <Form.Item
                name={['profile', `${protocol}Username`]}
                label={`${protocol.toUpperCase()} username`}
                rules={[{ required: true, max: 254 }]}
              >
                <Input />
              </Form.Item>
              <Form.Item
                name={['profile', `${protocol}Endpoint`, 'host']}
                label={`${protocol.toUpperCase()} host`}
                rules={[{ required: true, max: 253 }]}
              >
                <Input />
              </Form.Item>
              <Form.Item
                name={['profile', `${protocol}Endpoint`, 'port']}
                label={`${protocol.toUpperCase()} port`}
                rules={[{ required: true }]}
              >
                <InputNumber min={1} max={65535} />
              </Form.Item>
              <Form.Item
                name={['profile', `${protocol}Endpoint`, 'tlsMode']}
                label={`${protocol.toUpperCase()} TLS`}
                rules={[{ required: true }]}
              >
                <Select
                  options={[
                    { value: 'ImplicitTls', label: 'Required implicit TLS' },
                    { value: 'StartTls', label: 'Required STARTTLS' },
                  ]}
                />
              </Form.Item>
            </Flex>
          ))}
          {!account && (
            <Form.Item
              name="protectedValue"
              label="Password"
              rules={[{ required: true }]}
              extra="Encrypted on the server. Never read back or shared with the agent."
            >
              <Input.Password
                visibilityToggle={false}
                autoComplete="new-password"
                spellCheck={false}
              />
            </Form.Item>
          )}
        </>
      )}
    </>
  );
}
