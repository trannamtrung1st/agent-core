import { useEffect, useState } from 'react';
import { Button, Flex, Tooltip } from 'antd';
import { LogoutOutlined, SettingOutlined, UserOutlined } from '@ant-design/icons';
import { ChatApp } from './ChatApp';
import { TenantApprovalNotification } from './TenantConversationApprovals';
import { chatTenantId } from '../../services/tenantChat';
import { identityRequest, resetIdentityCsrf, tenantApi, type TenantContext } from '../../services/identity';
import { retirePrivateChat } from '../../services/realtime';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { TenantSwitcher } from '../access/Organizations';
import { tenantAccessPath } from '../access/accessRoute';
import { approveAppNavigation } from '../../app/appRoute';

export function TenantChatEntry({ tenantId }: { tenantId: string }) {
  const tenant = useIdentityQuery<TenantContext>(chatTenantId === tenantId ? tenantApi(tenantId, '/access/me') : null);
  const [signingOut, setSigningOut] = useState(false); const [error, setError] = useState<unknown>();
  useEffect(() => {
    const expired = () => { retirePrivateChat(); resetIdentityCsrf(); window.location.replace(`/sign-in?notice=expired&returnTo=${encodeURIComponent(window.location.pathname)}`); };
    window.addEventListener('agentcore:authentication-expired', expired);
    const restore = (event: PageTransitionEvent) => { if (event.persisted) { retirePrivateChat(); window.location.reload(); } };
    window.addEventListener('pagehide', retirePrivateChat); window.addEventListener('pageshow', restore);
    return () => { window.removeEventListener('agentcore:authentication-expired', expired); window.removeEventListener('pagehide', retirePrivateChat); window.removeEventListener('pageshow', restore); };
  }, []);
  if (chatTenantId !== tenantId) return <main className="access-loading-page"><Button href={`/t/${tenantId}`}>Open organization Chat</Button></main>;
  if (tenant.loading) return <main className="access-loading-page"><AccessLoading /></main>;
  if (tenant.error || !tenant.data) return <main className="access-loading-page"><AccessError error={tenant.error} retry={tenant.reload} /><Button href="/account/organizations">My organizations</Button><Button href={`/sign-in?returnTo=${encodeURIComponent(window.location.pathname)}`}>Sign in</Button></main>;
  const signOut = async () => {
    if (!await approveAppNavigation('/sign-in')) return; setSigningOut(true); setError(undefined);
    try { await identityRequest('/api/v2/auth/logout', { method: 'POST' }); retirePrivateChat(); resetIdentityCsrf(); window.location.replace('/sign-in'); }
    catch (reason) { setError(reason); setSigningOut(false); }
  };
  const context = tenant.data;
  return <ChatApp tenantContext={context} tenantError={error} tenantActions={<Flex align="center" wrap gap="var(--ac-space-compact)">
    <TenantSwitcher tenantId={tenantId} name={context.tenant.name} chat />
    <TenantApprovalNotification tenant={tenantId} />
    <Tooltip title="Organization"><Button type="text" className="chat-header-icon-action" href={tenantAccessPath(tenantId)} aria-label="Organization administration" icon={<SettingOutlined />} /></Tooltip>
    <Tooltip title="Your account"><Button type="text" className="chat-header-icon-action" href="/account/profile" aria-label="Your account" icon={<UserOutlined />} /></Tooltip>
    <Tooltip title="Sign out"><Button type="text" className="chat-header-icon-action" aria-label="Sign out" loading={signingOut} onClick={() => void signOut()} icon={<LogoutOutlined />} /></Tooltip>
  </Flex>} />;
}
