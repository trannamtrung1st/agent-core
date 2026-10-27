import type { SessionGoal } from '../../services/tenantSessionGoals';

/** Optional work in this transitive set is needed by an unfinished required outcome. */
export function requiredPrerequisiteIds(goal: SessionGoal): Set<string> {
  const plans = new Map(goal.plan.checkpoints.map(checkpoint => [checkpoint.checkpointId, checkpoint]));
  const states = new Map(goal.checkpoints.map(checkpoint => [checkpoint.checkpointId, checkpoint.status]));
  const needed = new Set<string>();
  function visit(id: string) {
    for (const dependency of plans.get(id)?.dependencies ?? []) {
      if (needed.has(dependency)) continue;
      needed.add(dependency); visit(dependency);
    }
  }
  for (const checkpoint of goal.plan.checkpoints) {
    if (checkpoint.required && states.get(checkpoint.checkpointId) !== 4) visit(checkpoint.checkpointId);
  }
  return needed;
}
