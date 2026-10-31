import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useState } from 'react';
import { Alert, Button, Empty, Flex, Form, Input, Modal, Pagination, Typography, message } from 'antd';
import { ApartmentOutlined, RightOutlined } from '@ant-design/icons';
import { approveAppNavigation, navigateToAppPath } from '../../app/appRoute';
import { AdminCollectionToolbar } from '../admin/AdminCollectionToolbar';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { appMessageConfig } from '../../app/appFeedback';
import { identityRequest, membershipsApi, type Membership, type Page } from '../../services/identity';
import { AccessError, AccessLoading, accessErrorMessage } from './AccessFeedback';
import { tenantAccessPath } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';

export function OrganizationsList({ selectedTenantId, switching = false, chat = true }: { selectedTenantId?: string; switching?: boolean; chat?: boolean }) {
  const [page, setPage] = useState(0); const [query, setQuery] = useState('');
  const state = useIdentityQuery<Page<Membership>>(membershipsApi(page, query));
  const open = async (tenantId: string) => {
    const target = chat ? `/t/${tenantId}` : tenantAccessPath(tenantId);
    if (switching || chat) {
      if (await approveAppNavigation(target)) window.location.assign(target);
    } else navigateToAppPath(target);
  };
  return <Flex vertical gap="var(--ac-space-section)">
    <AdminCollectionToolbar showScrollHint={false} label="organizations" value={query} onChange={value => { setQuery(value); setPage(0); }} />
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <>
      {!state.data?.items.length ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={query ? 'No organizations match your search.' : 'You have not joined an organization yet. Ask an owner for an invitation.'} /> : <div className="access-organization-list">{state.data.items.map(item => <div key={item.tenantId} className="access-organization-row">
        <Flex vertical gap="var(--ac-space-tight)" className="access-row-summary"><Typography.Text strong>{item.name}</Typography.Text><Typography.Text type="secondary">{item.slug}{selectedTenantId === item.tenantId ? ' · Current organization' : ''}</Typography.Text></Flex>
        <Flex wrap className="access-organization-actions" gap="var(--ac-space-compact)"><Button href={tenantAccessPath(item.tenantId, 'agents')} onClick={event => { event.preventDefault(); navigateToAppPath(tenantAccessPath(item.tenantId, 'agents')); }}>Manage</Button><Button type="primary" icon={<RightOutlined aria-hidden />} iconPlacement="end" onClick={() => void open(item.tenantId)} aria-label={`Open ${item.name}`}>{chat ? 'Open Chat' : 'Open'}</Button></Flex>
      </div>)}</div>}
      {!!state.data?.total && <>{state.data.total <= 10 && <Typography.Text type="secondary">{state.data.total} {state.data.total === 1 ? 'organization' : 'organizations'}</Typography.Text>}<Pagination hideOnSinglePage current={page + 1} pageSize={10} total={state.data.total} onChange={value => setPage(value - 1)} showSizeChanger={false} responsive showTotal={total => `${total} ${total === 1 ? 'organization' : 'organizations'}`} /></>}
    </>}
  </Flex>;
}
export function TenantSwitcher({ tenantId, name, chat = false }: { tenantId: string; name: string; chat?: boolean }) {
  const [open, setOpen] = useState(false);
  return <><Button className="access-tenant-switcher" icon={<ApartmentOutlined aria-hidden />} title={name} onClick={() => setOpen(true)} aria-label={`Switch organization. Current: ${name}`}><span>{name}</span></Button><Modal width={640} title="Switch organization" open={open} onCancel={() => setOpen(false)} footer={null} destroyOnHidden><OrganizationsList selectedTenantId={tenantId} switching chat={chat} /></Modal></>;
}
export function CreateOrganization({ onCreated }: { onCreated: () => void }) {
  const [open, setOpen] = useState(false); const [busy, setBusy] = useState(false); const [dirty, setDirty] = useState(false); const [error, setError] = useState<unknown>();
  const [form] = Form.useForm<{ name: string; slug: string }>(); const [feedback, holder] = message.useMessage(appMessageConfig);
  const discard = () => { setOpen(false); setDirty(false); form.resetFields(); };
  const guard = useAccessDraftGuard(open && dirty, busy, discard);
  const save = async (values: { name: string; slug: string }) => {
    setBusy(true); setError(undefined);
    try { await identityRequest('/api/v2/system/tenants', { method: 'POST', body: values }); setDirty(false); setOpen(false); form.resetFields(); onCreated(); void feedback.success('Organization created. You are its first owner.'); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  return <>{holder}{guard.holder}<Button type="primary" onClick={() => { setError(undefined); setOpen(true); }}>Create organization</Button><Modal title="Create organization" open={open} onCancel={guard.close} footer={null} destroyOnHidden>
    <Form form={form} layout="vertical" disabled={busy} onValuesChange={() => setDirty(true)} onFinish={values => void save(values)} onFinishFailed={reason => showAdminFormValidation(feedback, form, reason)}>
      <Typography.Paragraph type="secondary">Organizations keep members, agents and access separate. You will be the first owner.</Typography.Paragraph>
      {error !== undefined && <Alert type="error" showIcon role="alert" title={accessErrorMessage(error)} />}
      <Form.Item name="name" label="Organization name" rules={[{ required: true, whitespace: true, message: 'Enter an organization name.' }, { max: 160 }]}><Input autoFocus autoComplete="organization" /></Form.Item>
      <Form.Item name="slug" label="Organization identifier" extra="2–64 lowercase letters, digits or internal hyphens." rules={[{ required: true }, { pattern: /^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]$/, message: 'Use 2–64 lowercase letters, digits or internal hyphens.' }]}><Input /></Form.Item>
      <Flex justify="end" wrap gap="var(--ac-space-compact)"><Button disabled={busy} onClick={guard.close}>Cancel</Button><Button type="primary" htmlType="submit" loading={busy}>Create organization</Button></Flex>
    </Form>
  </Modal></>;
}
