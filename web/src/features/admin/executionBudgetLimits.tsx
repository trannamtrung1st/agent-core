import { createContext,useContext,type ReactNode } from 'react';
import type { ExecutionBudgetLimits } from '../../services/adminApi';
export type LimitsState = { limits: ExecutionBudgetLimits | null; error: string | null; reload: () => void };
const LimitsContext = createContext<LimitsState | null>(null);
export function useExecutionBudgetLimits(): LimitsState {
  const value = useContext(LimitsContext);
  if (!value) throw new Error('Execution budget controls require scoped authoring metadata.');
  return value;
}
/** The owning tenant page supplies current authorized metadata and its recovery action. */
export function ScopedExecutionBudgetLimits({ children, value }: { children: ReactNode; value: LimitsState }) {
  return <LimitsContext.Provider value={value}>{children}</LimitsContext.Provider>;
}
