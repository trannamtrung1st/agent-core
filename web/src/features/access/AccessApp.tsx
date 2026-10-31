import { TenantEvaluations } from './TenantEvaluations';
import { TenantModels } from './TenantModels';
import { SystemModelPresets } from './SystemModelPresets';
import { TenantAgentMemory } from './TenantAgentMemory';
import { TenantUserProfile } from './TenantUserProfile';
import { TenantAgentState } from './TenantAgentState';
import { TenantAgentIsolation } from './TenantAgentIsolation';
import { TenantEvents } from './TenantEvents';
import { TenantAgentAutomations } from "./TenantAgentAutomations";
import { TenantAgentActivity } from "./TenantAgentActivity";
import { TenantSpaces } from './TenantSpaces';
import { TenantSecrets } from './TenantSecrets';
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
import { lazy, Suspense, useEffect, useState } from 'react';
import { App as AntdApp, Alert, Button, Collapse, Empty, Flex, Tag, Typography } from 'antd';
import { LogoutOutlined, MessageOutlined } from '@ant-design/icons';
import { approveAppNavigation, lastChatUrl, navigateToAppPath } from '../../app/appRoute';
import { followAppLink } from '../../app/appLink';
import { chatTenantId } from '../../services/tenantChat';
import { AdminShell, type AdminShellContext } from '../admin/AdminShell';
import { draftPages } from '../admin/adminNavigation';
import { identityRequest, resetIdentityCsrf, tenantApi, type AccountContext, type TenantContext } from '../../services/identity';
import { AccessError, AccessLoading, AccessUnavailable } from './AccessFeedback';
import { AccountProfile, SignIn } from './AccountForms';
import { OrganizationsList, CreateOrganization, TenantSwitcher } from './Organizations';
import { Members } from './Members';
import { Roles, permissionFamily, permissionLabel } from './Roles';
import { InvitationAcceptance } from './InvitationAcceptance';
import { tenantAccessPath, tenantAgentPath, tenantDraftPath, type AccessPage, type AccessRoute, type TenantInstancePage } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';

import { page, accountPages, tenantPages, instancePages, systemPages, pagePermission } from './experienceNavigation';

const TenantWorkflows = lazy(() => import('../workflows/TenantWorkflows').then(module => ({ default: module.TenantWorkflows })));


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
  const navigation = [...(route.view === 'tenant' && route.instanceId ? instancePages.map(item => ({ ...item, href: tenantAgentPath(route.tenantId, route.instanceId!, item.id.slice('instance-'.length) as TenantInstancePage) })) : []), ...(route.view === 'tenant' && route.draftId && route.definitionId ? draftPages.map(item => ({ ...item, href: tenantDraftPath(route.tenantId, route.definitionId!, route.draftId!, item.id) })) : []), ...(route.view === 'tenant' && !route.instanceId && !route.draftId ? visibleTenantPages.map(item => ({ ...item, href: tenantAccessPath(route.tenantId, item.id as AccessPage) })) : []), ...(route.view === 'system' ? systemPages.filter(item => account.data!.systemPermissions.includes(item.permission)).map(item => ({ ...item, href: `/system/${item.pathPage}` })) : []), ...(route.view === 'account' || route.view === 'not-found' ? accountPages.map(item => ({ ...item, href: `/account/${item.id}` })) : [])];
  const scopeNavigation = [...accountPages.map(item => ({ ...item, href: `/account/${item.id}` })), ...systemPages.filter(item => account.data!.systemPermissions.includes(item.permission)).map(item => ({ ...item, href: `/system/${item.pathPage}` }))];
  const organizationNavigation = route.view === 'tenant' ? [{ id: 'chat', label: 'Chat', href: lastChatUrl(), group: 'Work' }, ...visibleTenantPages.map(item => ({ ...item, href: tenantAccessPath(route.tenantId, item.id as AccessPage) }))] : [];
  if (route.view === 'tenant' && !route.instanceId && !route.draftId) navigation.unshift({ id: 'chat', path: '', label: 'Chat', description: 'Talk to your agents.', aliases: [], group: 'Work', href: lastChatUrl() });
  const context: AdminShellContext = { scopeNavigation, organizationNavigation: route.view === 'tenant' && (route.instanceId || route.draftId) ? organizationNavigation : undefined, parentLink: route.view === 'tenant' && route.instanceId ? { label: 'Back to Agents', href: tenantAccessPath(route.tenantId, 'agents') } : route.view === 'tenant' && route.draftId ? { label: 'Back to Definitions', href: tenantAccessPath(route.tenantId, 'definitions') } : undefined, key: window.location.pathname, name: route.view === 'tenant' ? tenantContext?.tenant.name ?? 'Organization' : route.view === 'system' ? 'Platform' : account.data.account.displayName, areaLabel: route.view === 'tenant' ? 'Organization' : route.view === 'system' ? 'System' : 'Account', page: active, navigation,
    scopeAction: tenantContext ? <TenantSwitcher tenantId={tenantContext.tenant.tenantId} name={tenantContext.tenant.name} /> : undefined,
    headerActions: <>{tenantContext && <Button className="admin-header-chat" icon={<MessageOutlined aria-hidden />} href={lastChatUrl()} onClick={chatTenantId === tenantContext.tenant.tenantId ? followAppLink : undefined} aria-label="Chat"><span className="admin-chat-label">Chat</span></Button>}<Button icon={<LogoutOutlined aria-hidden />} aria-label="Sign out" title="Sign out" loading={signingOut} onClick={() => void signOut()} /></> };
  const allowed = route.view !== 'tenant' || !pagePermission[route.page] || tenantContext?.permissions.includes(pagePermission[route.page]);
  return <AdminShell context={context} headingActions={route.view === 'account' && route.page === 'organizations' && account.data.systemPermissions.includes('system.tenants.create') ? <CreateOrganization onCreated={() => setOrganizationsVersion(value => value + 1)} /> : undefined}>
    {logoutError !== undefined && <AccessError error={logoutError} />}
    {route.view === 'system' ? account.data.systemPermissions.includes(systemPages.find(item => item.pathPage === route.page)!.permission) ? route.page === 'model-presets' ? <SystemModelPresets permissions={account.data.systemPermissions} /> : route.page === 'organizations' || route.page === 'accounts' || route.page === 'operators' ? <SystemAdministration key={route.page} kind={route.page === 'organizations' ? 'tenants' : route.page} permissions={account.data.systemPermissions} /> : route.page === 'audit' ? <SystemAudit /> : <AgentConfigurationPanel title="Host Browser privacy" label="Platform Browser privacy" bodyGap="section"><BrowserPrivacySection /></AgentConfigurationPanel> : <AccessUnavailable /> : route.view === 'tenant' ? tenant.loading ? <AccessLoading /> : tenant.error ? <AccessError error={tenant.error} retry={tenant.reload} /> : !tenantContext || !allowed ? <AccessUnavailable /> : route.page === 'agents' ? route.instanceId ? route.instancePage === 'memory' ? <TenantAgentMemory key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'automations' ? <TenantAgentAutomations key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'activity-sessions' || route.instancePage === 'activity-runs' ? <TenantAgentActivity key={`${route.tenantId}:${route.instanceId}:${route.instancePage}`} context={tenantContext} id={route.instanceId} page={route.instancePage} /> : route.instancePage === 'state' ? <TenantAgentState key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'state-isolation' ? <TenantAgentIsolation key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'sandbox' ? <TenantAgentSandbox key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'resources' ? <TenantAgentResources key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'skills' ? <TenantAgentSkills key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'version' ? <TenantAgentVersion key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'execution-budgets' || route.instancePage === 'self-management' || route.instancePage === 'unattended-model' ? <TenantAgentControls key={`${route.tenantId}:${route.instanceId}:${route.instancePage}`} context={tenantContext} id={route.instanceId} page={route.instancePage} /> : route.instancePage === 'profile' || route.instancePage === 'settings' ? <TenantAgentConfiguration key={`${route.tenantId}:${route.instanceId}:${route.instancePage}`} context={tenantContext} id={route.instanceId} page={route.instancePage} /> : route.instancePage === 'lifecycle' ? <TenantAgentLifecycle key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : route.instancePage === 'connections' ? <TenantAgentConnections key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : <TenantAgentAccess key={`${route.tenantId}:${route.instanceId}`} context={tenantContext} id={route.instanceId} /> : <TenantAgents key={route.tenantId} context={tenantContext} /> : route.page === 'my-profile' ? <TenantUserProfile key={`${route.tenantId}:${tenantContext.principalId}`} context={tenantContext} /> : route.page === 'approvals' ? <TenantApprovals key={route.tenantId} context={tenantContext} requestId={route.requestId} /> : route.page === 'events' ? <TenantEvents key={route.tenantId} context={tenantContext} /> : route.page === 'sandboxes' ? <TenantSandboxes key={route.tenantId} context={tenantContext} /> : route.page === 'spaces' ? <TenantSpaces key={route.tenantId} context={tenantContext} id={route.spaceId} page={route.spacePage} /> : route.page === 'ai-models' ? <TenantModels key={route.tenantId} context={tenantContext} page={route.modelPage ?? 'connections'} /> : route.page === 'secrets' ? <TenantSecrets key={route.tenantId} context={tenantContext} /> : route.page === 'credentials' ? <TenantCredentials key={route.tenantId} context={tenantContext} /> : route.page === 'workflows' ? <Suspense fallback={<AccessLoading />}><TenantWorkflows key={`${route.tenantId}:${tenantContext.principalId}`} context={tenantContext} /></Suspense> : route.page === 'evaluations' ? <TenantEvaluations key={route.tenantId} context={tenantContext} /> : route.page === 'definitions' ? route.draftId && route.definitionId ? <TenantDefinitionEditor key={`${route.tenantId}:${route.draftId}`} context={tenantContext} id={route.definitionId} draftId={route.draftId} page={route.draftPage ?? 'overview'} /> : route.definitionId ? <TenantDefinitionDetail key={`${route.tenantId}:${route.definitionId}`} context={tenantContext} id={route.definitionId} /> : <TenantDefinitions key={route.tenantId} context={tenantContext} /> : route.page === 'members' ? <Members context={tenantContext} /> : route.page === 'roles' ? <Roles context={tenantContext} /> : route.page === 'governance' ? <TenantGovernance key={route.tenantId} context={tenantContext} /> : route.page === 'audit' ? <TenantAudit key={route.tenantId} context={tenantContext} /> : <OwnAccess context={tenantContext} /> : route.view === 'account' ? route.page === 'profile' ? <AccountProfile context={account.data} /> : <OrganizationsList key={organizationsVersion} /> : <AccessUnavailable />}
  </AdminShell>;
}
function OwnAccess({ context }: { context: TenantContext }) {
  const groups = [...new Set(context.permissions.map(permissionFamily))];
  return <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form"><Flex wrap gap="var(--ac-space-compact)">{context.roles.length ? context.roles.map(role => <Tag key={role.roleId}>{role.name}</Tag>) : <Typography.Text type="secondary">No organization-wide role is assigned.</Typography.Text>}</Flex>
    <Alert type="info" showIcon title="People and agents have separate authority." description="Chat access lets you reach an agent. Its instructions and judgment guide its response; its own roles control what it can do." />
    <Typography.Title level={2}>Your organization permissions</Typography.Title><Typography.Paragraph type="secondary">These permissions apply across {context.tenant.name}. Additional access may be assigned on individual resources.</Typography.Paragraph>
    {groups.length ? <Collapse items={groups.map(group => ({ key: group, label: group, children: <Flex vertical gap="var(--ac-space-compact)">{context.permissions.filter(key => permissionFamily(key) === group).map(key => <Typography.Text key={key}>{permissionLabel(key)}</Typography.Text>)}</Flex> }))} /> : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No organization-wide permissions. Contact an owner if you need more access." />}
  </Flex>;
}
