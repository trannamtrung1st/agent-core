import { useRef, useState } from 'react';
import { App, Button, Empty, Flex, Table, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { evaluatorLabels, freshTenantRevision, latestCheck, type EvaluationAttempt, type EvaluationCheck, type EvaluationSuite, type EvaluationBinding } from '../../services/tenantEvaluations';
import { IdentityCompletionError, identityRequest, tenantApi, type TenantContext } from '../../services/identity';
import type { TenantDraftState } from '../../services/tenantDefinitions';
import { AccessError } from './AccessFeedback';
import { EvaluationSuiteEditor } from './EvaluationSuiteEditor';
import { EvaluationAttemptInspector } from './EvaluationAttemptInspector';
import { useTenantCommand } from './useTenantCommand';

export function TenantDefinitionEvaluations({ context, snapshot, writable, busy, reload }: { context: TenantContext; snapshot: TenantDraftState; writable: boolean; busy: boolean; reload: () => void }) {
  const { modal, message } = App.useApp(); const [editing, setEditing] = useState(false); const [attempt, setAttempt] = useState<string>(); const command = useTenantCommand(); const requests = useRef(new Map<string, string>());
  const tenant = context.tenant.tenantId; const suite = snapshot.suites.find(s => s.draftId === snapshot.draft.draftId);
  const publicationSuites = snapshot.suites.filter(s => s === suite || (snapshot.draft.candidate.evaluationBindings as EvaluationBinding[] | undefined)?.some(b => b.suiteId === s.suiteId && b.version === s.version && b.policy === 3));
  const canManage = writable && context.permissions.includes('evaluations.manage'); const canRun = writable && context.permissions.includes('evaluations.run') && snapshot.resourceAccess.canEvaluate;
  const run = (selected: EvaluationSuite) => {
    const suite = selected;
    const witness = `${suite.suiteId}:${suite.version}:${snapshot.draft.revision}`;
    const dedupKey = requests.current.get(witness) ?? crypto.randomUUID();
    requests.current.set(witness, dedupKey);
    const invoke = () => command.run(async signal => identityRequest<EvaluationAttempt>(tenantApi(tenant, '/evaluation-attempts'), { method: 'POST', signal,
      body: { subjectKind: 0, subjectId: snapshot.draft.draftId, subjectRevision: snapshot.draft.revision, suiteId: suite.suiteId, suiteVersion: suite.version, hook: 4, dedupKey, expectedRevision: await freshTenantRevision(tenant, signal) } }), result => {
        if (result.tenantId !== tenant || result.subjectId !== snapshot.draft.draftId || result.suiteVersion !== suite.version) throw new IdentityCompletionError(); requests.current.delete(witness); setAttempt(result.attemptId); void message.success('Evaluation status stored.');
      });
    if (suite.checks.some(c => c.kind === 1)) confirmAction(modal, { title: 'Run this evaluation?', content: 'Configured model judges can incur provider charges. The exact saved version and permitted subject snapshot will be used. Native Script execution requires independent authority and may return Inconclusive.', okText: 'Run evaluation', onOk: invoke }); else void invoke();
  };
  return <Flex vertical gap="var(--ac-space-section)">
    <Typography.Paragraph type="secondary">Required checks must pass for the exact saved draft and Suite version. Offline assertions, model judgments and human assessments retain separate evidence and trust.</Typography.Paragraph>
    <Flex wrap gap="var(--ac-space-compact)">{canManage && <Button disabled={busy || command.busy} onClick={() => setEditing(true)}>Edit publication checks</Button>}</Flex>
    {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
    {!snapshot.resourceAccess.canEvaluate && <Typography.Text type="secondary">Running these checks requires current read access to every draft resource and Definition update access.</Typography.Text>}
    {publicationSuites.length ? publicationSuites.map(currentSuite => <section key={`${currentSuite.suiteId}:${currentSuite.version}`}><Flex vertical gap="var(--ac-space-compact)"><Typography.Text strong>{currentSuite.name} · immutable version {currentSuite.version}</Typography.Text>{canRun && <Button type="primary" disabled={busy || command.busy} loading={command.busy} onClick={() => run(currentSuite)}>Run {currentSuite.draftId ? 'evaluation' : currentSuite.name}</Button>}<Table<EvaluationCheck> className="admin-collection-table" rowKey="checkId" dataSource={currentSuite.checks} pagination={{ pageSize: 10, responsive: true, showSizeChanger: false }} columns={[
      { title: 'Checks and evidence', render: (_, check) => { const evidence = latestCheck(snapshot, currentSuite, check); return <Flex vertical gap="var(--ac-space-compact)" align="start"><Typography.Text strong>{check.title}</Typography.Text><Typography.Text type="secondary">{evaluatorLabels[check.kind]} · {check.required ? 'Required' : 'Advisory'}</Typography.Text><Tag color={evidence.label === 'Passed' ? 'success' : evidence.label === 'Failed' ? 'error' : 'warning'}>{evidence.label}</Tag>{evidence.finding && <Typography.Text>{evidence.finding.summary}</Typography.Text>}{evidence.result && <Button onClick={() => setAttempt(evidence.result!.attemptId)}>Inspect evidence</Button>}</Flex>; } }
    ]} /></Flex></section>) : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No publication checks configured." />}
    {editing && <EvaluationSuiteEditor tenant={tenant} existing={suite} draft={snapshot.draft} onClose={() => setEditing(false)} onSaved={() => { setEditing(false); reload(); }} />}
    {attempt && <EvaluationAttemptInspector tenant={tenant} id={attempt} canReview={context.permissions.includes('evaluations.review')} onClose={() => { setAttempt(undefined); reload(); }} />}
  </Flex>;
}
