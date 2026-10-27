import { describe, expect, it } from 'vitest';
import type { SessionGoal } from '../../services/tenantSessionGoals';
import { requiredPrerequisiteIds } from './goalCheckpoints';
const checkpoint = (checkpointId: string, required: boolean, dependencies: string[] = []) => ({ checkpointId, title: checkpointId, expectedOutcome: 'Reviewed', required, dependencies, verification: [{ kind: 2 }] });
const goal = (requiredStatus = 0): SessionGoal => ({ goalId: 'goal', tenantId: 'tenant', sessionId: 'session', spaceId: 'space', revision: 1, planRevision: 1, status: 1, pendingPlan: null, autoRunsUsed: 0, finalAcceptedBy: null,
  plan: { title: 'Goal', objective: 'Deliver', successCriteria: 'Reviewed', requireFinalAcceptance: true, autoContinuation: { enabled: false, maxRuns: 0, until: null }, checkpoints: [checkpoint('first', false), checkpoint('second', false, ['first']), checkpoint('required', true, ['second']), checkpoint('independent', false)] },
  checkpoints: ['first', 'second', 'required', 'independent'].map(checkpointId => ({ checkpointId, status: checkpointId === 'required' ? requiredStatus : 0, note: null, sourceRunId: null, evidence: null })) });
describe('Goal optional prerequisite controls', () => {
  it('protects direct and transitive prerequisites while leaving independent optional work skippable', () => {
    expect([...requiredPrerequisiteIds(goal())].sort()).toEqual(['first', 'second']);
  });
  it('stops protecting prerequisites of a required outcome that is already verified', () => {
    expect([...requiredPrerequisiteIds(goal(4))]).toEqual([]);
  });
  it('still protects a shared prerequisite needed by another unfinished required outcome', () => {
    const shared = goal(4); shared.plan.checkpoints.push(checkpoint('other-required', true, ['first']));
    shared.checkpoints.push({ checkpointId: 'other-required', status: 0, note: null, sourceRunId: null, evidence: null });
    expect([...requiredPrerequisiteIds(shared)]).toEqual(['first']);
  });
});
