import { useEffect, useState } from 'react';
import { Button, Drawer, Flex, Modal, Select, Table, Typography } from 'antd';
import { modelReferenceImpact, modelReferenceKinds, modelReferencesApi, offeringLabel, type ModelAdmin, type ModelReferenceKind,
  type ModelReferencePage, type ModelReferenceTarget } from '../../services/models';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';

export function ModelReferences({ tenant, catalog, target, close }: { tenant: string; catalog: ModelAdmin; target: ModelReferenceTarget; close: () => void }) {
  const [kind, setKind] = useState<ModelReferenceKind>('default'), [page, setPage] = useState(0);
  const state = useIdentityQuery<ModelReferencePage>(modelReferencesApi(tenant, target, kind, page));
  return <Drawer open title={`Model references: ${target.label}`} className="access-editor-drawer" size="large"
    styles={{ wrapper: { maxWidth: '100vw' } }} onClose={close} destroyOnHidden footer={<Flex wrap justify="end" gap="var(--ac-space-compact)"><Button onClick={close}>Close</Button></Flex>}>
    <Flex vertical gap="var(--ac-space-section)">
      <Typography.Paragraph>References are limited to resources you can read. Counts exclude unreadable resources. Saved approvals and historical pins remain retained; this view changes no configuration.</Typography.Paragraph>
      <Select aria-label="Model reference category" value={kind} options={modelReferenceKinds.map(item => ({ ...item }))} onChange={next => { setKind(next); setPage(0); }} />
      {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : state.data && <>
        <Typography.Text type="secondary">{state.data.total} readable references · security revision {state.data.securityRevision}</Typography.Text>
        <Table className="admin-collection-table" rowKey={row => `${row.resourceId}:${row.referenceId}:${row.offeringId}`} dataSource={state.data.items}
          scroll={{ x: 620 }} pagination={{ current: page + 1, pageSize: 20, total: state.data.total, showSizeChanger: false, responsive: true, onChange: next => setPage(next - 1) }}
          locale={{ emptyText: 'No readable references in this category.' }} columns={[
            { title: 'Resource', key: 'resource', width: 270, render: (_, row) => <Flex vertical gap="var(--ac-space-tight)"><Typography.Text>{row.label}</Typography.Text><Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>{row.resourceId} · {row.referenceId}</Typography.Text></Flex> },
            { title: 'Use', key: 'use', width: 210, render: (_, row) => <Flex vertical gap="var(--ac-space-tight)"><Typography.Text>{row.usage}</Typography.Text><Typography.Text type="secondary">{row.state} · revision {row.revision}</Typography.Text></Flex> },
            { title: 'Model', key: 'model', render: (_, row) => { const offering = catalog.offerings.find(o => o.offeringId === row.offeringId); return offering ? offeringLabel(offering, catalog) : row.offeringId; } }
          ]} />
      </>}
    </Flex>
  </Drawer>;
}

export function ModelImpactConfirmation({ tenant, target, revision, changes, busy, close, confirm, failed }: {
  tenant: string; target: ModelReferenceTarget; revision: number; changes: string[]; busy: boolean; close: () => void; confirm: () => void; failed: (reason: unknown) => void;
}) {
  const [preview, setPreview] = useState<Awaited<ReturnType<typeof modelReferenceImpact>>>();
  const [error, setError] = useState<unknown>();
  useEffect(() => {
    const controller = new AbortController();
    void modelReferenceImpact(tenant, target, revision, controller.signal).then(result => { if (!controller.signal.aborted) setPreview(result); })
      .catch(reason => { if (!controller.signal.aborted) { setError(reason); failed(reason); } });
    return () => controller.abort();
  }, [tenant, target.kind, target.id, revision]);
  return <Modal open title="Apply model configuration change?" className="model-impact-dialog" centered onCancel={close} mask={{ closable: !busy }} keyboard={!busy}
    closable={!busy} okText="Apply configuration change" cancelText="Keep editing" onOk={confirm}
    confirmLoading={busy} okButtonProps={{ disabled: !preview || !!error }} cancelButtonProps={{ disabled: busy }}>
    <Flex vertical gap="var(--ac-space-default)" style={{ overflowWrap: 'anywhere' }}>
      <Typography.Text strong style={{ overflowWrap: 'anywhere' }}>{target.label}</Typography.Text>
      {changes.map(change => <Typography.Text key={change}>{change}</Typography.Text>)}
      <Typography.Paragraph>New work uses current configuration. Disabling or changing a route can stop later generations of admitted work. Stored assignments, approvals and historical pins are retained; no alternative model is selected automatically.</Typography.Paragraph>
      {error !== undefined ? <AccessError error={error} /> : !preview ? <AccessLoading /> : <>
        <Typography.Text strong>Current readable references</Typography.Text>
        {preview.map(row => <Typography.Text key={row.value}>{row.label}: {row.page.total}</Typography.Text>)}
        <Typography.Text type="secondary">Counts exclude resources you cannot read. Inherited defaults can affect additional work. Current permissions and revisions are checked again when saving.</Typography.Text>
      </>}
    </Flex>
  </Modal>;
}
