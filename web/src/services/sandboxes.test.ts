import { expect, it } from 'vitest';
import { sandboxImageRegistry } from './sandboxes';
it.each([
  ['ubuntu:24.04', 'docker.io'], ['acme/tool:latest', 'docker.io'],
  ['my.image:v1', 'docker.io'], ['my.image@sha256:'+'a'.repeat(64), 'docker.io'],
  ['mcr.microsoft.com/dotnet/sdk:10.0', 'mcr.microsoft.com'],
  ['localhost:5000/acme/tool:v1', 'localhost:5000'], ['', null], ['sha256:'+'a'.repeat(64), null],
])('matches registry credential choices to normalized image host %s', (input, host) => {
  expect(sandboxImageRegistry(input!)).toBe(host);
});
