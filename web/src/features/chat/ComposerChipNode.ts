import { TextNode, type NodeKey, type SerializedTextNode, type Spread } from "lexical";
import { displayPart, normalizeParts, type MessagePart } from "../../services/messageParts";
import { chipPresentation } from "./MessageParts";

type ChipPart = Exclude<MessagePart, { kind: "text" }>;
type SerializedChip = Spread<
  { type: "composer-chip"; part: ChipPart },
  SerializedTextNode
>;
export class ComposerChipNode extends TextNode {
  __part: ChipPart;
  static getType() {
    return "composer-chip";
  }
  static clone(node: ComposerChipNode) {
    return new ComposerChipNode(node.__part, node.__key);
  }
  constructor(part: ChipPart, key?: NodeKey) {
    super(displayPart(part), key);
    this.__part = structuredClone(part);
    this.__mode = 1;
  }
  static importJSON(value: SerializedChip) {
    const part = normalizeParts([value.part])[0];
    if (!part || part.kind === "text") throw new Error("Invalid chip");
    return new ComposerChipNode(part);
  }
  exportJSON(): SerializedChip {
    return {
      ...super.exportJSON(),
      type: "composer-chip",
      version: 1,
      part: this.__part,
    };
  }
  createDOM() {
    const span = document.createElement("span");
    const presentation = chipPresentation(this.__part, "draft");
    span.className = presentation.className;
    span.textContent = this.__text;
    span.title = presentation.title;
    span.setAttribute("data-reference-kind", presentation.kind);
    span.setAttribute("data-composer-chip", this.__part.kind);
    if (this.__part.kind === "invocation")
      span.setAttribute("data-skill-key", this.__part.skillKey);
    return span;
  }
  updateDOM(previous: this, element: HTMLElement) {
    if (previous.__text !== this.__text) element.textContent = this.__text;
    return false;
  }
  isTextEntity() {
    return true;
  }
  canInsertTextBefore() {
    return false;
  }
  canInsertTextAfter() {
    return false;
  }
}
