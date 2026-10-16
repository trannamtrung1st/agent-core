export default async function canonicalHost() {
  const api = new URL(process.env.CANONICAL_SYNTHETIC_API_URL!);
  const web = new URL(process.env.CANONICAL_SYNTHETIC_URL!);
  for (const url of [api, web]) {
    if (url.protocol !== 'http:' || !['127.0.0.1', 'localhost'].includes(url.hostname) || url.username || url.password)
      throw new Error('Canonical cutover tests require explicit disposable loopback hosts.');
  }
  const response = await fetch(new URL('/health', api), { signal: AbortSignal.timeout(5000) });
  if (!response.ok || (await response.json()).profile !== 'Synthetic')
    throw new Error('Refusing canonical workspace tests against an unavailable, unknown or Real host.');
}
