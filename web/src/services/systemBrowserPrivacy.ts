import { IdentityCompletionError, identityRequest } from './identity';
export const systemBrowserPrivacyPath = '/system/browser-privacy';
export const systemBrowserPrivacyApi = '/api/v2/system/browser/privacy';
export type BrowserPrivacyMode = 'Protected' | 'Unmasked' | 'Disabled';
export type BrowserScreenshotPolicy = { mode: BrowserPrivacyMode; unmaskedOrigins: string[]; trustedGraphicsOrigins: string[]; revision: number };
export type BrowserPrivacy = { saved: BrowserScreenshotPolicy; effective: BrowserScreenshotPolicy;
  deployment: { captureAllowed: boolean; unmaskedAllowed: boolean; unmaskedOriginCeiling: string[]; graphicsOriginCeiling: string[] };
  restartRequired: boolean; activation: string; durable: boolean; constrainedByDeployment: boolean };
export const getBrowserPrivacy = (signal?: AbortSignal) => identityRequest<BrowserPrivacy>(systemBrowserPrivacyApi, { signal });
export async function saveBrowserPrivacy(input: Omit<BrowserScreenshotPolicy, 'revision'> & { expectedRevision: number; acknowledgeExposure: boolean }, signal?: AbortSignal): Promise<BrowserPrivacy> {
  const saved = await identityRequest<BrowserPrivacy>(systemBrowserPrivacyApi, { method: 'PUT', body: input, signal });
  if (!saved?.saved || saved.saved.revision !== input.expectedRevision + 1 || saved.saved.mode !== input.mode
    || !saved.effective || !Number.isSafeInteger(saved.effective.revision) || saved.effective.revision < 0 || saved.effective.revision > saved.saved.revision) throw new IdentityCompletionError();
  return saved;
}
