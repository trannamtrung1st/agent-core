import { useEffect, useState } from 'react';
import { Button, Dropdown, Flex } from 'antd';
import { MenuOutlined } from '@ant-design/icons';
import { ChatApp } from './ChatApp';
import { TenantApprovalNotification } from './TenantConversationApprovals';
import { chatTenantId } from '../../services/tenantChat';
import { identityRequest, resetIdentityCsrf, tenantApi, type TenantContext } from '../../services/identity';
import { retirePrivateChat } from '../../services/realtime';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { TenantSwitcher } from '../access/Organizations';
import { tenantAccessPath } from '../access/accessRoute';
import { approveAppNavigation, navigateToAppPath } from '../../app/appRoute';

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
    <Dropdown trigger={['click']} menu={{ items: [
      { key: tenantAccessPath(tenantId, 'agents'), label: 'Agents' },
      { key: tenantAccessPath(tenantId, 'spaces'), label: 'Spaces' },
      { key: tenantAccessPath(tenantId, 'workflows'), label: 'Workflows' },
      { key: tenantAccessPath(tenantId), label: 'Organization administration' },
      { type: 'divider' },
      { key: '/account/organizations', label: 'My organizations' },
      { key: '/account/profile', label: 'Your account' },
      { key: 'sign-out', label: 'Sign out', disabled: signingOut }
    ], onClick: ({ key }) => key === 'sign-out' ? void signOut() : navigateToAppPath(key) }}>
      <Button icon={<MenuOutlined aria-hidden />} aria-label="Workspace menu" loading={signingOut}>Menu</Button>
    </Dropdown>
  </Flex>} />;
}
