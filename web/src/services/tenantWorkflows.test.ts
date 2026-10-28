import { describe, expect, it } from 'vitest';
import { connectWorkflow, initialWorkflow, outcomesFor, readWorkflowGraph, workflowOrder } from './tenantWorkflows';

describe('Workflow authoring model', () => {
  it('replaces only the selected outcome, retaining stable branch evidence identity', () => {
    const graph = initialWorkflow('reviewer'); const original = graph.edges.find(e => e.outcome === 'approve')!;
    const changed = connectWorkflow(graph, 'review', 'approve', 'failed');
    expect(changed.edges.find(e => e.outcome === 'approve')).toEqual({ ...original, to: 'failed' });
    expect(changed.edges.find(e => e.outcome === 'reject')).toEqual(graph.edges.find(e => e.outcome === 'reject'));
    expect(graph.edges.find(e => e.outcome === 'approve')?.to).toBe('done');
  });
  it('keeps detached and cyclic draft steps editable with a finite outline', () => {
    const graph = initialWorkflow('reviewer'); graph.nodes.push({ id: 'detached', kind: 4, name: 'Detached', requirement: 0 });
    graph.edges.push({ id: 'cycle', from: 'done', to: 'start', outcome: 'next' });
    expect(new Set(workflowOrder(graph).map(n => n.id))).toEqual(new Set(graph.nodes.map(n => n.id)));
    expect(() => readWorkflowGraph('{"schemaVersion":99,"nodes":[],"edges":[]}')).toThrow();
    expect(outcomesFor(4)).toEqual(['true', 'false']);
  });
});
