import type { ModelDescriptor } from "../../services/api";
import type { PendingAttachment } from "../../services/attachments";
import type { PendingSendItem } from "../../state/sessionStore";
import { INHERIT_MODEL } from "./ModelPicker";

export const IMAGE_MODEL_INCOMPATIBLE_MESSAGE =
  "This model cannot read images. Choose a vision-capable model to send this attachment.";

export function normalizeContentType(contentType: string): string {
  const semicolon = contentType.indexOf(";");
  return (semicolon >= 0 ? contentType.slice(0, semicolon) : contentType).trim().toLowerCase();
}

export function isImageContentType(contentType: string): boolean {
  return normalizeContentType(contentType).startsWith("image/");
}

export function pendingAttachmentsIncludeImage(pending: readonly PendingAttachment[]): boolean {
  return pending.some((item) => item.readable !== false && isImageContentType(item.contentType));
}

export function pendingSendQueueIncludesImage(queue: readonly PendingSendItem[]): boolean {
  return queue.some((item) => pendingAttachmentsIncludeImage(item.attachments));
}

export function resolveModelVision(
  models: readonly Pick<ModelDescriptor, "offeringId" | "vision">[],
  modelValue: string,
  defaultOfferingId: string | null
): boolean | null {
  const key = modelValue === INHERIT_MODEL ? defaultOfferingId : modelValue;
  if (!key) {
    return null;
  }

  return models.find((model) => model.offeringId === key)?.vision ?? null;
}

export type ImageModelCompatibilityInput = {
  models: readonly Pick<ModelDescriptor, "offeringId" | "vision">[];
  modelValue: string;
  defaultOfferingId: string | null;
  pendingAttachments: readonly PendingAttachment[];
  pendingSendQueue: readonly PendingSendItem[];
};

export function imageModelCompatibility(input: ImageModelCompatibilityInput): {
  hasImage: boolean;
  incompatible: boolean;
  message: string | null;
} {
  const hasImage =
    pendingAttachmentsIncludeImage(input.pendingAttachments)
    || pendingSendQueueIncludesImage(input.pendingSendQueue);
  if (!hasImage) {
    return { hasImage: false, incompatible: false, message: null };
  }

  const vision = resolveModelVision(input.models, input.modelValue, input.defaultOfferingId);
  if (vision !== false) {
    return { hasImage: true, incompatible: false, message: null };
  }

  return { hasImage: true, incompatible: true, message: IMAGE_MODEL_INCOMPATIBLE_MESSAGE };
}

export function queuedSendIncludesImage(item: PendingSendItem): boolean {
  return pendingAttachmentsIncludeImage(item.attachments);
}
