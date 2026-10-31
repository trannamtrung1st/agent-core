import { useRef, useState } from 'react';
import { App, Button, Drawer, Dropdown, Empty, Flex, Form, Grid, Select, Table, Tag, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { navigateToAppPath } from '../../app/appRoute';
import { identityRequest, tenantApi, type Page, type TenantContext } from '../../services/identity';
import { AdminCollectionToolbar, adminCollectionPagination } from '../admin/AdminCollectionToolbar';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantAgentPath } from './accessRoute';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

type Agent = { managedKind?: string | null; principalId: string; systemIdentityId: string; instance: { instanceId: string; revision: number; lifecycle: string; showInChat?: boolean; persona: { name: string; role: string } } };
export function TenantAgents({ context }: { context: TenantContext }) {
  const screens = Grid.useBreakpoint();
  const tenant = context.tenant.tenantId; const [page, setPage] = useState(0); const [search, setSearch] = useState(''); const [creating, setCreating] = useState(false); const [visibility, setVisibility] = useState('all');
  const opener = useRef<HTMLElement | null>(null);
  const closeCreation = () => { setCreating(false); requestAnimationFrame(() => opener.current?.isConnected ? opener.current.focus({ preventScroll: true }) : document.querySelector<HTMLElement>('[data-admin-page-heading]')?.focus()); };
  const state = useIdentityQuery<Page<Agent>>(tenantApi(tenant, `/agents?page=${page}&pageSize=20`));
  return <Flex vertical gap="var(--ac-space-section)">
    <AdminCollectionToolbar label="agents on this page" value={search} onChange={setSearch} showScrollHint={false} filters={<><Select aria-label="Visibility on this page" value={visibility} onChange={setVisibility} options={[{ value: 'all', label: 'All on this page' }, { value: 'visible', label: 'Visible on this page' }, { value: 'hidden', label: 'Hidden on this page' }]} /><Button onClick={state.reload}>Reload</Button>{context.permissions.includes('agents.create') && <Button type="primary" onClick={event => { opener.current = event.currentTarget; setCreating(true); }}>New agent</Button>}</>} />
    <Typography.Paragraph type="secondary">Each instance has its own identity and permissions. Chat access and the agent's execution authority are managed separately.</Typography.Paragraph>
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <Table<Agent> className="admin-collection-table" rowKey={row => row.instance.instanceId}
      dataSource={state.data?.items.filter(row => (visibility === 'all' || (row.instance.showInChat !== false) === (visibility === 'visible'))).filter(row => `${row.instance.persona.name} ${row.instance.persona.role}`.toLowerCase().includes(search.trim().toLowerCase()))}
      pagination={{ ...adminCollectionPagination, current: page + 1, pageSize: 20, total: state.data?.total, showSizeChanger: false, onChange: next => setPage(next - 1) }}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching agents on this page. Clear search or choose another page.' : 'No agents you can read in this organization.'} /> }} columns={[
        { title: 'Agent', render: (_, row) => <Flex vertical align="start" gap="var(--ac-space-tight)"><Typography.Link strong href={tenantAgentPath(tenant, row.instance.instanceId, 'profile')} onClick={followAppLink} aria-label={`Profile for ${row.instance.persona.name}`}>{row.instance.persona.name}</Typography.Link>{!screens.lg && <><Typography.Text type="secondary">{row.instance.persona.role}</Typography.Text><Flex wrap gap="var(--ac-space-tight)"><Tag>{row.instance.lifecycle}</Tag>{row.managedKind && <Tag>System-managed</Tag>}<Tag>{row.instance.showInChat !== false ? 'Visible in Chat' : 'Hidden from Chat'}</Tag></Flex></>}</Flex> },
        { title: 'Role', responsive: ['lg'], render: (_, row) => row.instance.persona.role },
        { title: 'Lifecycle', responsive: ['lg'], render: (_, row) => <Tag>{row.instance.lifecycle}</Tag> },
        { title: 'Managed', responsive: ['lg'], render: (_, row) => row.managedKind ? 'System-managed' : 'Organization' },
        { title: 'Chat visibility', responsive: ['lg'], render: (_, row) => row.instance.showInChat !== false ? 'Visible' : 'Hidden' },
        { title: 'Actions', width: 180, render: (_, row) => <Flex wrap gap="var(--ac-space-compact)"><Typography.Link href={tenantAgentPath(tenant, row.instance.instanceId)} onClick={followAppLink} aria-label={`Access for ${row.instance.persona.name}`}>Access</Typography.Link><Dropdown trigger={['click']} menu={{ items: [
          { key: 'connections', label: <a href={tenantAgentPath(tenant, row.instance.instanceId, 'connections')} onClick={followAppLink} aria-label={`Connections for ${row.instance.persona.name}`}>Connections</a> },
          { key: 'activity', label: <a href={tenantAgentPath(tenant, row.instance.instanceId, 'activity-sessions')} onClick={followAppLink} aria-label={`Activity for ${row.instance.persona.name}`}>Activity</a> },
          { key: 'lifecycle', label: <a href={tenantAgentPath(tenant, row.instance.instanceId, 'lifecycle')} onClick={followAppLink} aria-label={`Lifecycle for ${row.instance.persona.name}`}>Lifecycle</a> }
        ] }}><Button aria-label={`More actions for ${row.instance.persona.name}`}>More</Button></Dropdown></Flex> }

      ]} />}
    {creating && <CreateAgent context={context} onClose={closeCreation} />}
  </Flex>;
}
function CreateAgent({ context, onClose }: { context: TenantContext; onClose: () => void }) {
  const { message } = App.useApp(); const command = useTenantCommand(); const [form] = Form.useForm<{ template: string }>(); const [dirty, setDirty] = useState(false);
  const guard = useAccessDraftGuard(dirty, command.busy, onClose);
  const templates = useIdentityQuery<{ items: { id: string; version: number; identity: { name: string; role: string } }[] }>(tenantApi(context.tenant.tenantId, '/agent-templates'));
  return <>{guard.holder}<Drawer open className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} title="New agent" onClose={guard.close} keyboard={!command.busy} maskClosable={!command.busy} closable={!command.busy}
    footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button disabled={command.busy} onClick={guard.close}>Cancel</Button><Button type="primary" form="tenant-agent-create" htmlType="submit" loading={command.busy} disabled={command.requiresReload || !templates.data?.items.length}>Create agent</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)"><Typography.Paragraph>Create an independent agent in {context.tenant.name} from an available template. The template supplies configuration; it does not grant execution permissions.</Typography.Paragraph>
      {command.error !== undefined && <AccessError error={command.error} />}{templates.error ? <AccessError error={templates.error} retry={templates.reload} /> : !templates.loading && !templates.data?.items.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No available templates. Ask an organization administrator to check the catalog." />}
      <Form form={form} id="tenant-agent-create" layout="vertical" disabled={command.busy} onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)} onFinish={values => void command.run(async signal => {
        const source = templates.data?.items.find(item => `${item.id}:${item.version}` === values.template);
        if (!source) throw new Error('Reload the available templates before creating an instance.');
        const current = await identityRequest<TenantContext>(tenantApi(context.tenant.tenantId, '/access/me'), { signal });
        return identityRequest<Agent>(tenantApi(context.tenant.tenantId, '/agents'), { method: 'POST', signal, body: { templateId: source.id, version: source.version, expectedRevision: current.revision } });
      }, saved => { guard.release(); setDirty(false); message.success('Agent instance created.'); navigateToAppPath(tenantAgentPath(context.tenant.tenantId, saved.instance.instanceId)); })}>
        <Form.Item name="template" label="Agent template" rules={[{ required: true, message: 'Choose an agent template.' }]}><Select autoFocus showSearch optionFilterProp="label" loading={templates.loading} placeholder="Choose a template" options={templates.data?.items.map(item => ({ value: `${item.id}:${item.version}`, label: `${item.identity.name} · ${item.identity.role} · v${item.version}` }))} /></Form.Item>
      </Form>
    </Flex>
  </Drawer></>;
}
