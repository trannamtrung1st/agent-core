import { useInstanceConfigurationEditor } from './InstanceConfigurationActions';
import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Flex, Form, Spin, Tag, Typography, theme } from 'antd';
import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { AdminErrorNotice } from './adminFailure';
import { describeAdminError, type AdminFailureNotice } from './adminErrors';
import { confirmAction } from '../../app/confirmAction';
import { getSelfManagement, saveSelfManagement, type SelfManagementView } from '../../services/adminApi';

import { SelfManagementFields, inheritedManagement as inherited, managementDraftOf as draftOf, type SelfManagementDraft as Draft } from './SelfManagementFields';

export function SelfManagementSection({ instanceId, archived, onUpdated, onDirtyChange }: { instanceId: string; archived: boolean; onDirtyChange?: (dirty: boolean) => void; onUpdated: () => void }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [view, setView] = useState<SelfManagementView | null>(null); const [draft, setDraft] = useState<Draft>(inherited);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false); const [error, setError] = useState<AdminFailureNotice | null>(null);
  const generation = useRef(0);
  useEffect(() => {
    const request = ++generation.current; setView(null); setDraft(inherited); setLoading(true); setBusy(false); setError(null);
    void getSelfManagement(instanceId).then(next => { if (request === generation.current) { setView(next); setDraft(draftOf(next)); } })
      .catch(e => { if (request === generation.current) setError(describeAdminError(e, 'Unable to load self-management. Retry.')); })
      .finally(() => { if (request === generation.current) setLoading(false); });
    return () => { generation.current++; };
  }, [instanceId]);
  async function reload() {
    const request = generation.current; setLoading(true); setError(null);
    try { const next = await getSelfManagement(instanceId); if (request === generation.current) setView(next); }
    catch (e) { if (request === generation.current) setError(describeAdminError(e, 'Unable to refresh policy. Retry.')); }
    finally { if (request === generation.current) setLoading(false); }
  }
  async function save(frozen?: boolean, expectedRevision?: number) {
    if (!view || archived) return;
    const request = generation.current; setBusy(true); setError(null);
    try {
      const next = await saveSelfManagement(instanceId, { expectedRevision: expectedRevision ?? view.instanceRevision,
        ...(frozen === undefined ? draft : draftOf(view)), ...(frozen !== undefined ? { frozen } : {}) });
      if (request !== generation.current) return;
      setView(next); if (frozen === undefined) setDraft(draftOf(next)); if (expectedRevision === undefined) onUpdated(); return next.instanceRevision;
    } catch (e) { if (request === generation.current) setError(describeAdminError(e, 'Policy could not be saved. Refresh revisions; your draft is retained.')); if (expectedRevision !== undefined) throw e; }
    finally { if (request === generation.current) setBusy(false); }
  }
  const changed = view && JSON.stringify(draft) !== JSON.stringify(draftOf(view));
  useEffect(() => { onDirtyChange?.(!!changed); }, [changed, onDirtyChange]);
  const configuration = useInstanceConfigurationEditor('Self-management', { dirty: !!changed, busy: busy || loading, revision: view?.instanceRevision, blocked: !view ? 'Read current self-management before saving.' : undefined, save: async revision => { const updated = await save(undefined, revision); if (updated === undefined) throw new Error('Self-management could not be saved.'); return updated; }, discard: () => setDraft(view ? draftOf(view) : inherited), refresh: reload });
  const disabled = archived || loading || busy;
  return <AgentConfigurationPanel title="Agent self-management" label="Agent self-management"
    description="Admin can always configure this Instance. Enabled Chat management uses the same owners, within selected areas and exact approvals." bodyGap="section">
    {loading && <Spin aria-label="Loading agent self-management" />}
    {error && <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />}
      action={<Button disabled={busy} onClick={() => void reload()}>Refresh policy</Button>} />}
    {view && <>
      <Flex wrap gap={token.paddingXS}><Tag>{view.effective.frozen ? 'Frozen' : view.effective.mode === 'Disabled' ? 'Off' : view.effective.mode}</Tag>
        <Typography.Text type="secondary">{view.source === 'per-field' ? 'Per-field inheritance' : view.source === 'definition' ? 'Definition defaults' : 'Instance policy'} · policy revision {view.policyRevision} · Instance revision {view.instanceRevision}</Typography.Text></Flex>
      {view.effective.frozen && <Alert type="info" showIcon title="Self-management is frozen" description="Only Admin can explicitly unfreeze. Normal tasks and retained Skills, resources and settings remain available." />}
      <Form layout="vertical" className="admin-config-form">
        <Flex vertical gap={token.padding}>
          <SelfManagementFields value={draft} defaults={view.definitionDefaults} disabled={disabled} onChange={setDraft} />
          <Typography.Text type="secondary">Changes apply to the next new Run in existing Sessions. Revocation and freeze take effect immediately.</Typography.Text>
          <Flex wrap gap={token.paddingXS}>
            {!configuration.managed && <Button type="primary" disabled={disabled || !changed} onClick={() => void save()}>Save self-management</Button>}
            <Button disabled={disabled} onClick={() => setDraft(inherited)}>Reset to Definition defaults</Button>
            {view.effective.frozen ? <Button disabled={disabled} onClick={() => confirmAction(modal, { title: 'Unfreeze self-management?', content: 'This explicitly removes the Instance lock. The selected mode and areas govern the next Run.', okText: 'Unfreeze', onOk: async () => { await save(false); } })}>Unfreeze self-management</Button>
              : view.effective.mode !== 'Disabled' && <Button disabled={disabled} onClick={() => confirmAction(modal, { title: 'Freeze self-management?', content: 'Revoke Chat configuration immediately. Normal tasks and retained content remain available. Only Admin can unfreeze.', okText: 'Freeze self-management', onOk: async () => { await save(true); } })}>Freeze self-management</Button>}
          </Flex>
        </Flex>
      </Form>
    </>}
  </AgentConfigurationPanel>;
}
