import { describe, expect, it } from 'vitest';
import { isCanceledEmailAccountPoll } from '../../e2e/support/request-failures';

const path = 'http://localhost/api/v2/sessions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/email-account-request';
const request = (method = 'GET', url = path, errorText = 'net::ERR_ABORTED') => ({ method: () => method, url: () => url, failure: () => ({ errorText }) });
describe('expected owner picker poll cancellation', () => {
  it('recognizes only a native abort of the read-only picker route', () => {
    expect(isCanceledEmailAccountPoll(request())).toBe(true);
    expect(isCanceledEmailAccountPoll(request('GET', path, 'net::ERR_CONNECTION_REFUSED'))).toBe(false);
    expect(isCanceledEmailAccountPoll(request('GET', path, 'net::ERR_CONNECTION_RESET'))).toBe(false);
    expect(isCanceledEmailAccountPoll(request('POST', path + '/decision'))).toBe(false);
    expect(isCanceledEmailAccountPoll(request('POST'))).toBe(false);
    expect(isCanceledEmailAccountPoll(request('GET', path + '/other'))).toBe(false);
    expect(isCanceledEmailAccountPoll(request('GET', 'http://localhost/api/v2/admin/credentials'))).toBe(false);
  });
});
