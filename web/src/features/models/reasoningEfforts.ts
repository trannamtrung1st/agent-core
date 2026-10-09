/** Semantic intensity; unknown modes must never occupy slider positions. */
export const REASONING_EFFORT_ORDER = ["none", "minimal", "low", "medium", "high", "xhigh", "max"] as const;

export function reasoningEffortChoices(values: readonly string[]): { levels: string[]; modes: string[] } {
  const unique = [...new Set(values)];
  return {
    levels: REASONING_EFFORT_ORDER.filter(value => unique.includes(value)),
    modes: unique.filter(value => !REASONING_EFFORT_ORDER.some(level => level === value))
  };
}

export function orderedReasoningEfforts(values: readonly string[]): string[] {
  const { levels, modes } = reasoningEffortChoices(values);
  return [...levels, ...modes];
}

export function retainedReasoningEffort(model: { supportedReasoningEfforts: readonly string[]; defaultReasoningEffort?: string | null } | undefined, current: string): string {
  return model?.supportedReasoningEfforts.includes(current) ? current : model?.defaultReasoningEffort ?? "";
}
