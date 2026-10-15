import { afterEach, expect, it, vi } from 'vitest';
import { getSandboxExecution, getSessionSandbox, sandboxImageRegistry } from './sandboxes';
import { ownerFetch } from './api';

vi.mock('./api', () => ({ ownerFetch: vi.fn() }));
afterEach(() => { vi.mocked(ownerFetch).mockReset(); });
it.each([
  ['ubuntu:24.04', 'docker.io'], ['acme/tool:latest', 'docker.io'],
  ['my.image:v1', 'docker.io'], ['my.image@sha256:'+'a'.repeat(64), 'docker.io'],
  ['mcr.microsoft.com/dotnet/sdk:10.0', 'mcr.microsoft.com'],
  ['localhost:5000/acme/tool:v1', 'localhost:5000'], ['', null], ['sha256:'+'a'.repeat(64), null],
])('matches registry credential choices to normalized image host %s', (input, host) => {
  expect(sandboxImageRegistry(input!)).toBe(host);
});
function respond(body: string | null, status = 200) {
  vi.mocked(ownerFetch).mockResolvedValue(new Response(body, { status }));
}

it.each(['', 'null'])('treats a successful unprovisioned response (%j) as no environment', async body => {
  respond(body);
  await expect(getSessionSandbox('s1')).resolves.toBeNull();
});
it('reads an existing environment without changing its status', async () => {
  const environment = { sessionId: 's1', generation: 'g1', state: 'Ready' };
  respond(JSON.stringify(environment));
  await expect(getSessionSandbox('s1')).resolves.toEqual(environment);
});
it('does not treat an unsuccessful empty response as no environment', async () => {
  respond('', 503);
  await expect(getSessionSandbox('s1')).rejects.toBeTruthy();
});
it('does not hide a malformed successful response', async () => {
  respond('{');
  await expect(getSessionSandbox('s1')).rejects.toThrow();
});
it('still requires JSON for a command receipt', async () => {
  respond('');
  await expect(getSandboxExecution('s1', 'e1')).rejects.toThrow();
});
