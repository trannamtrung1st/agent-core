import { useEffect, useState } from 'react';
import { Alert, Button, Collapse, Empty, Flex, Table, Tag, Typography } from 'antd';
import { LogoutOutlined, UserOutlined } from '@ant-design/icons';
import { approveAppNavigation, navigateToAppPath } from '../../app/appRoute';
import { AdminShell, type AdminShellContext } from '../admin/AdminShell';
import type { NavigationPage } from '../admin/adminNavigation';
import { identityRequest, resetIdentityCsrf, tenantApi, type AccountContext, type TenantContext } from '../../services/identity';
import { AccessError, AccessLoading, AccessUnavailable } from './AccessFeedback';
import { AccountProfile, SignIn } from './AccountForms';
import { OrganizationsList, CreateOrganization, TenantSwitcher } from './Organizations';
import { Members } from './Members';
import { Roles, permissionFamily, permissionLabel } from './Roles';
import { InvitationAcceptance } from './InvitationAcceptance';
import { tenantAccessPath, type AccessRoute } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';

const page = (id: string, label: string, description: string, group = 'Account'): NavigationPage => ({ id, path: id, label, description, group, aliases: [] });
const accountPages = [page('organizations', 'My organizations', 'Choose an organization. Membership and access are separate in each one.'), page('profile', 'Your account', 'Your global account connects your organization memberships.')];
const tenantPages = [page('access', 'Your access', 'Inspect your current organization roles and permissions.', 'Access & Security'), page('members', 'Members', 'Invite people and manage their access to this organization.', 'Access & Security'), page('roles', 'Roles & permissions', 'Define authority separately from agent instructions and capabilities.', 'Access & Security'), page('audit', 'Audit', 'Inspect recorded changes to organization access.', 'Access & Security')];
const pagePermission: Record<string, string> = { members: 'members.read', roles: 'iam.roles.read', audit: 'security.audit.read' };

export function AccessApp({ route }: { route: AccessRoute }) {
  const needsAccount = route.view !== 'sign-in' && route.view !== 'invitation';
  const account = useIdentityQuery<AccountContext>(needsAccount ? '/api/v2/account/me' : null);
  const tenant = useIdentityQuery<TenantContext>(route.view === 'tenant' ? tenantApi(route.tenantId, '/access/me') : null, route.view === 'tenant' ? route.page : '');
  const [logoutError, setLogoutError] = useState<unknown>(); const [signingOut, setSigningOut] = useState(false); const [organizationsVersion, setOrganizationsVersion] = useState(0);
  useEffect(() => {
    const expired = () => { resetIdentityCsrf(); navigateToAppPath(`/sign-in?notice=expired&returnTo=${encodeURIComponent(window.location.pathname)}`, true); };
    window.addEventListener('agentcore:authentication-expired', expired);
    return () => window.removeEventListener('agentcore:authentication-expired', expired);
  }, []);
  if (route.view === 'sign-in') return <SignIn />;
  if (route.view === 'invitation') return <InvitationAcceptance invitationId={route.invitationId} />;
  if (account.loading) return <main className="access-loading-page"><AccessLoading /></main>;
  if (account.error) return <main className="access-loading-page"><AccessError error={account.error} retry={account.reload} /><Button href="/sign-in">Sign in</Button></main>;
  if (!account.data) return <AccessUnavailable />;
  const signOut = async () => { if (!await approveAppNavigation('/sign-in')) return; setSigningOut(true); setLogoutError(undefined); try { await identityRequest('/api/v2/auth/logout', { method: 'POST' }); resetIdentityCsrf(); window.location.replace('/sign-in'); } catch (error) { setLogoutError(error); setSigningOut(false); } };
  const tenantContext = route.view === 'tenant' ? tenant.data : undefined;
  const visibleTenantPages = tenantPages.filter(item => !pagePermission[item.id] || tenantContext?.permissions.includes(pagePermission[item.id]));
  const active = route.view === 'tenant' ? tenantPages.find(item => item.id === route.page)! : route.view === 'account' ? accountPages.find(item => item.id === route.page)! : page('not-found', 'Page not found', 'Choose a destination from the navigation.');
  const navigation = [...(route.view === 'tenant' ? visibleTenantPages.map(item => ({ ...item, href: tenantAccessPath(route.tenantId, item.id as 'access' | 'members' | 'roles' | 'audit') })) : []), ...accountPages.map(item => ({ ...item, href: `/account/${item.id}` }))];
  const context: AdminShellContext = { key: window.location.pathname, name: route.view === 'tenant' ? tenantContext?.tenant.name ?? 'Organization' : account.data.account.displayName, areaLabel: route.view === 'tenant' ? 'Access' : 'Account', page: active, navigation,
    scopeAction: tenantContext ? <TenantSwitcher tenantId={tenantContext.tenant.tenantId} name={tenantContext.tenant.name} /> : undefined,
    headerActions: <><Button icon={<UserOutlined aria-hidden />} aria-label="Your account" title="Your account" onClick={() => navigateToAppPath('/account/profile')} /><Button icon={<LogoutOutlined aria-hidden />} aria-label="Sign out" title="Sign out" loading={signingOut} onClick={() => void signOut()} /></> };
  const allowed = route.view !== 'tenant' || !pagePermission[route.page] || tenantContext?.permissions.includes(pagePermission[route.page]);
  return <AdminShell route={{ area: 'admin', view: 'home' }} context={context} headingActions={route.view === 'account' && route.page === 'organizations' && account.data.systemPermissions.includes('system.tenants.create') ? <CreateOrganization onCreated={() => setOrganizationsVersion(value => value + 1)} /> : undefined}>
    {logoutError !== undefined && <AccessError error={logoutError} />}
    {route.view === 'tenant' ? tenant.loading ? <AccessLoading /> : tenant.error ? <AccessError error={tenant.error} retry={tenant.reload} /> : !tenantContext || !allowed ? <AccessUnavailable /> : route.page === 'members' ? <Members context={tenantContext} /> : route.page === 'roles' ? <Roles context={tenantContext} /> : route.page === 'audit' ? <Audit context={tenantContext} /> : <OwnAccess context={tenantContext} /> : route.view === 'account' ? route.page === 'profile' ? <AccountProfile context={account.data} /> : <OrganizationsList key={organizationsVersion} /> : <AccessUnavailable />}
  </AdminShell>;
}
function OwnAccess({ context }: { context: TenantContext }) {
  const groups = [...new Set(context.permissions.map(permissionFamily))];
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form"><Flex wrap gap="var(--ac-space-compact)">{context.roles.length ? context.roles.map(role => <Tag key={role.roleId}>{role.name}</Tag>) : <Typography.Text type="secondary">No organization-wide role is assigned.</Typography.Text>}</Flex>
    <Alert type="info" showIcon title="People and agents have separate authority." description="Chat access lets you reach an agent. Its instructions and judgment guide its response; its own roles control what it can do." />
    <Typography.Title level={4}>Your organization permissions</Typography.Title><Typography.Paragraph type="secondary">These permissions apply across {context.tenant.name}. Additional access may be assigned on individual resources.</Typography.Paragraph>
    {groups.length ? <Collapse items={groups.map(group => ({ key: group, label: group, children: <Flex vertical gap="var(--ac-space-compact)">{context.permissions.filter(key => permissionFamily(key) === group).map(key => <Typography.Text key={key}>{permissionLabel(key)}</Typography.Text>)}</Flex> }))} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No organization-wide permissions. Contact an owner if you need more access." />}
  </Flex>;
}
function Audit({ context }: { context: TenantContext }) {
  const state = useIdentityQuery<{ items: { auditId: string; action: string; at: string; decision: string }[] }>(tenantApi(context.tenant.tenantId, '/audit?limit=100'));
  if (state.loading) return <AccessLoading />;
  if (state.error) return <AccessError error={state.error} retry={state.reload} />;
  const names: Record<string, string> = { 'iam.roles.assign': 'Role assigned', 'iam.roles.revoke': 'Role revoked', 'iam.roles.manage': 'Role saved', 'members.invite': 'Member invited', 'members.accept_invitation': 'Invitation accepted', 'members.status': 'Membership status changed', 'members.revoke_invitation': 'Invitation revoked' };
  return <><Typography.Paragraph type="secondary">The latest 100 access changes in this organization.</Typography.Paragraph><Table className="admin-collection-table" rowKey="auditId" dataSource={state.data?.items} pagination={{ pageSize: 10, responsive: true, showSizeChanger: false }} columns={[
    { title: 'Change', render: (_, item) => <Flex vertical><Typography.Text>{names[item.action] ?? 'Organization access updated'}</Typography.Text><Typography.Text type="secondary"><time dateTime={item.at}>{new Date(item.at).toLocaleString()}</time></Typography.Text></Flex> },
    { title: 'Result', width: 100, render: () => <Tag>Recorded</Tag> }
  ]} /></>;
}
