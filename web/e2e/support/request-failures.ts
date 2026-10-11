import type { Request } from '@playwright/test';

// The picker aborts its read-only poll when its Chat/attachment is replaced.
// Never suppress actual transport errors, decision writes or unrelated reads.
export function isCanceledEmailAccountPoll(request: Pick<Request, 'method' | 'url' | 'failure'>): boolean {
  return request.method() === 'GET'
    && /\/api\/v2\/sessions\/[0-9a-f-]{36}\/email-account-request$/.test(request.url())
    && request.failure()?.errorText === 'net::ERR_ABORTED';
}
