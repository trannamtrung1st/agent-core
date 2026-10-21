import { TenantEvents } from './TenantEvents';
import { TenantAgentAutomations } from "./TenantAgentAutomations";
import { TenantAgentActivity } from "./TenantAgentActivity";
import { TenantAgentWorkspace } from './TenantAgentWorkspace';
import { TenantAgentSandbox } from './TenantAgentSandbox';
import { TenantSandboxes } from './TenantSandboxes';
import { TenantAgentResources } from './TenantAgentResources';
import { TenantAgentSkills } from './TenantAgentSkills';
import { TenantAgentVersion } from './TenantAgentVersion';
import { appMessageConfig } from '../../app/appFeedback';
import { TenantDefinitions } from './TenantDefinitions';
import { TenantAgents } from './TenantAgents';
import { TenantAgentAccess } from './TenantAgentAccess';
import { TenantAgentControls } from './TenantAgentControls';
import { TenantAgentConfiguration } from './TenantAgentConfiguration';
import { BrowserPrivacySection } from '../admin/BrowserPrivacySection';
import { AgentConfigurationPanel } from '../admin/AgentConfigurationLayout';
import { TenantAgentLifecycle } from './TenantAgentLifecycle';
import { TenantAgentConnections } from './TenantAgentConnections';
import { TenantCredentials } from './TenantCredentials';
import { TenantApprovals } from './TenantApprovals';
import { TenantGovernance } from './TenantGovernance';
import { SystemAudit, TenantAudit } from './TenantAudit';
import { SystemAdministration } from './SystemAdministration';
import { TenantDefinitionDetail } from './TenantDefinitionDetail';
import { TenantDefinitionEditor } from './TenantDefinitionEditor';
import { useEffect, useState } from 'react';
import { App as AntdApp, Alert, Button, Collapse, Empty, Flex, Tag, Typography } from 'antd';
import { LogoutOutlined, UserOutlined, MessageOutlined } from '@ant-design/icons';
import { approveAppNavigation, navigateToAppPath } from '../../app/appRoute';
import { AdminShell, type AdminShellContext } from '../admin/AdminShell';
import { draftPages, type NavigationPage } from '../admin/adminNavigation';
import { identityRequest, resetIdentityCsrf, tenantApi, type AccountContext, type TenantContext } from '../../services/identity';
import { AccessError, AccessLoading, AccessUnavailable } from './AccessFeedback';
import { AccountProfile, SignIn } from './AccountForms';
import { OrganizationsList, CreateOrganization, TenantSwitcher } from './Organizations';
import { Members } from './Members';
import { Roles, permissionFamily, permissionLabel } from './Roles';
import { InvitationAcceptance } from './InvitationAcceptance';
import { tenantAccessPath, tenantAgentPath, tenantDraftPath, type AccessPage, type AccessRoute, type TenantInstancePage } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';

const page = (id: string, label: string, description: string, group = 'Account'): NavigationPage => ({ id, path: id, label, description, group, aliases: [] });
const accountPages = [page('organizations', 'My organizations', 'Choose an organization. Membership and access are separate in each one.'), page('profile', 'Your account', 'Your global account connects your organization memberships.')];
const tenantPages = [page('agents', 'Instances', 'Inspect agent instances, chat access and independent execution authority.', 'Agents'), page('definitions', 'Definitions', 'Create and publish Definitions owned by this organization.', 'Agents'), page('credentials', 'Credentials', 'Manage protected accounts and values owned by this organization.', 'Integrations'), page('events', 'Events', 'Manage authenticated webhook connections and inspect the read-only built-in catalog.', 'Integrations'), page('sandboxes', 'Sandbox profiles', 'Inspect and configure prebuilt environments owned by this organization.', 'Integrations'), page('access', 'Your access', 'Inspect your current organization roles and permissions.', 'Access & Security'), page('members', 'Members', 'Invite people and manage their access to this organization.', 'Access & Security'), page('roles', 'Roles & permissions', 'Define authority separately from agent instructions and capabilities.', 'Access & Security'), page('approvals', 'Approvals', 'Review exact saved agent actions and their recorded outcomes.', 'Access & Security'), page('governance', 'Governance', 'Inspect Core minimums and set organization review policy.', 'Access & Security'), page('audit', 'Audit', 'Inspect authorization decisions and recorded organization changes.', 'Access & Security')];
const instancePages = [page('instance-automations', 'Automations', 'Manage saved tasks triggered by schedules, received events, or explicit actions.', 'Activity'), page('instance-activity-sessions', 'Sessions', 'Inspect conversations and background work for this agent.', 'Activity'), page('instance-activity-runs', 'Runs', 'Inspect individual invocations and their saved outcomes.', 'Activity'), page('instance-profile', 'Profile', 'Edit this agent’s persona independently of authority.', 'Agent instance'), page('instance-version', 'Definition version', 'Review and adopt an exact Definition version for future Runs.', 'Agent instance'), page('instance-skills', 'Skills', 'Inspect inherited procedures and manage deliberate Instance choices.', 'Agent instance'), page('instance-resources', 'Resources', 'Inspect inherited files and stage deliberate local resource changes.', 'Agent instance'), page('instance-workspace', 'Workspace', 'Inspect and manage current home files under separate read, write and delete access.', 'Agent instance'), page('instance-sandbox', 'Sandbox', 'Configure inherited environments and verify exact saved setup.', 'Agent instance'), page('instance-settings', 'Settings', 'Inspect inherited settings and save deliberate Instance overrides.', 'Agent instance'), page('instance-execution-budgets', 'Execution budgets', 'Set inherited or bounded per-class execution limits.', 'Agent instance'), page('instance-self-management', 'Self-management', 'Configure management eligibility and the saved lock separately from IAM.', 'Agent instance'), page('instance-unattended-model', 'Unattended model', 'Choose the preference for future unattended Runs.', 'Agent instance'), page('instance-lifecycle', 'Lifecycle', 'Archive, restore or permanently retire this exact agent identity.', 'Agent instance'), page('instance-access', 'Access', 'Inspect chat audience and independent agent authority.', 'Agent instance'), page('instance-connections', 'Connections', 'Manage credential bindings and persistent Browser state under independent access.', 'Agent instance')];
const systemPrivacyPage = page('browser-privacy', 'Browser privacy', 'Manage platform-wide screenshot privacy within deployment limits.', 'Platform');
const systemAuditPage = page('system-audit', 'Platform audit', 'Inspect body-free platform security and configuration records.', 'Platform');
const systemPages = [
  { ...page('system-operators', 'Platform operators', 'Review and manage exact built-in platform roles on Accounts.', 'Platform'), pathPage: 'operators', permission: 'system.governance.manage' },
  { ...page('system-organizations', 'Organizations', 'Inspect administrative organization metadata and current platform status.', 'Platform'), pathPage: 'organizations', permission: 'system.tenants.read' },
  { ...page('system-accounts', 'Accounts', 'Inspect Account identity metadata and current sign-in status.', 'Platform'), pathPage: 'accounts', permission: 'system.accounts.manage' },
  { ...systemPrivacyPage, pathPage: 'browser-privacy', permission: 'system.governance.manage' },
  { ...systemAuditPage, pathPage: 'audit', permission: 'system.governance.manage' }
];
const pagePermission: Record<string, string> = { members: 'members.read', roles: 'iam.roles.read', audit: 'security.audit.read', governance: 'tenant.governance.read' };

export function AccessApp({ route }: { route: AccessRoute }) { return <AntdApp component={false} message={appMessageConfig}><AccessContent route={route} /></AntdApp>; }
function AccessContent({ route }: { route: AccessRoute }) {
  const needsAccount = route.view !== 'sign-in' && route.view !== 'invitation';
  const account = useIdentityQuery<AccountContext>(needsAccount ? '/api/v2/account/me' : null, route.view === 'system' ? 'system' : '');
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
  const active = route.view === 'tenant' ? route.instanceId ? instancePages.find(item => item.id === `instance-${route.instancePage ?? 'access'}`)! : route.draftId ? draftPages.find(item => item.id === route.draftPage)! : tenantPages.find(item => item.id === route.page)! : route.view === 'system' ? systemPages.find(item => item.pathPage === route.page)! : route.view === 'account' ? accountPages.find(item => item.id === route.page)! : page('not-found', 'Page not found', 'Choose a destination from the navigation.');
  const navigation = [...(route.view === 'tenant' && route.instanceId ? instancePages.map(item => ({ ...item, href: tenantAgentPath(route.tenantId, route.instanceId!, item.id.slice('instance-'.length) as TenantInstancePage) })) : []), ...(route.view === 'tenant' && route.draftId && route.definitionId ? draftPages.map(item => ({ ...item, href: tenantDraftPath(route.tenantId, route.definitionId!, route.draftId!, item.id) })) : []), ...(route.view === 'tenant' ? visibleTenantPages.map(item => ({ ...item, href: tenantAccessPath(route.tenantId, item.id as AccessPage) })) : []), ...systemPages.filter(item => account.data!.systemPermissions.includes(item.permission)).map(item => ({ ...item, href: `/system/${item.pathPage}` })), ...accountPages.map(item => ({ ...item, href: `/account/${item.id}` }))];
  const context: AdminShellContext = { key: window.location.pathname, name: route.view === 'tenant' ? tenantContext?.tenant.name ?? 'Organization' : route.view === 'system' ? 'Platform' : account.data.account.displayName, areaLabel: route.view === 'tenant' ? 'Organization' : route.view === 'system' ? 'System' : 'Account', page: active, navigation,
    scopeAction: tenantContext ? <TenantSwitcher tenantId={tenantContext.tenant.tenantId} name={tenantContext.tenant.name} /> : undefined,
    headerActions: <>{tenantContext && <Button icon={<MessageOutlined aria-hidden />} href={`/t/${tenantContext.tenant.tenantId}`} aria-label="Chat">Chat</Button>}<Button icon={<UserOutlined aria-hidden />} aria-label="Your account" title="Your account" onClick={() => navigateToAppPath('/account/profile')} /><Button icon={<LogoutOutlined aria-hidden />} aria-label="Sign out" title="Sign out" loading={signingOut} onClick={() => void signOut()} /></> };
  const allowed = route.view !== 'tenant' || !pagePermission[route.page] || tenantContext?.permissions.includes(pagePermission[route.page]);
  return <AdminShell context={context} headingActions={route.view === 'account' && route.page === 'organizations' && account.data.systemPermissions.includes('system.tenants.create') ? <CreateOrganization onCreated={() => setOrganizationsVersion(value => value + 1)} /> : undefined}>
    {logoutError !== undefined && <AccessError error={logoutError} />}
    {route.view === 'system' ? account.data.systemPermissions.includes(systemPages.find(item => item.pathPage === route.page)!.permission) ? route.page === 'organizations' || route.page === 'accounts' || route.page === 'operators' ? <SystemAdministration key={route.page} kind={route.page === 'organizations' ? 'tenants' : route.page} permissions={account.data.systemPermissions} /> : route.page === 'audit' ? <SystemAudit /> : <AgentConfigurationPanel title="Host Browser privacy" label="Platform Browser privacy" bodyGap="section"><BrowserPrivacySection /></AgentConfigurationPanel> : <AccessUnavailable /> : route.view === 'tenant' ? tenant.loading ? <AccessLoading /> : tenant.error ? <AccessError error={tenant.error} retry={tenant.reload} /> : !tenantContext || !allowed ? <AccessUnavailable /> : route.page === 'agents' ? route.instanceId ? route.instancePage === 'automations' ? <TenantAgentAutomations key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'activity-sessions' || route.instancePage === 'activity-runs' ? <TenantAgentActivity key={`${route.tenantId}:${route.instanceId}:${route.instancePage}`} context={tenantContext} id={route.instanceId} page={route.instancePage} /> : route.instancePage === 'workspace' ? <TenantAgentWorkspace key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'sandbox' ? <TenantAgentSandbox key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'resources' ? <TenantAgentResources key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'skills' ? <TenantAgentSkills key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'version' ? <TenantAgentVersion key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'execution-budgets' || route.instancePage === 'self-management' || route.instancePage === 'unattended-model' ? <TenantAgentControls key={`${route.tenantId}:${route.instanceId}:${route.instancePage}`} context={tenantContext} id={route.instanceId} page={route.instancePage} /> : route.instancePage === 'profile' || route.instancePage === 'settings' ? <TenantAgentConfiguration key={`${route.tenantId}:${route.instanceId}:${route.instancePage}`} context={tenantContext} id={route.instanceId} page={route.instancePage} /> : route.instancePage === 'lifecycle' ? <TenantAgentLifecycle key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'connections' ? <TenantAgentConnections key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : <TenantAgentAccess key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : <TenantAgents key={route.tenantId} context={tenantContext} /> : route.page === 'approvals' ? <TenantApprovals key={route.tenantId} context={tenantContext} requestId={route.requestId} /> : route.page === 'events' ? <TenantEvents key={route.tenantId} context={tenantContext} /> : route.page === 'sandboxes' ? <TenantSandboxes key={route.tenantId} context={tenantContext} /> : route.page === 'credentials' ? <TenantCredentials key={route.tenantId} context={tenantContext} /> : route.page === 'definitions' ? route.draftId && route.definitionId ? <TenantDefinitionEditor key={`${route.tenantId}:${route.draftId}`} context={tenantContext} id={route.definitionId} draftId={route.draftId} page={route.draftPage ?? 'overview'} /> : route.definitionId ? <TenantDefinitionDetail key={`${route.tenantId}:${route.definitionId}`} context={tenantContext} id={route.definitionId} /> : <TenantDefinitions key={route.tenantId} context={tenantContext} /> : route.page === 'members' ? <Members context={tenantContext} /> : route.page === 'roles' ? <Roles context={tenantContext} /> : route.page === 'governance' ? <TenantGovernance key={route.tenantId} context={tenantContext} /> : route.page === 'audit' ? <TenantAudit key={route.tenantId} context={tenantContext} /> : <OwnAccess context={tenantContext} /> : route.view === 'account' ? route.page === 'profile' ? <AccountProfile context={account.data} /> : <OrganizationsList key={organizationsVersion} /> : <AccessUnavailable />}
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
