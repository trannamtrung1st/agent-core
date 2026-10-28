import { Table, Typography } from 'antd';
import { nodeKinds, readWorkflowGraph, requirementLabels, type WorkflowDefinition, type WorkflowGraph } from '../../services/tenantWorkflows';

/** Compare every persisted behavioral field; positions are only a local view. */
export function workflowDraftFields(name: string, description: string, graph: WorkflowGraph): Map<string, string> {
  const fields = new Map<string, string>([['Name', name], ['Description', description]]);
  const walk = (path: string, value: unknown) => {
    if (value == null) return;
    if (Array.isArray(value)) { value.forEach((item, index) => walk(`${path} ${index + 1}`, item)); if (!value.length) fields.set(path, 'None'); return; }
    if (typeof value === 'object') { for (const [key, entry] of Object.entries(value)) walk(`${path} · ${key}`, entry); return; }
    fields.set(path, typeof value === 'boolean' ? value ? 'Yes' : 'No' : String(value));
  };
  const { nodes, edges, ...settings } = graph; walk('Process', settings);
  for (const node of nodes) walk(`Step ${node.id}`, { ...node, kind: nodeKinds[node.kind], requirement: requirementLabels[node.requirement] });
  for (const edge of edges) walk(`Connection ${edge.id}`, edge);
  return fields;
}
export function WorkflowDraftComparison({ name, description, graph, saved }: { name: string; description: string; graph: WorkflowGraph; saved: WorkflowDefinition }) {
  const local = workflowDraftFields(name, description, graph); const latest = workflowDraftFields(saved.name, saved.description, readWorkflowGraph(saved.draftJson));
  const differences = [...new Set([...local.keys(), ...latest.keys()])].filter(key => local.get(key) !== latest.get(key)).map(key => ({ key, local: local.get(key) ?? 'Absent', saved: latest.get(key) ?? 'Absent' }));
  return <><Typography.Paragraph>{differences.length} changed fields. Inspect the saved values before deliberately replacing this revision with your local edits.</Typography.Paragraph><Table size="small" rowKey="key" dataSource={differences} pagination={{ pageSize: 10 }} scroll={{ x: 520 }} columns={[
    { title: 'Field', dataIndex: 'key', render: value => <Typography.Text style={{ overflowWrap: 'anywhere' }}>{value}</Typography.Text> },
    { title: 'Your local draft', dataIndex: 'local', render: value => <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{value}</Typography.Text> },
    { title: `Saved revision ${saved.revision}`, dataIndex: 'saved', render: value => <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{value}</Typography.Text> },
  ]} /></>;
}
