import { Button, Flex, Tag, Typography } from 'antd';
import { workflowOrder, nodeKinds, type WorkflowGraph, type WorkflowStep } from '../../services/tenantWorkflows';

export function WorkflowOutline({ graph, selected, onSelect, steps }: { graph: WorkflowGraph; selected?: string; onSelect: (id: string) => void; steps?: WorkflowStep[] }) {
  return <ol className="workflow-outline" aria-label="Workflow steps">
    {workflowOrder(graph).map(node => {
      const step = steps?.find(s => s.nodeId === node.id);
      return <li key={node.id}><Button type={selected === node.id ? 'primary' : 'default'} aria-pressed={selected === node.id} data-workflow-node={node.id} onClick={() => onSelect(node.id)}>
        <span className="workflow-outline-label"><Typography.Text strong>{node.name || 'Unnamed step'}</Typography.Text><span>{nodeKinds[node.kind]}{node.requirement === 1 ? ' · Required' : node.requirement === 2 ? ' · Required on branch' : ''}</span>
          {step && <Tag>{step.status}{step.proven ? ' · Evidence accepted' : ''}</Tag>}
          <Flex vertical gap="var(--ac-space-tight)">{graph.edges.filter(e => e.from === node.id).map(edge => <Typography.Text key={edge.id} type={selected === node.id ? undefined : 'secondary'}>{edge.outcome} → {graph.nodes.find(n => n.id === edge.to)?.name ?? 'Unavailable step'}</Typography.Text>)}</Flex>
        </span>
      </Button></li>;
    })}
  </ol>;
}
