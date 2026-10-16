import { useState } from 'react';
import { App, Button, Drawer, Empty, Flex, Form, Input, Select, Table, Tag, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { navigateToAppPath } from '../../app/appRoute';
import { identityRequest, tenantApi, type Page, type TenantContext } from '../../services/identity';
import { type DefinitionTemplate, type TenantDefinitionSummary, type TenantDraft } from '../../services/tenantDefinitions';
import { AdminCollectionToolbar } from '../admin/AdminCollectionToolbar';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { tenantDefinitionPath, tenantDraftPath } from './accessRoute';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useTenantCommand } from './useTenantCommand';

export function TenantDefinitions({ context }: { context: TenantContext }) {
  const tenant = context.tenant.tenantId;
  const [page, setPage] = useState(0); const [search, setSearch] = useState(''); const [creating, setCreating] = useState(false);
  const state = useIdentityQuery<Page<TenantDefinitionSummary>>(tenantApi(tenant, `/agent-definitions?page=${page}&pageSize=20`));
  return <Flex vertical gap="var(--ac-space-section)">
    <AdminCollectionToolbar label="loaded definitions" value={search} onChange={setSearch} showScrollHint={false} filters={<>
      <Button onClick={state.reload}>Reload</Button>
      {context.permissions.includes('agent-definitions.create') && <Button type="primary" onClick={() => setCreating(true)}>New definition</Button>}
    </>} />
    {state.error ? <AccessError error={state.error} retry={state.reload} /> : state.loading ? <AccessLoading /> : <Table className="admin-collection-table" rowKey={item => item.definition.definitionId}
      dataSource={state.data?.items.filter(item => item.displayName.toLowerCase().includes(search.trim().toLowerCase()))}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search ? 'No matching definitions on this page. Clear search or choose another page.' : 'No definitions you can read in this organization.'} /> }}
      pagination={{ current: page + 1, total: state.data?.total, pageSize: 20, showSizeChanger: false, responsive: true, onChange: value => setPage(value - 1) }} columns={[
        { title: 'Definition', render: (_, item) => <Flex vertical><Typography.Link href={tenantDefinitionPath(tenant, item.definition.definitionId)} onClick={followAppLink}>{item.displayName}</Typography.Link><Typography.Text type="secondary">{item.draftCount} {item.draftCount === 1 ? 'draft' : 'drafts'}</Typography.Text></Flex> },
        { title: 'Publication', width: 130, render: (_, item) => item.latestVersion ? <Tag>Version {item.latestVersion}</Tag> : <Typography.Text type="secondary">Unpublished</Typography.Text> }
      ]} />}
    {creating && <CreateDefinition context={context} onClose={() => setCreating(false)} onCreated={draft => navigateToAppPath(tenantDraftPath(tenant, draft.definitionId, draft.draftId, 'profile'))} />}
  </Flex>;
}
function CreateDefinition({ context, onClose, onCreated }: { context: TenantContext; onClose: () => void; onCreated: (draft: TenantDraft) => void }) {
  const { message } = App.useApp(); const command = useTenantCommand(); const [form] = Form.useForm<{ name: string; template?: string }>();
  const [dirty, setDirty] = useState(false); const guard = useAccessDraftGuard(dirty, command.busy, onClose);
  const templates = useIdentityQuery<{ items: DefinitionTemplate[]; canFork: Record<string, boolean> }>(tenantApi(context.tenant.tenantId, '/definition-templates'));
  return <>{guard.holder}<Drawer open title="New definition" onClose={guard.close} className="access-editor-drawer" size={640} styles={{ wrapper: { maxWidth: '100vw' } }} keyboard={!command.busy} maskClosable={!command.busy} closable={!command.busy}
    footer={<Flex gap="var(--ac-space-compact)" justify="end"><Button disabled={command.busy} onClick={guard.close}>Cancel</Button><Button type="primary" htmlType="submit" form="tenant-definition-create" loading={command.busy}>Create definition</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)">
      <Typography.Paragraph>Create a definition in {context.tenant.name}. Start blank or copy an exact shipped template with its approved resources. Definition settings do not grant agent permissions.</Typography.Paragraph>
      {command.error !== undefined && <AccessError error={command.error} />}
      {templates.error && <AccessError error={templates.error} retry={templates.reload} />}
      <Form id="tenant-definition-create" form={form} layout="vertical" disabled={command.busy} onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)}
        onFinish={values => void command.run(async signal => {
          const access = await identityRequest<TenantContext>(tenantApi(context.tenant.tenantId, '/access/me'), { signal });
          const source = templates.data?.items.find(t => `${t.id}:${t.version}` === values.template);
          if (values.template && !source) throw new Error('The selected template is no longer available.');
          if (source && !templates.data?.canFork[`${source.id}:${source.version}`]) throw new Error('Your current access cannot copy this template. Choose a blank definition or ask an access administrator.');
          return identityRequest<TenantDraft>(tenantApi(context.tenant.tenantId, source ? '/agent-definitions/from-template' : '/agent-definitions'), { method: 'POST', signal, body: source ? { name: values.name, templateId: source.id, version: source.version, expectedRevision: access.revision } : { name: values.name, expectedRevision: access.revision } });
        }, draft => { guard.release(); setDirty(false); message.success('Definition created.'); onCreated(draft); })}>
        <Form.Item name="name" label="Definition name" rules={[{ required: true, whitespace: true, message: 'Enter a definition name.' }, { max: 80 }]}><Input autoFocus maxLength={80} /></Form.Item>
        <Form.Item name="template" label="Starting point" extra="Shipped templates are read-only. Copying their files requires organization resource creation access."><Select allowClear placeholder="Blank definition" loading={templates.loading} showSearch optionFilterProp="label" options={templates.data?.items.map(item => ({ value: `${item.id}:${item.version}`, label: `${item.identity.name} · v${item.version}${templates.data?.canFork[`${item.id}:${item.version}`] ? '' : ' · unavailable with current access'}`, disabled: !templates.data?.canFork[`${item.id}:${item.version}`] }))} /></Form.Item>
      </Form>
    </Flex>
  </Drawer></>;
}
