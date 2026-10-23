import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { HttpRequestPreview, savedHttpRequest } from './HttpRequestPreview';
import type { TenantApproval } from './TenantApprovals';
const changes = (): TenantApproval['review']['changes'] => Object.entries({ httpMethod: 'POST', httpUrl: 'https://public.example/submit', httpHeaders: '[{"Name":"Content-Type","Value":"text/plain"}]', httpBody: '<img src=x onerror=alert(1)>試', httpRequestHash: 'a'.repeat(64), httpDisclosure: 'One exact public request; no replay of uncertainty.' }).map(([field, after]) => ({ field, before: null, after, beforeAvailable: false, afterAvailable: true, restoresDefault: false }));
describe('exact HTTP consent', () => {
  it('shows full literal body and headers without executable content or source links', () => {
    render(<HttpRequestPreview proposal={savedHttpRequest(changes())} />);
    expect(screen.getByLabelText('Exact request body')).toHaveValue('<img src=x onerror=alert(1)>試');
    expect(screen.getByLabelText('Exact headers')).toHaveValue('Content-Type: text/plain');
    expect(document.querySelector('img[src]')).toBeNull(); expect(screen.queryByRole('link')).toBeNull();
  });
  it.each(['httpMethod', 'httpUrl', 'httpHeaders', 'httpBody', 'httpRequestHash', 'httpDisclosure'])('refuses missing %s', field => {
    expect(savedHttpRequest(changes().filter(c => c.field !== field))).toBeNull();
  });
  it.each(['http://user:secret@public.example/', 'javascript:alert(1)', 'https://public.example/#unsaved'])('refuses unsafe or unsaved URL %s', after => {
    expect(savedHttpRequest(changes().map(c => c.field === 'httpUrl' ? { ...c, after } : c))).toBeNull();
  });
  it.each(['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE'])('accepts an exact empty-body %s request', method => {
    expect(savedHttpRequest(changes().map(c => c.field === 'httpMethod' ? { ...c, after: method } : c.field === 'httpBody' ? { ...c, after: '' } : c))).not.toBeNull();
  });
  it('refuses duplicate fields and credential headers', () => {
    expect(savedHttpRequest([...changes(), changes()[0]!])).toBeNull();
    expect(savedHttpRequest(changes().map(c => c.field === 'httpHeaders' ? { ...c, after: '[{"Name":"Authorization","Value":"secret"}]' } : c))).toBeNull();
  });
});
