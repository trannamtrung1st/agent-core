import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { useState, type ReactNode } from 'react';
import { Alert, Button, Flex, Form, Input, Typography, message } from 'antd';
import { appMessageConfig } from '../../app/appFeedback';
import { parseAccessRoute } from './accessRoute';
import { identityRequest, resetIdentityCsrf, type Account, type AccountContext } from '../../services/identity';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { accessErrorMessage } from './AccessFeedback';

export function AccountFormFrame({ title, description, children }: { title: string; description: string; children: ReactNode }) {
  return <main className="access-auth-page"><div className="access-auth-form"><Typography.Text strong>Agent Core</Typography.Text><div className="admin-page-heading"><Typography.Title level={2}>{title}</Typography.Title><Typography.Paragraph type="secondary">{description}</Typography.Paragraph></div>{children}</div></main>;
}
function safeReturnPath() {
  const candidate = new URLSearchParams(window.location.search).get('returnTo');
  if (!candidate) return '/account/organizations';
  try { const url = new URL(candidate, window.location.origin); const route = parseAccessRoute(url.pathname);
    return url.origin === window.location.origin && route && route.view !== 'sign-in' && route.view !== 'not-found' && route.view !== 'invitation' ? url.pathname : '/account/organizations';
  } catch { return '/account/organizations'; }
}
export function SignIn() {
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>();
  const [form] = Form.useForm<{ login: string; password: string }>(); const [feedback, holder] = message.useMessage(appMessageConfig);
  const notice = new URLSearchParams(window.location.search).get('notice');
  const submit = async (values: { login: string; password: string }) => {
    setBusy(true); setError(undefined);
    try { await identityRequest<Account>('/api/v2/auth/login', { method: 'POST', body: values }); resetIdentityCsrf(); window.location.replace(safeReturnPath()); }
    catch (reason) { setError(reason); setBusy(false); }
  };
  return <AccountFormFrame title="Sign in" description="One account for all your organizations. Each organization controls its own access.">{holder}
    {notice === 'password-changed' && <Alert type="success" showIcon title="Password changed. Sign in with your new password." />}
    {notice === 'expired' && <Alert type="info" showIcon title="Your session ended. Sign in again to continue." />}
    <Form form={form} layout="vertical" onFinish={values => void submit(values)} onFinishFailed={reason => showAdminFormValidation(feedback, form, reason)} disabled={busy}>
      {error !== undefined && <div className="access-form-error"><Alert type="error" showIcon role="alert" title={accessErrorMessage(error)} /></div>}
      <Form.Item name="login" label="Email" rules={[{ required: true, message: 'Enter your account email.' }, { type: 'email', message: 'Enter a valid email address.' }]}><Input autoFocus autoComplete="username" type="email" maxLength={254} /></Form.Item>
      <Form.Item name="password" label="Password" rules={[{ required: true, message: 'Enter your password.' }]}><Input.Password autoComplete="current-password" maxLength={256} /></Form.Item>
      <Button type="primary" htmlType="submit" block loading={busy}>Sign in</Button>
    </Form>
    <Typography.Paragraph type="secondary">New here? Open your organization's invitation link to create an account. For first-time setup, contact your platform operator.</Typography.Paragraph>
  </AccountFormFrame>;
}
export function AccountProfile({ context }: { context: AccountContext }) {
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>();
  const [form] = Form.useForm<{ currentPassword: string; newPassword: string; confirmPassword: string }>(); const [feedback, holder] = message.useMessage(appMessageConfig);
  const save = async (values: { currentPassword: string; newPassword: string }) => {
    setBusy(true); setError(undefined);
    try { await identityRequest('/api/v2/account/password', { method: 'POST', body: values }); form.resetFields(); resetIdentityCsrf(); window.location.replace('/sign-in?notice=password-changed'); }
    catch (reason) { setError(reason); setBusy(false); }
  };
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">{holder}
    <Flex vertical gap="var(--ac-space-tight)"><Typography.Text strong>{context.account.displayName}</Typography.Text><Typography.Text type="secondary">{context.account.loginKey.toLowerCase()}</Typography.Text></Flex>
    <AgentConfigurationPanel title="Change password" label="Change password" description="Changing your password signs you out on every device. Your organization memberships stay the same.">
    <Form form={form} layout="vertical" disabled={busy} onFinish={values => void save(values)} onFinishFailed={reason => showAdminFormValidation(feedback, form, reason)}>
      {error !== undefined && <div className="access-form-error"><Alert type="error" showIcon role="alert" title={accessErrorMessage(error)} /></div>}
      <Form.Item label="Current password" name="currentPassword" rules={[{ required: true, message: 'Enter your current password.' }]}><Input.Password autoComplete="current-password" maxLength={256} /></Form.Item>
      <Form.Item label="New password" name="newPassword" extra="Use 12–256 characters." rules={[{ required: true, message: 'Enter a new password.' }, { min: 12, max: 256, message: 'Use 12–256 characters.' }]}><Input.Password autoComplete="new-password" maxLength={256} /></Form.Item>
      <Form.Item label="Confirm new password" name="confirmPassword" dependencies={['newPassword']} rules={[{ required: true, message: 'Confirm your new password.' }, ({ getFieldValue }) => ({ validator(_, value: string) { return !value || getFieldValue('newPassword') === value ? Promise.resolve() : Promise.reject(new Error('The new passwords do not match.')); } })]}><Input.Password autoComplete="new-password" maxLength={256} /></Form.Item>
      <Button type="primary" htmlType="submit" loading={busy}>Change password</Button>
    </Form></AgentConfigurationPanel>
  </Flex>;
}
