import { useEffect, useState } from 'react';
import { Alert, App, Descriptions, Flex, Form, Input, InputNumber, Modal, Select, Typography, theme } from 'antd';
import { createEmailAccount, updateEmailAccount, type EmailAccountProfile, type SystemCredential } from '../../services/adminApi';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { AdminErrorNotice } from './adminFailure';
import { useAdminDetailLayout } from './useAdminDetailLayout';

type Fields = { displayName: string; status: string; profile: EmailAccountProfile; protectedValue?: string };
export function EmailAccountDialog({ open, account, onClose, onSaved }: { open: boolean; account?: SystemCredential; onClose: () => void; onSaved: () => Promise<void> }) {
  const { token } = theme.useToken(); const { message } = App.useApp(); const detailLayout = useAdminDetailLayout();
  const [form] = Form.useForm<Fields>(); const [busy, setBusy] = useState(false); const [error, setError] = useState<AdminFailureNotice>();
  const preset = Form.useWatch(['profile', 'preset'], form) ?? 'Gmail';
  const initialValues = account ? { displayName: account.displayName, status: account.status, profile: account.emailAccount! }
    : { status: 'Active', profile: { preset: 'Gmail', emailAddress: '', imapUsername: '', smtpUsername: '', imapEndpoint: { host: 'imap.gmail.com', port: 993, tlsMode: 'ImplicitTls' }, smtpEndpoint: { host: 'smtp.gmail.com', port: 465, tlsMode: 'ImplicitTls' } } };
  useEffect(() => { if (open) setError(undefined); }, [open]);
  function close() { if (busy) return; form.resetFields(); setError(undefined); onClose(); }
  async function save() {
    const values = await form.validateFields(); setBusy(true); setError(undefined);
    const profile: EmailAccountProfile = values.profile.preset === 'Gmail'
      ? { ...values.profile, imapUsername: values.profile.emailAddress, smtpUsername: values.profile.emailAddress,
        imapEndpoint: { host: 'imap.gmail.com', port: 993, tlsMode: 'ImplicitTls' }, smtpEndpoint: account?.emailAccount?.preset === 'Gmail' ? account.emailAccount.smtpEndpoint : { host: 'smtp.gmail.com', port: 465, tlsMode: 'ImplicitTls' } } : values.profile;
    try {
      if (account) await updateEmailAccount(account, { displayName: values.displayName, status: values.status, profile });
      else await createEmailAccount({ displayName: values.displayName, profile, protectedValue: values.protectedValue! });
      form.resetFields(); onClose(); await onSaved(); message.success('Email account saved');
    } catch (e) { setError(describeAdminError(e, 'Account could not be saved. Refresh after a revision conflict; enter the password again to retry.')); }
    finally { form.setFieldValue('protectedValue', undefined); setBusy(false); }
  }
  return <Modal className="admin-credential-dialog" centered width={640} open={open} title={account ? 'Edit email account' : 'Add email account'}
    onCancel={close} onOk={() => void save().catch(() => {})} confirmLoading={busy} closable={!busy} keyboard={!busy}
    cancelButtonProps={{ disabled: busy }} okText="Save email account" mask={{ closable: false }} destroyOnHidden>
    <Flex vertical gap={token.padding}>
      {error && <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} />}
      <Form name="email-account" form={form} initialValues={initialValues} className="admin-config-form" layout="vertical" disabled={busy} autoComplete="off" preserve={false}>
        <Form.Item name="displayName" label="Display name" rules={[{ required: true, max: 120 }]}><Input /></Form.Item>
        <Form.Item name={['profile', 'emailAddress']} label="Sender email address" rules={[{ required: true, type: 'email', max: 254 }]} extra="This account supplies From. The agent cannot choose another sender."><Input autoComplete="off" /></Form.Item>
        <Form.Item name={['profile', 'preset']} label="Provider" rules={[{ required: true }]}><Select options={[{ value: 'Gmail', label: 'Gmail (IMAP/SMTP)' }, { value: 'Custom', label: 'Custom IMAP/SMTP' }]} /></Form.Item>
        {account && <Form.Item name="status" label="Status"><Select options={[{ value: 'Active', label: 'Active' }, { value: 'Disabled', label: 'Disabled' }]} /></Form.Item>}
        {preset === 'Gmail' ? <>
          <Descriptions bordered size="small" column={1} {...detailLayout} items={[
            { key: 'imap', label: 'Incoming', children: 'imap.gmail.com:993 · Required implicit TLS' },
            { key: 'smtp', label: 'Outgoing', children: account?.emailAccount?.preset === 'Gmail' && account.emailAccount.smtpEndpoint.port === 587 ? 'smtp.gmail.com:587 · Required STARTTLS' : 'smtp.gmail.com:465 · Required implicit TLS' }
          ]} />
          <Typography.Paragraph type="secondary">Use a dedicated Gmail account with two-step verification and an App Password. Some managed accounts and Advanced Protection do not support App Passwords. <a href="https://support.google.com/accounts/answer/185833" target="_blank" rel="noreferrer">Google setup guidance</a></Typography.Paragraph>
        </> : <>
          <Alert type="info" showIcon title="Custom endpoints require the operator's host/port allowlist" description="Only public DNS destinations and verified TLS are allowed. Local/private mail servers are blocked." />
          {(['imap', 'smtp'] as const).map(protocol => <Flex vertical gap={token.paddingXS} key={protocol}>
            <Typography.Text strong>{protocol === 'imap' ? 'Incoming (IMAP)' : 'Outgoing (SMTP)'}</Typography.Text>
            <Form.Item name={['profile', `${protocol}Username`]} label={`${protocol.toUpperCase()} username`} rules={[{ required: true, max: 254 }]}><Input /></Form.Item>
            <Form.Item name={['profile', `${protocol}Endpoint`, 'host']} label={`${protocol.toUpperCase()} host`} rules={[{ required: true, max: 253 }]}><Input /></Form.Item>
            <Form.Item name={['profile', `${protocol}Endpoint`, 'port']} label={`${protocol.toUpperCase()} port`} rules={[{ required: true }]}><InputNumber min={1} max={65535} /></Form.Item>
            <Form.Item name={['profile', `${protocol}Endpoint`, 'tlsMode']} label={`${protocol.toUpperCase()} TLS`} rules={[{ required: true }]}><Select options={[{ value: 'ImplicitTls', label: 'Required implicit TLS' }, { value: 'StartTls', label: 'Required STARTTLS' }]} /></Form.Item>
          </Flex>)}
        </>}
        {!account && <Form.Item name="protectedValue" label={preset === 'Gmail' ? 'App Password' : 'Password'} rules={[{ required: true }]} extra="Encrypted on the server. Never read back or shared with the agent."><Input.Password visibilityToggle={false} autoComplete="new-password" spellCheck={false} /></Form.Item>}
      </Form>
    </Flex>
  </Modal>;
}
