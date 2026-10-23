import { useEffect, useState } from 'react';
import { Alert, Button, Flex, Form, Input, Segmented, Typography, message } from 'antd';
import { appMessageConfig } from '../../app/appFeedback';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { identityRequest, resetIdentityCsrf, type Invitation, type InvitationProof } from '../../services/identity';
import { AccountFormFrame } from './AccountForms';
import { AccessError, AccessLoading, accessErrorMessage } from './AccessFeedback';
import { tenantAccessPath } from './accessRoute';

export function InvitationAcceptance({ invitationId }: { invitationId: string }) {
  const [proof] = useState<InvitationProof>(() => { const data = new URLSearchParams(window.location.hash.slice(1)); return { invitationId, tenantId: data.get('tenant') ?? '', token: data.get('token') ?? '' }; });
  const [invitation, setInvitation] = useState<Invitation>(); const [loadError, setLoadError] = useState<unknown>();
  const [mode, setMode] = useState('Create account'); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>();
  const [form] = Form.useForm<{ displayName?: string; password: string }>(); const [feedback, holder] = message.useMessage(appMessageConfig);
  useEffect(() => {
    // Reopening the original link after its fragment was removed is a same-document
    // navigation. Reload that purpose URL so it gets a fresh read and no old form/error.
    const reopen = () => {
      const data = new URLSearchParams(window.location.hash.slice(1));
      if (data.has('tenant') || data.has('token')) window.location.reload();
    };
    window.addEventListener('hashchange', reopen);
    return () => window.removeEventListener('hashchange', reopen);
  }, []);
  useEffect(() => {
    // Purpose credentials stay out of requests' URLs, browser history and copied error diagnostics.
    window.history.replaceState(window.history.state, '', window.location.pathname);
    const controller = new AbortController();
    void identityRequest<Invitation>('/api/v2/auth/invitation', { method: 'POST', body: proof, signal: controller.signal })
      .then(value => { if (!controller.signal.aborted) setInvitation(value); }).catch(reason => { if (!controller.signal.aborted) setLoadError(reason); });
    return () => controller.abort();
  }, [proof]);
  const submit = async (values: { displayName?: string; password: string }) => {
    if (!invitation) return;
    setBusy(true); setError(undefined);
    try {
      if (mode === 'Create account') await identityRequest('/api/v2/auth/register', { method: 'POST', body: { login: invitation.login, displayName: values.displayName, password: values.password, invitation: proof } });
      await identityRequest('/api/v2/auth/login', { method: 'POST', body: { login: invitation.login, password: values.password } }); resetIdentityCsrf();
      if (mode !== 'Create account') await identityRequest(`/api/v2/account/tenant-invitations/${invitationId}/accept`, { method: 'POST', body: proof });
      window.location.replace(tenantAccessPath(invitation.tenantId));
    } catch (reason) { setError(reason); setBusy(false); }
  };
  return <AccountFormFrame title="Join organization" description={invitation ? `You have been invited to ${invitation.organizationName}.` : 'An invitation connects your account to one organization.'}>{holder}
    {loadError !== undefined ? <><AccessError error={loadError} /><Typography.Paragraph type="secondary">Open the original invitation link again, or ask the owner for a new link.</Typography.Paragraph><Button href="/sign-in">Back to sign in</Button></> : !invitation ? <AccessLoading /> : <Flex vertical gap="var(--ac-space-section)">
      <Typography.Text>{invitation.login.toLowerCase()}</Typography.Text><Segmented block options={['Create account', 'I have an account']} value={mode} onChange={value => { setMode(value); setError(undefined); form.resetFields(); }} disabled={busy} />
      <Form form={form} layout="vertical" disabled={busy} onFinish={values => void submit(values)} onFinishFailed={reason => showAdminFormValidation(feedback, form, reason)}>
        {error !== undefined && <div className="access-form-error"><Alert type="error" showIcon role="alert" title={accessErrorMessage(error)} /></div>}
        {mode === 'Create account' && <Form.Item name="displayName" label="Your name" rules={[{ required: true, whitespace: true, message: 'Enter your name.' }, { max: 160 }]}><Input autoComplete="name" autoFocus /></Form.Item>}
        <Form.Item name="password" label="Password" extra={mode === 'Create account' ? 'Use 12–256 characters.' : undefined} rules={[{ required: true, message: 'Enter your password.' }, ...(mode === 'Create account' ? [{ min: 12, max: 256, message: 'Use 12–256 characters.' }] : [])]}><Input.Password autoComplete={mode === 'Create account' ? 'new-password' : 'current-password'} maxLength={256} /></Form.Item>
        <Button type="primary" htmlType="submit" block loading={busy}>{mode === 'Create account' ? 'Create account & join' : 'Sign in & join'}</Button>
      </Form>
      <Typography.Text type="secondary">Your other organization memberships will not change.</Typography.Text>
    </Flex>}
  </AccountFormFrame>;
}
