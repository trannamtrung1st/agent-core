import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { getExecutionBudgetLimits, type ExecutionBudgetLimits } from '../../services/adminApi';

type LimitsState = { limits: ExecutionBudgetLimits | null; error: string | null; reload: () => void };
const LimitsContext = createContext<LimitsState | null>(null);

export function useExecutionBudgetLimits(): LimitsState {
  const shared = useContext(LimitsContext);
  const [limits, setLimits] = useState<ExecutionBudgetLimits | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (shared) return;
    let current = true;
    setLimits(null); setError(null);
    void Promise.resolve().then(getExecutionBudgetLimits).then(value => {
      if (current) setLimits(value);
    }).catch(() => {
      if (current) setError('Execution limits could not be loaded. Retry before editing or saving budget values.');
    });
    return () => { current = false; };
  }, [shared, attempt]);
  return shared ?? { limits, error, reload: () => setAttempt(value => value + 1) };
}

export function ExecutionBudgetLimitsProvider({ children }: { children: ReactNode }) {
  const value = useExecutionBudgetLimits();
  return <LimitsContext.Provider value={value}>{children}</LimitsContext.Provider>;
}

/** A scoped owner supplies already-authorized host metadata; no global request is issued. */
export function ScopedExecutionBudgetLimits({ children, value }: { children: ReactNode; value: LimitsState }) {
  return <LimitsContext.Provider value={value}>{children}</LimitsContext.Provider>;
}
