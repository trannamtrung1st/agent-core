import { checkedAuthorizationUrl, credentialPurpose, purposeLabel } from './tenantCredentials';
describe('tenant OAuth navigation', () => {
  it('accepts only the exact Google or same-origin Synthetic consent destinations', () => {
    expect(checkedAuthorizationUrl('/api/v2/integrations/tenant-google/oauth/synthetic?state=opaque')).toContain('/tenant-google/oauth/synthetic?state=opaque');
    expect(checkedAuthorizationUrl('https://accounts.google.com/o/oauth2/v2/auth?state=opaque')).toBe('https://accounts.google.com/o/oauth2/v2/auth?state=opaque');
    for (const value of ['https://evil.test/o/oauth2/v2/auth', '//evil.test/path', 'javascript:alert(1)', 'https://accounts.google.com/other', 'https://person:password@accounts.google.com/o/oauth2/v2/auth', '/api/v2/integrations/google-email/oauth/synthetic', '/api/v2/integrations/tenant-google/oauth/synthetic#unexpected'])
      expect(() => checkedAuthorizationUrl(value)).toThrow('Unexpected authorization destination');
  });
});

it('distinguishes model inference from generic and independent speech/account credentials', () => {
  const credential = { purpose: 'ModelInference' } as import('./adminApi').SystemCredential;
  expect(credentialPurpose(credential)).toBe('ModelInference');
  expect(purposeLabel(credentialPurpose(credential))).toBe('Model inference API key');
});
