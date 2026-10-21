export type SandboxProfile = { id: string; revision: number; name: string; description: string; enabled: boolean; imageReference: string;
  initializationScript: string | null; cpu: number; memoryMiB: number; pids: number; maxCommandSeconds: number; idleTtlSeconds: number;
  maxLifetimeSeconds: number; storageBytes: number; lastVerifiedAt: string | null; verificationReason: string | null; verification?:SandboxVerification|null; registryCredentialId?:string|null; availability?:string; availabilityMessage?:string|null };
export type SandboxPlatform = { state: string; message: string; profiles: SandboxProfile[]; allowedImagePrefixes: string[];
  localImageIdsAllowed: boolean; securityScope: string; storageScope: string; maxWorkingStorageBytes: number };
export type SandboxEnvironment = { sessionId: string; generation: string; state: string; profileId: string; profileRevision: number; expiresAt: string; activeExecutionId: string | null; diagnosticId: string | null; lastExecutionId: string | null; message?: string | null; createdAt?: string | null; lastActivityAt?: string | null; maximumLifetimeAt?: string | null;
  cleanupReason?: string | null; cleanupPending?: boolean; homeAccess?: 'ReadWrite' | 'ReadOnly' | 'Disabled' | null; storageEnforcement?: string; cleanupPasses?: number; lastCleanupAttemptAt?: string | null; initialization?: SandboxInitialization | null; profileName?:string|null;imageReference?:string|null; selectedProfileId?:string|null; transitionProfileId?:string|null; transitionPhase?:string|null };
export type SandboxExecution = { executionId: string; status: string; exitCode: number | null; stdout: string; stderr: string;
  truncated: boolean; durationMs: number; filesystemEffectsMayHaveOccurred: boolean; diagnosticId: string | null; background: boolean; commandText: string; profileId?:string|null;profileRevision?:number|null;generation?:string|null;phase?:string|null;failureCategory?:string|null;cancellationReason?:string|null;terminationConfirmed?:boolean; generationRecycled?:boolean; remoteTerminationConfirmed?:boolean };
export const sandboxSourceLabel = (p:SandboxProfile) => p.imageReference.startsWith('sha256:') ? 'Local image' : 'Remote OCI';
export const sandboxImageRegistry = (value: string) => {
  const image = value.trim();
  if (!image || image.startsWith('sha256:')) return null;
  const first = image.split('/')[0];
  return image.includes('/') && (first.includes('.') || first.includes(':') || first === 'localhost') ? first : 'docker.io';
};
export const sandboxStatusLabel = (p:Pick<SandboxProfile,'availability'> & Partial<Pick<SandboxProfile,'enabled'>>) => ({Eligible:'Verified · eligible', NeedsVerification:'Needs verification', VerificationFailed:'Verification failed', VerificationPending:'Verification or cleanup pending', SourceUnavailable:'Source unavailable', Forbidden:'Forbidden by host', ServerUnavailable:'Server unavailable', Disabled:'Disabled'}[p.availability??''] ?? (p.enabled===false?'Disabled':'Needs verification'));
export const sandboxEligible = (p:Pick<SandboxProfile,'availability'>) => p.availability==='Eligible';

export type SandboxInitialization = { state:string; profileId:string; profileRevision:number; instanceRevision:number|null; startedAt:string|null; finishedAt:string|null; exitCode:number|null; stdout:string; stderr:string; truncated:boolean; durationMs:number; diagnosticId:string|null; phase:string|null };
export type SandboxVerification = { passed:boolean; message:string; profileId:string; profileRevision:number; instanceRevision:number|null; verifiedAt:string; exitCode:number|null; stdout:string; stderr:string; truncated:boolean; durationMs:number; diagnosticId:string|null };
