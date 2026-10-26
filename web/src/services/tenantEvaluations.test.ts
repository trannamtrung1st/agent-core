import { describe, expect, it } from 'vitest';
import { latestCheck, type EvaluationAttempt, type EvaluationCheck, type EvaluationSuite } from './tenantEvaluations';
const check = { checkId: 'required', required: true } as EvaluationCheck;
const suite = { suiteId: 'suite', version: 2, contentHash: 'exact' } as EvaluationSuite;
const attempt = (overrides: Partial<EvaluationAttempt>): EvaluationAttempt => ({ attemptId: 'a', suiteId: 'suite', suiteVersion: 2, suiteHash: 'exact', subjectRevision: 3, commitSequence: 8, status: 2, findings: [{ checkId: 'required', outcome: 0 }], ...overrides }) as EvaluationAttempt;
const inspect = (attempts: EvaluationAttempt[]) => latestCheck({ draft: { revision: 3 }, attempts }, suite, check);
describe('publication evidence selection', () => {
  it('uses trusted commit order over clocks and stale green evidence', () => {
    expect(inspect([attempt({ commitSequence: 8 }), attempt({ commitSequence: 9, subjectRevision: 2 })]).label).toBe('Stale — run again');
    expect(inspect([attempt({ commitSequence: 0 }), attempt({ commitSequence: null })]).label).toBe('Not run');
  });
  it('never revives an older pass after cancelled or partial evidence', () => {
    expect(inspect([attempt({}), attempt({ commitSequence: 9, status: 3, findings: [{ checkId: 'required', outcome: 2 }] as EvaluationAttempt['findings'] })]).label).toBe('Inconclusive');
    expect(inspect([attempt({}), attempt({ commitSequence: 9, status: 4 })]).label).toBe('Inconclusive');
  });
  it('separates pending advisory review from a completed required finding', () => {
    expect(inspect([attempt({ status: 0 })]).label).toBe('Passed');
    expect(inspect([attempt({ suiteVersion: 1 })]).label).toBe('Stale — run again');
  });
});
