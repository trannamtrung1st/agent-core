import { useMemo, useState, type CSSProperties } from 'react';
import { Flex, Tag, Typography, theme } from 'antd';
import { BranchesOutlined, CheckCircleOutlined, CloseCircleOutlined, PlayCircleOutlined, RobotOutlined, SafetyCertificateOutlined, UserOutlined } from '@ant-design/icons';
import { Background, Controls, Handle, MarkerType, Position, ReactFlow, type Node, type NodeProps } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { nodeKinds, outcomesFor, workflowOrder, type WorkflowGraph, type WorkflowStep } from '../../services/tenantWorkflows';

type StepNode = Node<{ name: string; kind: number; required: string; status?: string; selected: boolean }, 'step'>;
const icons = [PlayCircleOutlined, RobotOutlined, SafetyCertificateOutlined, UserOutlined, BranchesOutlined, CheckCircleOutlined, CloseCircleOutlined];
function DiagramStep({ data, isConnectable }: NodeProps<StepNode>) {
  const Icon = icons[data.kind] ?? SafetyCertificateOutlined;
  return <div className={`workflow-diagram-step${data.selected ? ' workflow-diagram-step-selected' : ''}`}>
    {data.kind !== 0 && <Handle type="target" position={Position.Left} isConnectable={isConnectable} />}
    <Flex vertical gap="var(--ac-space-compact)">
      <Flex align="center" gap="var(--ac-space-compact)"><Icon aria-hidden /><Typography.Text type="secondary">{nodeKinds[data.kind]}</Typography.Text></Flex>
      <Typography.Text strong>{data.name || 'Unnamed step'}</Typography.Text>
      <Flex wrap gap="var(--ac-space-tight)">{data.required && <Tag>{data.required}</Tag>}{data.status && <Tag>{data.status}</Tag>}</Flex>
    </Flex>
    {outcomesFor(data.kind).map((outcome, index, all) => <Handle key={outcome} type="source" id={outcome} position={Position.Right} isConnectable={isConnectable}
      aria-label={`${data.name}: ${outcome}`} style={{ top: `${(index + 1) * 100 / (all.length + 1)}%` }} />)}
  </div>;
}
const nodeTypes = { step: DiagramStep };
export function WorkflowDiagram({ graph, selected, onSelect, onConnect, steps }: {
  graph: WorkflowGraph; selected?: string; onSelect: (id: string) => void; onConnect?: (from: string, outcome: string, to: string) => void; steps?: WorkflowStep[];
}) {
  const { token } = theme.useToken(); const [positions, setPositions] = useState<Record<string, { x: number; y: number }>>({});
  const nodes = useMemo(() => {
    const depth = new Map<string, number>(); const rows = new Map<number, number>();
    return workflowOrder(graph).map(node => {
      const predecessors = graph.edges.filter(e => e.to === node.id).map(e => depth.get(e.from)).filter(d => d !== undefined);
      const level = Math.min(12, predecessors.length ? Math.max(...predecessors) + 1 : 0); depth.set(node.id, level);
      const row = rows.get(level) ?? 0; rows.set(level, row + 1); const step = steps?.find(s => s.nodeId === node.id);
      return { id: node.id, type: 'step' as const, position: positions[node.id] ?? { x: level * 300, y: row * 176 },
        data: { name: node.name, kind: node.kind, required: node.requirement === 1 ? 'Required' : node.requirement === 2 ? 'Required on branch' : '', status: step?.status, selected: node.id === selected },
        selected: node.id === selected, ariaLabel: `${node.name}. ${nodeKinds[node.kind]}. ${node.requirement ? 'Required evidence.' : ''} ${step?.status ?? ''}`, width: 224 };
    });
  }, [graph, positions, selected, steps]);
  const edges = useMemo(() => graph.edges.map(edge => ({ id: edge.id, source: edge.from, sourceHandle: edge.outcome, target: edge.to, label: edge.outcome,
    markerEnd: { type: MarkerType.ArrowClosed }, ariaLabel: `${graph.nodes.find(n => n.id === edge.from)?.name ?? edge.from} to ${graph.nodes.find(n => n.id === edge.to)?.name ?? edge.to}: ${edge.outcome}`,
    animated: false, style: { stroke: token.colorTextSecondary }, labelStyle: { fill: token.colorText }, labelBgStyle: { fill: token.colorBgContainer } })), [graph, token]);
  return <div className="workflow-diagram" aria-label="Workflow diagram" style={{ '--xy-background-color': token.colorBgContainer,
    '--xy-edge-stroke': token.colorTextSecondary, '--xy-controls-button-background-color': token.colorBgElevated,
    '--xy-controls-button-color': token.colorText, '--xy-controls-button-border-color': token.colorBorder } as CSSProperties}>
    <ReactFlow<StepNode> nodes={nodes} edges={edges} nodeTypes={nodeTypes} colorMode="dark" fitView minZoom={0.2} maxZoom={1.5}
      nodesConnectable={!!onConnect} nodesDraggable elementsSelectable onNodeClick={(_, node) => onSelect(node.id)}
      onNodesChange={changes => {
        const moved = changes.filter(change => change.type === 'position' && change.position);
        if (moved.length) setPositions(prior => {
          const next = { ...prior }; let changed = false;
          for (const change of moved) if (change.type === 'position' && change.position && (prior[change.id]?.x !== change.position.x || prior[change.id]?.y !== change.position.y)) { next[change.id] = change.position; changed = true; }
          return changed ? next : prior;
        });
        for (const change of changes) if (change.type === 'select' && change.selected) onSelect(change.id);
      }}
      onNodeDragStop={(_, node) => setPositions(prior => ({ ...prior, [node.id]: node.position }))}
      onConnect={connection => { if (connection.source && connection.sourceHandle && connection.target) onConnect?.(connection.source, connection.sourceHandle, connection.target); }}
      deleteKeyCode={null} ariaLabelConfig={{ 'node.a11yDescription.default': 'Press Enter to select. The Steps outline provides all editing controls.',
        'controls.fitView.ariaLabel': 'Fit entire workflow' }}>
      <Background color={token.colorBorderSecondary} gap={24} size={1} /><Controls showInteractive={false} />
    </ReactFlow>
  </div>;
}
