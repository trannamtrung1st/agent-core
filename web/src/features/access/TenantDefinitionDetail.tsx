import { useState } from 'react';
import { App, Button, Descriptions, Drawer, Empty, Flex, Table, Tag, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { navigateToAppPath } from '../../app/appRoute';
import { confirmAction } from '../../app/confirmAction';
import { identityRequest, tenantApi, type TenantContext } from '../../services/identity';
import { definitionApi, type DefinitionAccess, type TenantDefinitionSummary, type TenantDraft, type TenantPublication, type TenantPublicationSummary } from '../../services/tenantDefinitions';
import { AdminOwnerHeading } from '../admin/AdminShell';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { tenantAccessPath, tenantDraftPath } from './accessRoute';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';
import { TenantPublicationResources } from './TenantDefinitionResources';

export function TenantDefinitionDetail({ context, id }: { context: TenantContext; id: string }) {
  const tenant = context.tenant.tenantId; const { message, modal } = App.useApp(); const command = useTenantCommand();
  const [attempt, setAttempt] = useState(0); const [version, setVersion] = useState<number>();
  const summary = useIdentityQuery<TenantDefinitionSummary>(definitionApi(tenant, id), String(attempt));
  const access = useIdentityQuery<DefinitionAccess>(definitionApi(tenant, id, '/access'), String(attempt));
  const drafts = useIdentityQuery<{ items: TenantDraft[] }>(definitionApi(tenant, id, '/drafts'), String(attempt));
  const versions = useIdentityQuery<{ items: TenantPublicationSummary[] }>(definitionApi(tenant, id, '/publications'), String(attempt));
  const reload = () => { command.clearError(); setAttempt(value => value + 1); };
  const allowed = (action: string) => access.data?.permissions.includes(`agent-definitions.${action}`);
  const error = summary.error ?? access.error ?? drafts.error ?? versions.error;
  if (error) return <AccessError error={error} retry={reload} />;
  if (!summary.data || !access.data || !drafts.data || !versions.data) return <AccessLoading />;
  return <Flex vertical gap="var(--ac-space-section)">
    <Button href={tenantAccessPath(tenant, 'definitions')} onClick={followAppLink} style={{ alignSelf: 'start' }}>Back to Definitions</Button>
    <AdminOwnerHeading name={summary.data.displayName} metadata={<Typography.Text type="secondary">{summary.data.draftCount} drafts · {summary.data.latestVersion ? `Latest publication v${summary.data.latestVersion}` : 'Not published'}</Typography.Text>} extra={<Flex wrap gap="var(--ac-space-compact)">
      <Button disabled={command.busy} onClick={reload}>Reload saved state</Button>
      {allowed('delete') && <Button danger disabled={command.busy} onClick={() => confirmAction(modal, { title: `Delete ${summary.data!.displayName}?`, content: 'This permanently removes the definition and its drafts/publications. Instances and retained conversation history must not reference it.', okText: 'Delete definition', danger: true,
        onOk: () => command.run(signal => identityRequest(definitionApi(tenant, id), { method: 'DELETE', signal, body: { expectedRevision: access.data!.revision, witness: { drafts: drafts.data!.items.map(d => ({ draftId: d.draftId, revision: d.revision })), publications: versions.data!.items.map(p => ({ version: p.version, metadataRevision: p.metadataRevision })) } } }), () => { message.success('Definition deleted.'); navigateToAppPath(tenantAccessPath(tenant, 'definitions')); }) })}>Delete definition</Button>}
    </Flex>} />
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    <Typography.Title level={5}>Drafts</Typography.Title>
    <Table className="admin-collection-table" rowKey="draftId" dataSource={drafts.data.items} pagination={{ pageSize: 10, responsive: true, showSizeChanger: false }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No drafts. Fork a publication below to prepare changes." /> }} columns={[
      { title: 'Draft', render: (_, draft) => <Flex vertical><Typography.Link href={tenantDraftPath(tenant, id, draft.draftId)} onClick={followAppLink}>{String((draft.candidate.identity as { name?: string })?.name ?? 'Draft')}</Typography.Link><Typography.Text type="secondary">Revision {draft.revision} · <time dateTime={draft.updatedAt}>{new Date(draft.updatedAt).toLocaleString()}</time></Typography.Text></Flex> }
    ]} />
    <Typography.Title level={5}>Publications</Typography.Title>
    <Table className="admin-collection-table" rowKey="version" dataSource={versions.data.items} pagination={{ pageSize: 10, responsive: true, showSizeChanger: false }} locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Publish a draft to create an immutable version." /> }} columns={[
      { title: 'Version', render: (_, publication) => <Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>Version {publication.version}</Typography.Text><Tag>{publication.status === 0 ? 'Active' : 'Deprecated'}</Tag><Typography.Text type="secondary"><time dateTime={publication.publishedAt}>{new Date(publication.publishedAt).toLocaleString()}</time></Typography.Text></Flex> },
      { title: 'Actions', render: (_, publication) => <Flex wrap gap="var(--ac-space-compact)">
        <Button onClick={() => setVersion(publication.version)}>Inspect v{publication.version}</Button>
        {access.data?.canFork[publication.version] && <Button disabled={command.busy} onClick={() => void command.run(signal => identityRequest<TenantDraft>(definitionApi(tenant, id, '/drafts'), { method: 'POST', signal, body: { version: publication.version, expectedRevision: access.data!.revision } }), draft => navigateToAppPath(tenantDraftPath(tenant, id, draft.draftId, 'profile')))}>Fork v{publication.version}</Button>}
        {allowed('publish') && publication.status === 0 && <Button disabled={command.busy} onClick={() => confirmAction(modal, { title: `Deprecate version ${publication.version}?`, content: 'Existing history stays intact. This version will no longer be available for new Instances.', okText: 'Deprecate version', onOk: () => command.run(signal => identityRequest(definitionApi(tenant, id, `/publications/${publication.version}/deprecate`), { method: 'POST', signal, body: { expectedRevision: access.data!.revision, expectedMetadataRevision: publication.metadataRevision } }), () => { message.success('Version deprecated.'); reload(); }) })}>Deprecate</Button>}
        {context.permissions.includes('agents.create') && publication.status === 0 && <Button disabled={command.busy} onClick={() => confirmAction(modal, { title: `Create an Instance from version ${publication.version}?`, content: 'The Instance inherits this exact publication. Assign its own roles before expecting it to act.', okText: 'Create Instance', onOk: () => command.run(signal => identityRequest<{ instance: { instanceId: string } }>(tenantApi(tenant, '/agents/from-publication'), { method: 'POST', signal, body: { definitionId: id, version: publication.version, expectedRevision: access.data!.revision } }), () => { message.success('Instance created.'); reload(); }) })}>Create Instance</Button>}
      </Flex> }
    ]} />
    {version !== undefined && <PublicationInspection tenant={tenant} id={id} version={version} onClose={() => setVersion(undefined)} />}
  </Flex>;
}
function PublicationInspection({ tenant, id, version, onClose }: { tenant: string; id: string; version: number; onClose: () => void }) {
  const state = useIdentityQuery<TenantPublication>(definitionApi(tenant, id, `/publications/${version}`)); const layout = useAdminDetailLayout();
  return <Drawer open title={`Publication v${version}`} onClose={onClose} size={720} styles={{ wrapper: { maxWidth: '100vw' } }} footer={<Button onClick={onClose}>Close inspection</Button>}>
    {state.error ? <AccessError error={state.error} retry={state.reload} /> : !state.data ? <AccessLoading /> : <Flex vertical gap="var(--ac-space-section)">
      <Descriptions {...layout} column={1} items={[{ label: 'Status', children: state.data.status === 0 ? 'Active' : 'Deprecated' }, { label: 'Instructions', children: <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{String(state.data.payload.agentInstructions)}</Typography.Paragraph> }]} />
      <Typography.Paragraph type="secondary">Published versions are immutable. Fork a draft to prepare changes.</Typography.Paragraph>
      <TenantPublicationResources tenant={tenant} id={id} version={version} />
      <details><summary>Published configuration JSON</summary><pre className="tenant-definition-json">{JSON.stringify(state.data.payload, null, 2)}</pre></details>
    </Flex>}
  </Drawer>;
}
