import { describe, expect, it } from "vitest";
import type { PendingAttachment } from "../../services/attachments";
import type { PendingSendItem } from "../../state/sessionStore";
import {
  IMAGE_MODEL_INCOMPATIBLE_MESSAGE,
  imageModelCompatibility,
  isImageContentType,
  pendingAttachmentsIncludeImage
} from "./imageModelCompatibility";

const models = [
  { key: "scripted-alpha", vision: false },
  { key: "scripted-vision", vision: true }
];

function readyImage(): PendingAttachment {
  return {
    localId: "local-1",
    displayName: "photo.png",
    contentType: "image/png",
    byteSize: 12,
    status: "ready",
    progress: 100,
    attachmentId: "019944af-0000-7000-8000-000000000001",
    error: null
  };
}

describe("imageModelCompatibility", () => {
  it("detects image content types with parameters", () => {
    expect(isImageContentType("image/png; charset=binary")).toBe(true);
    expect(isImageContentType("text/plain")).toBe(false);
  });

  it("blocks image turns on non-vision models", () => {
    const result = imageModelCompatibility({
      models,
      modelValue: "scripted-alpha",
      defaultKey: "scripted-alpha",
      pendingAttachments: [readyImage()],
      pendingSendQueue: []
    });
    expect(result.incompatible).toBe(true);
    expect(result.message).toBe(IMAGE_MODEL_INCOMPATIBLE_MESSAGE);
  });

  it("allows image turns on vision models and text-only turns on non-vision models", () => {
    expect(
      imageModelCompatibility({
        models,
        modelValue: "scripted-vision",
        defaultKey: "scripted-alpha",
        pendingAttachments: [readyImage()],
        pendingSendQueue: []
      }).incompatible
    ).toBe(false);

    expect(
      imageModelCompatibility({
        models,
        modelValue: "scripted-alpha",
        defaultKey: "scripted-alpha",
        pendingAttachments: [],
        pendingSendQueue: []
      }).incompatible
    ).toBe(false);
  });

  it("evaluates queued attachments", () => {
    const queueItem: PendingSendItem = {
      localId: "q1",
      eventId: "e1",
      text: "",
      attachmentIds: ["a1"],
      attachments: [readyImage()]
    };
    expect(pendingAttachmentsIncludeImage(queueItem.attachments)).toBe(true);
    expect(
      imageModelCompatibility({
        models,
        modelValue: "scripted-alpha",
        defaultKey: "scripted-alpha",
        pendingAttachments: [],
        pendingSendQueue: [queueItem]
      }).incompatible
    ).toBe(true);
  });
});
