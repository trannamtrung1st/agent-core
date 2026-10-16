import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useState } from 'react';
import { Alert, Button, Drawer, Empty, Flex, Form, Input, Modal, Select, Table, Tabs, Tag, Typography, message } from 'antd';
import { CopyOutlined, PlusOutlined, SettingOutlined } from '@ant-design/icons';
import { appMessageConfig } from '../../app/appFeedback';
import { confirmAction } from '../../app/confirmAction';
import { AdminCollectionToolbar } from '../admin/AdminCollectionToolbar';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { identityRequest, tenantApi, type Member, type Page, type Role, type TenantContext } from '../../services/identity';
import { AccessError, AccessLoading, accessErrorMessage } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';

const memberStatus = (value: number) => value === 0 ? 'Active' : value === 1 ? 'Suspended' : 'Removed';
export function Members({ context }: { context: TenantContext }) {
  const [showInvitations, setShowInvitations] = useState(false);
  const [page, setPage] = useState(0); const [query, setQuery] = useState(''); const [invite, setInvite] = useState(false); const [selected, setSelected] = useState<string | null>(null);
  const state = useIdentityQuery<Page<Member>>(tenantApi(context.tenant.tenantId, `/members?page=${page}&pageSize=10&q=${encodeURIComponent(query)}`));
  const roles = useIdentityQuery<{ items: Role[]; revision: number }>(context.permissions.includes('iam.roles.read') ? tenantApi(context.tenant.tenantId, '/roles') : null);
  const [error, setError] = useState<unknown>(); const [busy, setBusy] = useState(false); const [assignRole, setAssignRole] = useState<string>();
  const [feedback, holder] = message.useMessage(appMessageConfig); const [modal, modalHolder] = Modal.useModal();
  const canAssign = context.permissions.includes('iam.roles.assign');
  const canInvite = context.permissions.includes('members.invite') && canAssign;
  const activeMember = state.data?.items.find(member => member.userId === selected);
  const closeMember = () => { if (!busy) { setSelected(null); setAssignRole(undefined); setError(undefined); } };
  const roleName = (id: string) => roles.data?.items.find(role => role.roleId === id)?.name ?? 'Assigned role';
  const change = async (path: string, method: string, body?: unknown) => {
    setBusy(true); setError(undefined);
    try { await identityRequest(tenantApi(context.tenant.tenantId, path), { method, body }); state.reload(); roles.reload(); setAssignRole(undefined); void feedback.success('Access updated. The change applies to the next protected action.'); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const revision = state.data?.revision ?? context.revision;
  const decision = (title: string, description: string, action: () => Promise<void>, danger = false) => confirmAction(modal, { title, content: <>{description}<Typography.Paragraph type="secondary">Organization: {context.tenant.name}</Typography.Paragraph></>, okText: title, danger, onOk: action });
  if (showInvitations) return <Invitations context={context} onMembers={() => setShowInvitations(false)} />;
  return <Flex vertical gap="var(--ac-space-section)">{holder}{modalHolder}
    <Tabs items={[{ key: 'members', label: 'Members' }, ...(context.permissions.includes('members.invite') ? [{ key: 'invitations', label: 'Invitations' }] : [])]} activeKey="members" onChange={value => { if (value === 'invitations') setShowInvitations(true); }} />
    <AdminCollectionToolbar showScrollHint={false} label="members" value={query} onChange={value => { setQuery(value); setPage(0); setSelected(null); }} filters={canInvite ? <Button type="primary" icon={<PlusOutlined aria-hidden />} onClick={() => setInvite(true)}>Invite member</Button> : <Typography.Text type="secondary">Inviting members requires access-management permission.</Typography.Text>} />
    {error !== undefined && !selected && <AccessError error={error} retry={state.reload} />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <Table<Member> className="admin-collection-table" rowKey="userId" dataSource={state.data?.items} pagination={{ current: page + 1, pageSize: 10, total: state.data?.total, showSizeChanger: false, responsive: true, onChange: value => { setPage(value - 1); setSelected(null); }, showTotal: total => `${total} ${total === 1 ? 'member' : 'members'}` }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No members to show." /> }} columns={[
      { title: 'Member', render: (_, member) => <Flex vertical gap="var(--ac-space-tight)"><Typography.Text strong>{member.displayName}</Typography.Text><Typography.Text type="secondary">{member.login.toLowerCase()}</Typography.Text><Flex wrap gap="var(--ac-space-tight)"><Tag>{memberStatus(member.status)}</Tag>{member.roles.map(role => <Typography.Text key={role.assignmentId} type="secondary">{roleName(role.roleId)}</Typography.Text>)}</Flex></Flex> },
      { title: 'Actions', width: 110, render: (_, member) => <Button icon={<SettingOutlined aria-hidden />} onClick={() => { setError(undefined); setAssignRole(undefined); setSelected(member.userId); }} aria-label={`Manage ${member.displayName}`}>Access</Button> }
    ]} />}
    <Drawer className="access-editor-drawer" title="Member access" open={!!selected} onClose={closeMember} size="large" styles={{ wrapper: { maxWidth: '100vw' } }}>
      {error !== undefined && <div className="access-form-error"><AccessError error={error} retry={() => { state.reload(); roles.reload(); }} /></div>}
      {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : activeMember ? <Flex vertical gap="var(--ac-space-section)">
        <Flex vertical gap="var(--ac-space-tight)"><Typography.Title level={4}>{activeMember.displayName}</Typography.Title><Typography.Text type="secondary">{activeMember.login.toLowerCase()}</Typography.Text><Typography.Text>{context.tenant.name} · {memberStatus(activeMember.status)}</Typography.Text></Flex>
        <Typography.Paragraph type="secondary">Organization-wide roles are inherited by its resources. Changing this membership does not change the person's access to other organizations.</Typography.Paragraph>
        <Typography.Title level={5}>Assigned roles</Typography.Title>
        {!activeMember.roles.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No roles assigned. This member has no granted permissions." />}
        {activeMember.roles.map(assignment => <Flex key={assignment.assignmentId} justify="space-between" wrap align="center" gap="var(--ac-space-compact)"><Flex vertical><Typography.Text strong>{roleName(assignment.roleId)}</Typography.Text><Typography.Text type="secondary">{assignment.scope.kind === 1 ? 'Organization · inherited by resources' : 'Resource-specific access'}</Typography.Text></Flex>{canAssign && <Button danger disabled={busy} onClick={() => decision('Revoke role', `Revoke ${roleName(assignment.roleId)} from ${activeMember.displayName}?`, () => change(`/role-assignments/${assignment.assignmentId}?expectedRevision=${revision}`, 'DELETE'), true)}>Revoke</Button>}</Flex>)}
        {canAssign && activeMember.status === 0 && <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>Assign organization role</Typography.Text>{roles.error ? <AccessError error={roles.error} retry={roles.reload} /> : <Flex wrap gap="var(--ac-space-compact)"><Select className="access-role-select" aria-label="Role to assign" placeholder="Choose a role" showSearch optionFilterProp="label" value={assignRole} onChange={setAssignRole} loading={roles.loading} disabled={busy} options={roles.data?.items.filter(role => !activeMember.roles.some(current => current.roleId === role.roleId && current.scope.kind === 1)).map(role => ({ value: role.roleId, label: role.name + (role.humanOnly ? ' · People only' : '') }))} /><Button disabled={!assignRole || busy} onClick={() => { if (assignRole) decision('Assign role', `Assign ${roleName(assignRole)} to ${activeMember.displayName} throughout this organization?`, () => change('/role-assignments', 'POST', { principalId: activeMember.principalId, roleId: assignRole, expectedRevision: revision })); }}>Assign role</Button></Flex>}</Flex>}
        {!canAssign && <Typography.Paragraph type="secondary">You can inspect this membership. Changing roles requires permission to assign roles.</Typography.Paragraph>}
        <Typography.Title level={5}>Membership status</Typography.Title>
        <Flex wrap gap="var(--ac-space-compact)">
          {context.permissions.includes('members.update') && <Button disabled={busy} onClick={() => decision(activeMember.status === 0 ? 'Suspend member' : 'Restore member', `${activeMember.status === 0 ? 'Suspend' : 'Restore'} ${activeMember.displayName}'s membership?`, () => change(`/members/${activeMember.userId}/status`, 'PUT', { status: activeMember.status === 0 ? 1 : 0, expectedRevision: revision }), activeMember.status === 0)}>{activeMember.status === 0 ? 'Suspend member' : 'Restore member'}</Button>}
          {context.permissions.includes('members.remove') && activeMember.status !== 2 && <Button danger disabled={busy} onClick={() => decision('Remove member', `Remove ${activeMember.displayName} from this organization? Their account and other memberships remain available.`, () => change(`/members/${activeMember.userId}/status`, 'PUT', { status: 2, expectedRevision: revision }), true)}>Remove member</Button>}
        </Flex>
        <Typography.Text type="secondary">At least one active owner must remain.</Typography.Text>
      </Flex> : <Empty description="This member is no longer on the current page."><Button onClick={closeMember}>Back to members</Button></Empty>}
    </Drawer>
    {invite && <InviteMember context={context} roles={roles.data?.items ?? []} rolesError={roles.error} rolesRetry={roles.reload} revision={revision} onClose={() => setInvite(false)} onCreated={() => { state.reload(); roles.reload(); }} />}
  </Flex>;
}
function InviteMember({ context, roles, rolesError, rolesRetry, revision, onClose, onCreated }: { context: TenantContext; roles: Role[]; rolesError?: Error; rolesRetry: () => void; revision: number; onClose: () => void; onCreated: () => void }) {
  const [form] = Form.useForm<{ login: string; roleId: string }>(); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(); const [dirty, setDirty] = useState(false); const [link, setLink] = useState<string>();
  const [feedback, holder] = message.useMessage(appMessageConfig);
  const [copyError, setCopyError] = useState(false);
  const copyLink = async () => {
    if (!link) return;
    try { await navigator.clipboard.writeText(link); setCopyError(false); void feedback.success('Invitation link copied.'); }
    catch { setCopyError(true); }
  };
  const guard = useAccessDraftGuard(dirty && !link, busy, onClose);
  const save = async (values: { login: string; roleId: string }) => {
    setBusy(true); setError(undefined);
    try { const invitation = await identityRequest<{ invitationId: string; tenantId: string; token: string }>(tenantApi(context.tenant.tenantId, '/members/invite'), { method: 'POST', body: { ...values, expectedRevision: revision } });
      const fragment = new URLSearchParams({ tenant: invitation.tenantId, token: invitation.token });
      setLink(`${window.location.origin}/invitations/${invitation.invitationId}#${fragment}`); onCreated();
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  return <Modal title={link ? 'Invitation created' : 'Invite member'} open onCancel={guard.close} footer={null} destroyOnHidden>{holder}{guard.holder}
    {link ? <Flex vertical gap="var(--ac-space-section)"><Alert type="success" showIcon title="Send this link to the invited person. No email has been sent." /><Typography.Paragraph type="secondary">This invitation is for {context.tenant.name} and expires in seven days. Copy it before closing this dialog.</Typography.Paragraph><Input.TextArea aria-label="Invitation link" value={link} readOnly autoSize={{ minRows: 3, maxRows: 6 }} onFocus={event => event.target.select()} />{copyError && <Alert type="error" showIcon role="alert" title="Copy failed. Select the link and copy it manually, or try copying again." />}<Flex justify="end" gap="var(--ac-space-compact)" wrap><Button icon={<CopyOutlined aria-hidden />} onClick={() => void copyLink()}>Copy invitation link</Button><Button type="primary" onClick={onClose}>Done</Button></Flex></Flex> : <Form form={form} layout="vertical" disabled={busy} onValuesChange={() => setDirty(true)} onFinish={values => void save(values)} onFinishFailed={reason => showAdminFormValidation(feedback, form, reason)}>
      <Typography.Paragraph type="secondary">Invite a person to {context.tenant.name}. Their access to other organizations will stay the same.</Typography.Paragraph>
      {error !== undefined && <div className="access-form-error"><Alert type="error" showIcon role="alert" title={accessErrorMessage(error)} /></div>}
      <Form.Item name="login" label="Email" rules={[{ required: true, message: 'Enter the invited person’s email.' }, { type: 'email', message: 'Enter a valid email address.' }]}><Input type="email" autoFocus autoComplete="off" maxLength={254} /></Form.Item>
      <Form.Item name="roleId" label="Initial role" extra="Roles apply throughout this organization and are inherited by its resources." rules={[{ required: true, message: 'Choose an initial role.' }]}><Select aria-label="Initial role" showSearch optionFilterProp="label" options={roles.map(role => ({ value: role.roleId, label: role.name + (role.humanOnly ? ' · People only' : '') }))} /></Form.Item>
      {rolesError && <AccessError error={rolesError} retry={rolesRetry} />}
      <Flex justify="end" gap="var(--ac-space-compact)" wrap><Button disabled={busy} onClick={guard.close}>Cancel</Button><Button type="primary" htmlType="submit" loading={busy} disabled={!roles.length}>Create invitation</Button></Flex>
    </Form>}
  </Modal>;
}
function Invitations({ context, onMembers }: { context: TenantContext; onMembers: () => void }) {
  type Item = { invitationId: string; loginKey: string; roleName: string; status: string; expiresAt: string };
  const state = useIdentityQuery<{ items: Item[]; revision: number }>(tenantApi(context.tenant.tenantId, '/members/invitations'));
  const [error, setError] = useState<unknown>(); const [busy, setBusy] = useState<string | null>(null); const [modal, holder] = Modal.useModal();
  const revoke = async (invitationId: string) => {
    setBusy(invitationId); setError(undefined);
    try { await identityRequest(tenantApi(context.tenant.tenantId, `/members/invitations/${invitationId}/revoke`), { method: 'POST', body: { expectedRevision: state.data?.revision } }); state.reload(); }
    catch (reason) { setError(reason); } finally { setBusy(null); }
  };
  return <Flex vertical gap="var(--ac-space-section)">{holder}<Tabs items={[{ key: 'members', label: 'Members' }, { key: 'invitations', label: 'Invitations' }]} activeKey="invitations" onChange={onMembers} />
    <Typography.Paragraph type="secondary">The latest 100 invitations. Pending invitations can be revoked before they are accepted. Links are shown only when they are created.</Typography.Paragraph>
    {error !== undefined && <AccessError error={error} retry={state.reload} />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <Table<Item> className="admin-collection-table" rowKey="invitationId" dataSource={state.data?.items} pagination={{ pageSize: 10, responsive: true, showSizeChanger: false }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No invitations yet. Invite a person from Members." /> }} columns={[
      { title: 'Invitation', render: (_, item) => <Flex vertical gap="var(--ac-space-tight)"><Typography.Text strong>{item.loginKey.toLowerCase()}</Typography.Text><Typography.Text type="secondary">{item.roleName}</Typography.Text><Typography.Text type="secondary">Expires <time dateTime={item.expiresAt}>{new Date(item.expiresAt).toLocaleDateString()}</time></Typography.Text><Tag>{item.status}</Tag></Flex> },
      { title: 'Actions', width: 100, render: (_, item) => item.status === 'Pending' ? <Button danger loading={busy === item.invitationId} disabled={busy !== null} onClick={() => confirmAction(modal, { title: 'Revoke invitation?', content: `The invitation for ${item.loginKey.toLowerCase()} to ${context.tenant.name} will stop working.`, okText: 'Revoke invitation', danger: true, onOk: () => revoke(item.invitationId) })}>Revoke</Button> : <Typography.Text type="secondary">Final</Typography.Text> }
    ]} />}
  </Flex>;
}
