import { useState } from 'react';
import { Alert, App, Button, Empty, Flex, Form, Input, Pagination, Select, Table, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { followAppLink } from '../../app/appLink';
import { IdentityError, identityRequest, tenantApi, type Page, type Role } from '../../services/identity';
import { tenantAccessPath } from './accessRoute';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

type Principal = { principalId: string; name: string; kind: 'user' | 'agent' };
type Assignment = { assignmentId: string; principalId: string; principalName: string; principalKind: string; roleId: string; roleName: string; inherited: boolean; canRevoke: boolean };
type AccessEditor = { name: string; securityRevision: number; canAssign: boolean; canManageRoles: boolean; assignments: Page<Assignment>; principals: Page<Principal>; roles: Role[] };
export function TenantResourceAccess({ tenant, resourceType, resourceId }: { tenant: string; resourceType: 'space' | 'secret' | 'workspace' | 'resource'; resourceId: string }) {
  const [page, setPage] = useState(0); const [principalPage, setPrincipalPage] = useState(0); const [search, setSearch] = useState(''); const [principal, setPrincipal] = useState<string>(); const [role, setRole] = useState<string>();
  const { modal, message } = App.useApp(); const command = useTenantCommand();
  const state = useIdentityQuery<AccessEditor>(tenantApi(tenant, `/access/resources/${resourceType}/${encodeURIComponent(resourceId)}/editor?${new URLSearchParams({ page: String(page), principalPage: String(principalPage), search })}`));
  const view = state.data; const selected = view?.principals.items.find(p => p.principalId === principal); const selectedRole = view?.roles.find(r => r.roleId === role);
  const blocked = command.busy || command.requiresReload || command.error instanceof IdentityError && [401, 403, 404, 409].includes(command.error.status);
  const reload = () => { command.clearError(); state.reload(); };
  const changed = () => { setPrincipal(undefined); setRole(undefined); state.reload(); message.success('Access updated. Future actions check current permissions.'); };
  return <Flex vertical gap="var(--ac-space-section)"><Flex wrap justify="space-between" align="center" gap="var(--ac-space-compact)"><Typography.Title level={5} style={{ margin: 0 }}>{resourceType === 'resource' ? 'Snapshot permissions' : resourceType === 'workspace' ? 'Home file permissions' : resourceType === 'secret' ? 'Secret permissions' : 'Space permissions'}</Typography.Title><Button disabled={command.busy} onClick={reload}>Reload access</Button></Flex>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>{resourceType === 'resource' ? 'Snapshot reading and management are independent from membership availability and home file access.' : resourceType === 'workspace' ? 'File reading, writing and deletion are independent from Space membership and context.' : resourceType === 'secret' ? 'Reading metadata, managing the value and using a Secret are separate permissions.' : 'Membership supplies context and availability. These roles independently authorize Space, membership, Environment and variable actions.'} An assignment applies only the role permissions relevant to its scope.</Typography.Paragraph>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {state.error ? <AccessError error={state.error} retry={reload} /> : !view ? <AccessLoading /> : <>
      <Typography.Text strong>{view.name}</Typography.Text>
      <Table<Assignment> className="admin-collection-table" rowKey="assignmentId" dataSource={view.assignments.items} pagination={false} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No applicable role assignments on this page." /> }} columns={[
        { title: 'Access', render: (_, a) => <Flex vertical gap="var(--ac-space-tight)"><Typography.Text strong>{a.principalName}</Typography.Text><Typography.Text>{a.roleName}</Typography.Text><Typography.Text type="secondary">{a.principalKind === 'user' ? 'Person' : 'Agent'} · {a.inherited ? 'Organization · inherited' : 'This resource'}</Typography.Text></Flex> },
        { title: 'Actions', width: 105, render: (_, a) => a.canRevoke && <Button danger disabled={blocked} aria-label={`Revoke ${a.roleName} from ${a.principalName}`} onClick={() => confirmAction(modal, { title: `Revoke ${a.roleName}?`, content: `${a.principalName} · ${view.name}. ${a.inherited ? 'This removes the recorded organization-wide assignment, including access inherited by other resources.' : 'This removes only the recorded assignment on this resource.'}`, okText: 'Revoke role', danger: true, onOk: () => command.run(signal => identityRequest(tenantApi(tenant, `/role-assignments/${a.assignmentId}?expectedRevision=${view.securityRevision}`), { method: 'DELETE', signal }), changed) })}>Revoke</Button> }
      ]} />
      {view.assignments.total > 30 && <Pagination current={page + 1} total={view.assignments.total} pageSize={30} showSizeChanger={false} responsive onChange={p => setPage(p - 1)} />}
      {view.canAssign ? <Flex vertical gap="var(--ac-space-default)"><Typography.Title level={5} style={{ margin: 0 }}>Assign a role on this resource</Typography.Title>
        <Input.Search aria-label="Find a person or agent for resource access" placeholder="Search identities by name" allowClear disabled={blocked} onSearch={value => { setSearch(value); setPrincipalPage(0); setPrincipal(undefined); }} />
        <Form layout="vertical" disabled={blocked}><Form.Item label="Person or agent"><Select aria-label="Resource access identity" showSearch optionFilterProp="label" value={principal} placeholder="Choose an identity" options={view.principals.items.map(p => ({ value: p.principalId, label: `${p.name} · ${p.kind === 'user' ? 'Person' : 'Agent'}` }))} onChange={value => { setPrincipal(value); setRole(undefined); }} /></Form.Item>
          {view.principals.total > 30 && <Pagination current={principalPage + 1} pageSize={30} total={view.principals.total} showSizeChanger={false} responsive onChange={p => { setPrincipalPage(p - 1); setPrincipal(undefined); }} />}
          <Form.Item label="Role"><Select aria-label="Resource access role" showSearch optionFilterProp="label" value={role} placeholder="Choose a role" options={view.roles.filter(r => selected?.kind !== 'agent' || !r.humanOnly).map(r => ({ value: r.roleId, label: r.name + (r.humanOnly ? ' · People only' : '') }))} onChange={setRole} /></Form.Item>
          <Button type="primary" disabled={blocked || !selected || !selectedRole || selected.kind === 'agent' && selectedRole.humanOnly} onClick={() => selected && selectedRole && confirmAction(modal, { title: 'Assign resource role?', content: `${selectedRole.name} to ${selected.name} on ${view.name}. This does not create a membership, binding or approval exemption.`, okText: 'Assign role', onOk: () => command.run(signal => identityRequest(tenantApi(tenant, '/role-assignments'), { method: 'POST', signal, body: { principalId: selected.principalId, roleId: selectedRole.roleId, resourceType, resourceId, expectedRevision: view.securityRevision } }), changed) })}>Assign role</Button>
        </Form>{selectedRole && <Flex wrap gap="var(--ac-space-tight)">{selectedRole.permissions.map(key => <Tag key={key}>{key}</Tag>)}</Flex>}
      </Flex> : <Alert type="info" showIcon title="Access assignments are read-only" description="Changing roles requires assignment authority on the exact recorded scope." />}
      {view.canManageRoles && <Typography.Link href={tenantAccessPath(tenant, 'roles')} onClick={followAppLink}>Manage role definitions</Typography.Link>}
    </>}
  </Flex>;
}
