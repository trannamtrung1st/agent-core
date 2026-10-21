import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Descriptions, Drawer, Empty, Flex, Form, Input, Modal, Select, Table, Tag, Typography } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import { confirmAction } from '../../app/confirmAction';
import { IdentityError, identityRequest } from '../../services/identity';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { CreateOrganization } from './Organizations';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useIdentityQuery } from './useIdentityQuery';

type Kind = 'accounts' | 'tenants' | 'operators';
interface Row { accountId?: string; tenantId?: string; displayName?: string; name?: string; loginKey?: string; slug?: string; status: number; createdAt: string; version?: string; revision?: number; isOperator?: boolean; operatorVersion?: string }
interface Page { items: Row[]; total: number; page: number; pageSize: number }
interface Filters { q?: string; status?: number; isOperator?: number }
const statuses = ['Active', 'Suspended', 'Removed'];
const idOf = (row: Row) => row.accountId ?? row.tenantId!;
const nameOf = (row: Row) => row.displayName ?? row.name!;

export function SystemAdministration({ kind, permissions }: { kind: Kind; permissions: string[] }) {
  const organization = kind === 'tenants'; const operator = kind === 'operators'; const label = organization ? 'organization' : 'Account';
  const values = operator ? ['No platform role', 'Platform Operator'] : statuses;
  const selectedValue = (row: Row) => operator ? Number(row.isOperator) : row.status;
  const subject = operator ? 'platform role' : 'status';
  const endpoint = '/api/v2/system/' + kind;
  const [filters, setFilters] = useState<Filters>({}); const [filterForm] = Form.useForm<Filters>(); const [page, setPage] = useState(0);
  const query = new URLSearchParams({ page: String(page), pageSize: '30' });
  if (filters.q?.trim()) query.set('q', filters.q.trim()); if (filters.status !== undefined) query.set('status', statuses[filters.status]);
  if (operator && filters.isOperator !== undefined) query.set('isOperator', String(filters.isOperator === 1));
  const state = useIdentityQuery<Page>(endpoint + '?' + query);
  const [selected, setSelected] = useState<Row>(); const [draft, setDraft] = useState<number>(); const [busy, setBusy] = useState(false);
  const [needsReadback, setNeedsReadback] = useState(false); const [readingBack, setReadingBack] = useState(false); const [error, setError] = useState<unknown>();
  const [modal, modalHolder] = Modal.useModal(); const confirmation = useRef<ReturnType<typeof confirmAction> | undefined>(undefined); const owner = useRef(0); const readbackFrom = useRef<Page | undefined>(undefined);
  const layout = useAdminDetailLayout(); const canChange = permissions.includes(operator ? 'system.governance.manage' : organization ? 'system.tenants.manage' : 'system.accounts.manage');
  useEffect(() => () => { owner.current++; confirmation.current?.destroy(); }, []);
  const close = () => { owner.current++; confirmation.current?.destroy(); setSelected(undefined); setDraft(undefined); setError(undefined); setNeedsReadback(false); setReadingBack(false); };
  const guard = useAccessDraftGuard(!!selected && draft !== selectedValue(selected), busy, close);
  useEffect(() => {
    if (state.error) { close(); return; }
    if (readingBack && !state.loading && state.data && state.data !== readbackFrom.current) {
      if (!Array.isArray(state.data.items)) { close(); return; }
      const current = state.data.items.find(row => idOf(row) === (selected && idOf(selected)));
      setReadingBack(false); if (!current) { close(); return; }
      setSelected(current); setNeedsReadback(false); setError(undefined);
    }
  }, [state.error, state.data, state.loading, readingBack]);
  const inspect = (row: Row) => { close(); setSelected(row); setDraft(selectedValue(row)); };
  const save = async (row: Row, status: number, generation: number) => {
    if (generation !== owner.current) return;
    setBusy(true); setError(undefined);
    try {
      await identityRequest(endpoint + '/' + idOf(row) + (operator ? '' : '/status'), { method: 'PUT', body: operator ? { enabled: status === 1, expectedVersion: row.operatorVersion } : organization
        ? { status: statuses[status], expectedRevision: row.revision } : { status: statuses[status], expectedVersion: row.version } });
      if (generation === owner.current) { guard.release(); close(); state.reload(); }
    } catch (reason) {
      if (generation !== owner.current) return;
      if (reason instanceof IdentityError && [401, 403, 404].includes(reason.status)) { close(); state.reload(); }
      else { setError(reason); setNeedsReadback(true); }
    } finally { setBusy(false); }
  };
  const review = () => {
    if (!selected || draft === undefined || draft === selectedValue(selected) || busy || needsReadback || operator && draft === 1 && selected.status !== 0) return;
    const row = selected, status = draft, generation = owner.current;
    confirmation.current = confirmAction(modal, { title: operator ? 'Change platform operator role?' : `Change ${label} status?`, okText: operator ? 'Confirm role change' : 'Confirm status change', danger: operator ? status === 0 : status !== 0,
      content: <Flex vertical gap="var(--ac-space-default)" className="access-row-summary"><Typography.Text strong>{nameOf(row)}</Typography.Text><Typography.Text copyable>{idOf(row)}</Typography.Text>
        <Typography.Text>{values[selectedValue(row)]} → {values[status]}</Typography.Text><Typography.Paragraph>{operator ? 'Platform Operators manage organizations, Accounts and platform governance. This grants no access to organization content. All existing logins for this Account are invalidated; it must sign in again.' : organization
          ? 'Suspension blocks access to this organization. Its members, ownership and retained resources are preserved.'
          : 'This changes sign-in access across all organizations and invalidates existing sessions. Retained records are preserved.'}</Typography.Paragraph></Flex>,
      onOk: () => save(row, status, generation) });
  };
  const apply = (values: Filters) => { close(); setFilters(values); setPage(0); };
  const valid = state.data && Array.isArray(state.data.items) && Number.isInteger(state.data.total) && state.data.total >= state.data.items.length
    && state.data.items.every(row => row && typeof idOf(row) === 'string' && typeof nameOf(row) === 'string'
      && statuses[row.status] !== undefined && typeof row.createdAt === 'string'
      && (organization ? Number.isSafeInteger(row.revision) && row.revision! > 0 : operator ? typeof row.operatorVersion === 'string' && typeof row.isOperator === 'boolean' : typeof row.version === 'string'))
    && state.data.page === page && state.data.pageSize === 30 && state.data.items.length <= 30;
  return <Flex vertical gap="var(--ac-space-section)"><>{modalHolder}</><>{guard.holder}</>
    <Typography.Paragraph type="secondary">{operator ? 'Manage the built-in Platform Operator role on exact Accounts. Platform powers grant no access to organization content.' : organization ? 'Administrative organization metadata. Platform access does not grant access to organization content.' : 'Administrative Account identity metadata. Account management does not grant access to organization content.'}</Typography.Paragraph>
    {organization && permissions.includes('system.tenants.create') && <Flex><CreateOrganization onCreated={state.reload} /></Flex>}
    <Form form={filterForm} layout="vertical" className="access-bounded-form access-collection-filters" onFinish={apply}>
      <Flex gap="var(--ac-space-default)" wrap><Form.Item name="q" label={organization ? 'Organization name or identifier' : 'Account name or email'} extra="Case-sensitive text match."><Input allowClear maxLength={128} /></Form.Item>
        <Form.Item name="status" label="Status"><Select allowClear placeholder="All statuses" options={statuses.map((name, value) => ({ label: name, value }))} /></Form.Item>{operator && <Form.Item name="isOperator" label="Platform role"><Select allowClear placeholder="All roles" options={[{label:'Platform Operator',value:1},{label:'No platform role',value:0}]} /></Form.Item>}</Flex>
      <Flex gap="var(--ac-space-compact)" wrap><Button type="primary" htmlType="submit">Apply filters</Button><Button onClick={() => { filterForm.resetFields(); apply({}); }}>Clear filters</Button></Flex>
    </Form>
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : !valid ? <AccessError error={new Error('Unavailable results')} retry={state.reload} /> : <>
      <Flex justify="space-between" gap="var(--ac-space-compact)"><Typography.Text type="secondary">{state.data!.total} {organization ? state.data!.total === 1 ? 'organization' : 'organizations' : state.data!.total === 1 ? 'Account' : 'Accounts'}</Typography.Text><Button icon={<ReloadOutlined aria-hidden />} onClick={() => { close(); state.reload(); }}>Refresh</Button></Flex>
      <Table<Row> className="admin-collection-table" rowKey={idOf} dataSource={state.data!.items} locale={{emptyText:<Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No records match these filters." />}}
        pagination={{current:page+1,pageSize:30,total:state.data!.total,showSizeChanger:false,responsive:true,onChange:next=>{close();setPage(next-1);}}} columns={[
          {title:organization?'Organization':'Account',render:(_,row)=><Flex vertical align="start" gap="var(--ac-space-tight)"><Button type="link" aria-label={`Inspect ${nameOf(row)}`} onClick={()=>inspect(row)}>{nameOf(row)}</Button><Typography.Text type="secondary">{row.slug ?? row.loginKey?.toLowerCase()}</Typography.Text><Tag>{statuses[row.status] ?? 'Unavailable'}</Tag>{operator && <Tag>{values[selectedValue(row)]}</Tag>}</Flex>}
        ]} />
    </>}
    <Drawer className="access-editor-drawer" title={operator ? 'Platform operator role' : `${organization ? 'Organization' : 'Account'} status`} open={!!selected && !state.error && (state.loading || !!valid)} size="large" styles={{wrapper:{maxWidth:'100vw'}}} closable={!busy} mask={{closable:!busy}} onClose={guard.close} destroyOnHidden>
      {selected && (state.loading ? <AccessLoading /> : <Flex vertical gap="var(--ac-space-section)">
        <Typography.Text strong>{nameOf(selected)}</Typography.Text>
        <Descriptions {...layout} column={1} size="small" items={[{key:'id',label:'Exact ID',children:<Typography.Text copyable>{idOf(selected)}</Typography.Text>},{key:'status',label:'Current status',children:statuses[selected.status]},...(operator ? [{key:'role',label:'Current platform role',children:values[selectedValue(selected)]}] : []),{key:'created',label:'Created',children:new Date(selected.createdAt).toLocaleString()}]} />
        <Typography.Paragraph type="secondary">{operator ? 'Platform Operators manage organizations, Accounts and governance. Role changes invalidate all existing logins of this Account and grant no organization-private access.' : organization ? 'Suspending blocks organization access without deleting resources or changing ownership.' : 'Suspended and Removed Accounts cannot sign in. Status changes invalidate existing sessions across organizations; retained records are preserved.'}</Typography.Paragraph>
        {error !== undefined && <Alert type="warning" showIcon title={`Read current ${subject} before saving again`} description={error instanceof IdentityError && error.reasonCode === 'last_system_operator' ? operator ? 'Keep at least one active platform operator. This role was not changed. Reload the current role before choosing another action.' : 'Keep at least one active platform operator. This Account was not changed. Reload current status before choosing another action.' : 'The change was refused or its result is unconfirmed. Reload the current record, inspect your retained choice, then deliberately review a fresh change.'} />}
        <Form layout="vertical" className="access-bounded-form"><Form.Item label={operator ? "New platform role" : "New status"}><Select aria-label={operator ? "New platform role" : "New status"} value={draft} onChange={setDraft} disabled={!canChange || busy || needsReadback} options={values.slice(0,organization?2:3).map((name,value)=>({label:name,value}))} /></Form.Item></Form>
        {operator && selected.status !== 0 && <Typography.Text type="secondary">An inactive Account cannot receive the Platform Operator role. An existing role may still be revoked.</Typography.Text>}
        {!canChange && <Typography.Text type="secondary">Changing this {subject} requires platform management permission.</Typography.Text>}
        <Flex gap="var(--ac-space-compact)" wrap><Button disabled={busy} onClick={guard.close}>Close</Button><Button disabled={busy} onClick={()=>{readbackFrom.current=state.data;setNeedsReadback(true);setReadingBack(true);state.reload();}}>Reload current {subject}</Button><Button type="primary" disabled={!canChange || busy || needsReadback || draft === undefined || draft === selectedValue(selected) || operator && draft === 1 && selected.status !== 0} loading={busy} onClick={review}>Review {operator ? 'role' : 'status'} change</Button></Flex>
      </Flex>)}
    </Drawer>
  </Flex>;
}
