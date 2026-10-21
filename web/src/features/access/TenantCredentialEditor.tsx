import { Alert,App,Flex,Form,Modal,Select,Typography } from 'antd';
import { useLayoutEffect,useRef,useState } from 'react';
import type { SystemCredential } from '../../services/adminApi';
import { IdentityError,identityRequest } from '../../services/identity';
import { credentialApi,credentialPurpose,freshCredentialAccess,freshTenantRevision,purposeLabel,type CredentialPurpose,type TenantCredential } from '../../services/tenantCredentials';
import { ContainerRegistryFields,type RegistryCredentialFieldsValue } from '../admin/ContainerRegistryFields';
import { CredentialFields,ProtectedValueField,safeFields,type CredentialFieldsValue } from '../admin/CredentialFields';
import { DriveAccountFields } from '../admin/DriveAccountFields';
import { CustomEmailAccountFields,custom,type CustomEmailAccountFieldsValue } from '../admin/CustomEmailAccountFields';
import { showAdminFormValidation } from '../admin/adminFormFeedback';
import { AccessError } from './AccessFeedback';
import { useAccessDraftGuard } from './useAccessDraftGuard';
import { useTenantCommand } from './useTenantCommand';

type Fields = CredentialFieldsValue & CustomEmailAccountFieldsValue & RegistryCredentialFieldsValue;
const purposes: CredentialPurpose[] = ['General', 'Gmail', 'Drive', 'Custom', 'Registry'];
function initial(item?: SystemCredential) {
  return { displayName: item?.displayName ?? '', kind: item?.kind ?? 'Password', status: item?.status ?? 'Active',
    metadata: Object.entries(item?.metadata ?? {}).map(([key, value]) => ({ key, value })), origins: item?.allowedOrigins.join('\n') ?? '',
    profile: item?.emailAccount ?? structuredClone(custom), registry: item?.containerRegistry?.registry ?? '', username: item?.containerRegistry?.username ?? '',
    repositories: item?.containerRegistry?.repositoryPrefixes.join('\n') ?? '', expiresAtUtc: item?.containerRegistry?.expiresAtUtc ?? null };
}
function profile(values: Fields) {
  const p = values.profile;
  return { preset: 'Custom', emailAddress: p.emailAddress, imapUsername: p.imapUsername, smtpUsername: p.smtpUsername, imapEndpoint: p.imapEndpoint, smtpEndpoint: p.smtpEndpoint };
}
export function TenantCredentialEditor({ tenant, organization, account, replace = false, onClose, onSaved }: {
  tenant: string; organization: string; account?: SystemCredential; replace?: boolean; onClose: () => void; onSaved: (item: TenantCredential) => void;
}) {
  const { message } = App.useApp(); const command = useTenantCommand(); const [form] = Form.useForm<Fields>();
  const [purpose, setPurpose] = useState<CredentialPurpose>(account ? credentialPurpose(account) : 'General');
  const [dirty, setDirty] = useState(false); const [open, setOpen] = useState(true); const saved = useRef<TenantCredential | undefined>(undefined);
  const purposeName = useRef('');
  useLayoutEffect(() => {
    if (!account) form.resetFields();
  }, [purpose, account, form]);
  const guard = useAccessDraftGuard(dirty, command.busy, () => setOpen(false));
  const oauth = purpose === 'Gmail' || purpose === 'Drive';
  const title = replace ? 'Replace protected value' : account ? 'Edit credential' : 'Add credential';
  return <>{guard.holder}<Modal open={open} title={title} aria-label={title} className="admin-credential-dialog access-editor-drawer" centered width={640}
    onCancel={guard.close} afterClose={() => saved.current ? onSaved(saved.current) : onClose()} keyboard={!command.busy} closable={!command.busy} mask={{ closable: false }}
    onOk={() => form.submit()} confirmLoading={command.busy} cancelButtonProps={{ disabled: command.busy }} okText={replace ? 'Replace value' : 'Save credential'} okButtonProps={{ disabled: command.requiresReload, 'aria-label': replace ? 'Replace value' : 'Save credential' }} destroyOnHidden>
    <Flex vertical gap="var(--ac-space-section)" className="access-bounded-form">
      <Typography.Paragraph type="secondary">{account ? 'This credential belongs to' : 'Create this credential in'} {organization}.</Typography.Paragraph>
      {command.error !== undefined && <AccessError error={command.error} />}
      {command.error instanceof IdentityError && command.error.status === 409 && <Typography.Paragraph>Your draft is retained. Copy any edits you need, then cancel and reload the credential before saving again.</Typography.Paragraph>}
      {!account && <div><label htmlFor="tenant-credential-purpose">Credential purpose</label><Select id="tenant-credential-purpose" aria-label="Credential purpose" style={{ width: '100%' }} value={purpose} disabled={command.busy}
        options={purposes.map(value => ({ value, label: purposeLabel(value) }))} onChange={next => { purposeName.current = form.getFieldValue('displayName') ?? ''; setPurpose(next); setDirty(true); }} /></div>}
      {!replace && oauth && <Alert type="info" showIcon title="Save first, then connect Google" description="The saved credential keeps its own identity. Connecting and assigning agent permissions are separate actions." />}
      {(replace || !account && !oauth) && <Typography.Text type="secondary">Protected input is cleared after each submission. Enter it again if a save fails.</Typography.Text>}
      <Form form={form} layout="vertical" className="admin-config-form" initialValues={account ? initial(account) : { ...initial(), displayName: purposeName.current }} autoComplete="off" preserve={false} disabled={command.busy}
        onValuesChange={() => setDirty(true)} onFinishFailed={failure => showAdminFormValidation(message, form, failure)} onFinish={values => void command.run(async signal => {
          try {
            if (replace && account) {
              const access = await freshCredentialAccess(tenant, account.credentialId, signal);
              return await identityRequest<TenantCredential>(credentialApi(tenant, account.credentialId, '/value'), { method: 'PUT', signal,
                body: { protectedValue: values.protectedValue, expectedRevision: access.revision, expectedCredentialRevision: account.revision } });
            }
            const registry = purpose === 'Registry' ? { registry: values.registry, username: values.username, repositoryPrefixes: values.repositories.split(/\r?\n/).map(v => v.trim()).filter(Boolean), expiresAtUtc: values.expiresAtUtc ?? null } : undefined;
            const safe = purpose === 'General' ? safeFields(values) : { displayName: values.displayName, status: values.status, metadata: account?.metadata ?? {}, allowedOrigins: [] };
            const input = { ...safe, ...(purpose === 'Custom' ? { emailAccount: profile(values) } : {}), ...(registry ? { containerRegistry: registry } : {}) };
            if (account) {
              const access = await freshCredentialAccess(tenant, account.credentialId, signal);
              return await identityRequest<TenantCredential>(credentialApi(tenant, account.credentialId), { method: 'PATCH', signal,
                body: { ...input, expectedRevision: access.revision, expectedCredentialRevision: account.revision } });
            }
            return await identityRequest<TenantCredential>(credentialApi(tenant), { method: 'POST', signal, body: { ...input,
              kind: oauth ? 'Token' : purpose === 'Custom' ? 'Password' : values.kind,
              purpose: purpose === 'Gmail' || purpose === 'Custom' ? 'EmailAccount' : purpose === 'Drive' ? 'GoogleDriveAccount' : purpose === 'Registry' ? 'ContainerRegistry' : 'General',
              ...(purpose === 'Gmail' ? { emailAccount: { preset: 'Gmail', emailAddress: '' } } : {}), ...(!oauth ? { protectedValue: values.protectedValue } : {}),
              expectedRevision: await freshTenantRevision(tenant, signal) } });
          } finally { form.setFieldValue('protectedValue', undefined); }
        }, result => { saved.current = result; guard.release(); setDirty(false); form.resetFields(); setOpen(false); message.success(replace ? 'Protected value replaced.' : 'Credential saved.'); })}>
        {replace ? <ProtectedValueField email={purpose === 'Custom'} /> : purpose === 'General' ? <CredentialFields create={!account} />
          : purpose === 'Registry' ? <ContainerRegistryFields edit={!!account} /> : purpose === 'Custom' ? <CustomEmailAccountFields account={account} />
            : <DriveAccountFields account={account} />}
      </Form>
    </Flex>
  </Modal></>;
}
