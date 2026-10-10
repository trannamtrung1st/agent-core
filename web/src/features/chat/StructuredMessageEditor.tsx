import { useEffect, useMemo, useRef, useState } from "react";
import { Alert, Button, Empty, Spin, Input } from "antd";
import { CloseOutlined } from "@ant-design/icons";
import { LexicalComposer } from "@lexical/react/LexicalComposer";
import { PlainTextPlugin } from "@lexical/react/LexicalPlainTextPlugin";
import { ContentEditable } from "@lexical/react/LexicalContentEditable";
import { HistoryPlugin } from "@lexical/react/LexicalHistoryPlugin";
import { LexicalErrorBoundary } from "@lexical/react/LexicalErrorBoundary";
import { useLexicalComposerContext } from "@lexical/react/LexicalComposerContext";
import {
  $createParagraphNode,
  $createTextNode,
  $getRoot,
  $getSelection,
  $setSelection,
  $getNodeByKey,
  $isElementNode,
  $isLineBreakNode,
  $isRangeSelection,
  $isTextNode,
  COMMAND_PRIORITY_HIGH,
  COPY_COMMAND,
  CUT_COMMAND,
  PASTE_COMMAND,
  KEY_DOWN_COMMAND,
  TextNode,
  type LexicalNode,
  type NodeKey,
  type SerializedTextNode,
  type Spread,
  type RangeSelection,
} from "lexical";
import { ownerFetch } from "../../services/api";
import {
  displayPart,
  messageText,
  normalizeParts,
  type ComposerChoice,
  type MessagePart,
} from "../../services/messageParts";

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
    span.className = `composer-chip composer-chip-${this.__part.kind}`;
    span.textContent = this.__text;
    span.title =
      this.__part.kind === "invocation"
        ? `${this.__part.skillKey} · activates for the next turn`
        : messageText([this.__part]);
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
function partsFromRoot(): MessagePart[] {
  const result: MessagePart[] = [];
  function visit(node: LexicalNode) {
    if (node instanceof ComposerChipNode)
      result.push(structuredClone(node.__part));
    else if ($isTextNode(node))
      result.push({ kind: "text", text: node.getTextContent() });
    else if ($isLineBreakNode(node)) result.push({ kind: "text", text: "\n" });
    else if ($isElementNode(node)) node.getChildren().forEach(visit);
  }
  $getRoot()
    .getChildren()
    .forEach((node, i) => {
      if (i) result.push({ kind: "text", text: "\n" });
      visit(node);
    });
  return normalizeParts(result);
}
function writeParts(parts: MessagePart[]) {
  const root = $getRoot();
  root.clear();
  const paragraph = $createParagraphNode();
  root.append(paragraph);
  for (const p of parts)
    paragraph.append(
      p.kind === "text" ? $createTextNode(p.text) : new ComposerChipNode(p),
    );
}
const categories = [
  { key: "homeFile", label: "Files" },
  { key: "session", label: "Chats" },
  { key: "backgroundSession", label: "Background work" },
  { key: "artifact", label: "Artifacts" },
  { key: "agentRun", label: "Runs" },
  { key: "skill", label: "Skills" },
];
export type PickerRequest = {
  category: "invocation" | "homeFile";
  nonce: number;
} | null;
type Props = {
  draft: string;
  parts?: MessagePart[] | null;
  instanceId?: string | null;
  ready: boolean;
  placeholder: string;
  canSend: boolean;
  onChange: (text: string) => void;
  onPartsChange?: (parts: MessagePart[]) => void;
  onSend: (behavior?: "interrupt") => void;
  onFiles: (files: File[]) => void;
  pickerRequest: PickerRequest;
  onValidationChange?: (valid: boolean) => void;
};
function Controller(props: Props) {
  const [editor] = useLexicalComposerContext();
  const propsRef = useRef(props);
  propsRef.current = props;
  const [picker, setPicker] = useState<{
    mode: "invocation" | "reference";
    query: string;
    start: number | null;
    x: number;
    y: number;
  } | null>(null);
  const savedSelection = useRef<RangeSelection | null>(null);
  const pickerRef = useRef(picker);
  pickerRef.current = picker;
  const [category, setCategory] = useState("homeFile");
  const [items, setItems] = useState<ComposerChoice[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [active, setActive] = useState(0);
  const dismissed = useRef<string | null>(null);
  const requestEpoch = useRef(0);
  const last = useRef<string>("");
  function anchor() {
    const root = editor.getRootElement();
    const selected = window.getSelection();
    const range =
      selected?.rangeCount && root?.contains(selected.anchorNode)
        ? selected.getRangeAt(0)
        : null;
    const rect = range?.getBoundingClientRect();
    const host = editor.getRootElement()?.getBoundingClientRect();
    return {
      x: Math.max(
        8,
        Math.min(rect?.left || host?.left || 8, window.innerWidth - 328),
      ),
      y: Math.max(8, (rect?.top || host?.top || 400) - 8),
    };
  }
  function choose(choice: ComposerChoice) {
    if (choice.unavailableReason) return;
    editor.update(() => {
      let selection = $getSelection();
      if (
        !$isRangeSelection(selection) &&
        savedSelection.current &&
        $getNodeByKey(savedSelection.current.anchor.key) &&
        $getNodeByKey(savedSelection.current.focus.key)
      ) {
        selection = savedSelection.current.clone();
        $setSelection(selection);
      }
      if (!$isRangeSelection(selection)) selection = $getRoot().selectEnd();
      if (!$isRangeSelection(selection)) return;
      const p = pickerRef.current;
      if (
        p?.start !== null &&
        p?.start !== undefined &&
        selection.isCollapsed() &&
        $isTextNode(selection.anchor.getNode()) &&
        !(selection.anchor.getNode() instanceof ComposerChipNode)
      ) {
        selection.anchor.set(selection.anchor.key, p.start, "text");
      }
      const part: ChipPart = choice.skillKey
        ? {
            kind: "invocation",
            invocationKind: "skill",
            skillKey: choice.skillKey,
            label: choice.label,
          }
        : {
            kind: "reference",
            reference: choice.reference!,
            label: choice.label,
          };
      const duplicate =
        part.kind === "invocation" &&
        partsFromRoot().some(
          (p) => p.kind === "invocation" && p.skillKey === part.skillKey,
        );
      selection.insertNodes(
        duplicate
          ? [$createTextNode(" ")]
          : [new ComposerChipNode(part), $createTextNode(" ")],
      );
    });
    setPicker(null);
    dismissed.current = "selected";
    editor.focus();
  }
  const chooseRef = useRef(choose);
  chooseRef.current = choose;
  useEffect(() => {
    editor.setEditable(props.ready);
  }, [editor, props.ready]);
  useEffect(() => {
    let incoming: MessagePart[];
    try {
      incoming = normalizeParts(
        props.parts ??
          (props.draft ? [{ kind: "text", text: props.draft }] : []),
      );
    } catch (e) {
      setError(e instanceof Error ? e.message : "Message is too large.");
      propsRef.current.onValidationChange?.(false);
      return;
    }
    if (JSON.stringify(incoming) === last.current) return;
    last.current = JSON.stringify(incoming);
    editor.update(() => writeParts(incoming), { tag: "external-draft" });
  }, [editor, props.draft, props.parts]);
  useEffect(
    () =>
      editor.registerUpdateListener(({ editorState, tags }) => {
        editorState.read(() => {
          let parts: MessagePart[];
          try {
            parts = partsFromRoot();
          } catch (e) {
            setError(e instanceof Error ? e.message : "Message is too large.");
            propsRef.current.onValidationChange?.(false);
            return;
          }
          propsRef.current.onValidationChange?.(true);
          if (!pickerRef.current) setError(null);
          const serialized = JSON.stringify(parts);
          if (serialized !== last.current) {
            last.current = serialized;
            if (propsRef.current.onPartsChange)
              propsRef.current.onPartsChange(parts);
            else propsRef.current.onChange(messageText(parts));
          }
          if (tags.has("external-draft") || editor.isComposing()) return;
          const selection = $getSelection();
          if ($isRangeSelection(selection))
            savedSelection.current = selection.clone();
          if (
            !$isRangeSelection(selection) ||
            !selection.isCollapsed() ||
            !$isTextNode(selection.anchor.getNode()) ||
            selection.anchor.getNode() instanceof ComposerChipNode
          )
            return;
          const prefix = selection.anchor
            .getNode()
            .getTextContent()
            .slice(0, selection.anchor.offset);
          const match = /(?:^|\s)([/@])([^\s/@]*)$/.exec(prefix);
          if (match && (!dismissed.current || !prefix.startsWith(dismissed.current))) {
            const location = anchor();
            const mode = match[1] === "/" ? "invocation" : "reference";
            setPicker((old) =>
              old?.mode === mode &&
              old.query === match[2] &&
              old.start === prefix.length - match[2].length - 1 &&
              old.x === location.x &&
              old.y === location.y
                ? old
                : {
                    mode,
                    query: match[2],
                    start: prefix.length - match[2].length - 1,
                    ...location,
                  },
            );
          } else if (!match && pickerRef.current?.start !== null) {
            setPicker(null);
            dismissed.current = null;
          }
        });
      }),
    [editor],
  );
  useEffect(() => {
    if (!props.pickerRequest || !props.ready) return;
    editor.focus();
    setCategory("homeFile");
    dismissed.current = null;
    setPicker({
      mode:
        props.pickerRequest.category === "invocation"
          ? "invocation"
          : "reference",
      query: "",
      start: null,
      ...anchor(),
    });
  }, [props.pickerRequest, editor, props.ready]);
  useEffect(() => {
    setPicker(null);
    setItems([]);
    requestEpoch.current++;
  }, [props.instanceId]);
  useEffect(() => {
    if (!picker) return;
    const reposition = () => setPicker((p) => (p ? { ...p, ...anchor() } : p));
    const settled = () => requestAnimationFrame(reposition);
    const observer = new ResizeObserver(settled);
    const root = editor.getRootElement();
    if (root) observer.observe(root);
    window.addEventListener("resize", settled);
    window.addEventListener("transitionend", settled, true);
    window.addEventListener("scroll", reposition, true);
    return () => {
      observer.disconnect();
      window.removeEventListener("resize", settled);
      window.removeEventListener("transitionend", settled, true);
      window.removeEventListener("scroll", reposition, true);
    };
  }, [Boolean(picker), editor]);
  useEffect(() => {
    if (picker?.start !== null) return;
    const frame = requestAnimationFrame(() =>
      document
        .querySelector<HTMLInputElement>(
          '[aria-label="Search composer choices"]',
        )
        ?.focus(),
    );
    return () => cancelAnimationFrame(frame);
  }, [picker?.mode, picker?.start]);
  useEffect(() => {
    const root = editor.getRootElement();
    if (!root) return;
    if (picker) {
      root.setAttribute("aria-controls", "composer-choices");
      root.setAttribute("aria-activedescendant", `composer-choice-${active}`);
    } else {
      root.removeAttribute("aria-controls");
      root.removeAttribute("aria-activedescendant");
    }
  }, [editor, picker, active]);
  async function load(next?: string) {
    const p = pickerRef.current;
    const instance = propsRef.current.instanceId;
    if (!p || !instance) {
      setError(
        "Choose an active Agent Instance to select Skills and resources.",
      );
      return;
    }
    const epoch = ++requestEpoch.current;
    setLoading(true);
    setError(null);
    try {
      const params = new URLSearchParams({
        category: p.mode === "invocation" ? "invocation" : category,
        search: p.query,
        ...(next ? { cursor: next } : {}),
      });
      const response = await ownerFetch(
        `/api/v2/agent-instances/${instance}/composer?${params}`,
      );
      if (!response.ok) throw new Error("Unable to load choices. Try again.");
      const page = (await response.json()) as {
        items: ComposerChoice[];
        nextCursor: string | null;
      };
      if (
        requestEpoch.current !== epoch ||
        propsRef.current.instanceId !== instance
      )
        return;
      setItems((old) => (next ? [...old, ...page.items] : page.items));
      setCursor(page.nextCursor);
      setActive(0);
    } catch (e) {
      if (requestEpoch.current === epoch)
        setError(e instanceof Error ? e.message : "Unable to load choices.");
    } finally {
      if (requestEpoch.current === epoch) setLoading(false);
    }
  }
  useEffect(() => {
    if (!picker) {
      requestEpoch.current++;
      return;
    }
    setItems([]);
    setCursor(null);
    setLoading(true);
    const timer = window.setTimeout(() => void load(), 150);
    return () => window.clearTimeout(timer);
  }, [picker?.mode, picker?.query, category, props.instanceId]);
  useEffect(
    () =>
      editor.registerCommand(
        KEY_DOWN_COMMAND,
        (event) => {
          if (
            event.isComposing ||
            event.keyCode === 229 ||
            editor.isComposing()
          )
            return false;
          const p = pickerRef.current;
          if (
            p &&
            ["ArrowDown", "ArrowUp", "Escape", "Enter"].includes(event.key)
          ) {
            event.preventDefault();
            if (event.key === "Escape") {
              const s = $getSelection();
              if ($isRangeSelection(s))
                dismissed.current = s.anchor
                  .getNode()
                  .getTextContent()
                  .slice(0, s.anchor.offset);
              setPicker(null);
            } else if (event.key === "ArrowDown")
              setActive((i) => Math.min(i + 1, items.length - 1));
            else if (event.key === "ArrowUp")
              setActive((i) => Math.max(0, i - 1));
            else if (items[active] && !event.repeat)
              chooseRef.current(items[active]);
            return true;
          }
          if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            if (
              propsRef.current.ready &&
              propsRef.current.canSend &&
              !event.repeat
            )
              propsRef.current.onSend(
                event.metaKey || event.ctrlKey ? "interrupt" : undefined,
              );
            return true;
          }
          return false;
        },
        COMMAND_PRIORITY_HIGH,
      ),
    [editor, items, active],
  );
  useEffect(() => {
    const copy = (event: ClipboardEvent | null, cut: boolean) => {
      if (!event?.clipboardData) return false;
      const selection = $getSelection();
      if (!$isRangeSelection(selection)) return false;
      const nodes = selection.getNodes();
      const parts: MessagePart[] = [];
      let previousBlock: string | null = null;
      const [start, end] = selection.isBackward()
        ? [selection.focus, selection.anchor]
        : [selection.anchor, selection.focus];
      for (const node of nodes) {
        if (!$isTextNode(node) && !$isLineBreakNode(node)) continue;
        let block: LexicalNode = node;
        while (block.getParent() && block.getParent() !== $getRoot())
          block = block.getParent()!;
        if (previousBlock && previousBlock !== block.getKey())
          parts.push({ kind: "text", text: "\n" });
        previousBlock = block.getKey();
        if (node instanceof ComposerChipNode) parts.push(node.__part);
        else if ($isTextNode(node))
          parts.push({
            kind: "text",
            text: node
              .getTextContent()
              .slice(
                node.getKey() === start.key ? start.offset : 0,
                node.getKey() === end.key ? end.offset : undefined,
              ),
          });
        else if ($isLineBreakNode(node))
          parts.push({ kind: "text", text: "\n" });
      }
      event.preventDefault();
      event.clipboardData.setData("text/plain", selection.getTextContent());
      event.clipboardData.setData(
        "application/x-agent-core-parts",
        JSON.stringify({
          instanceId: propsRef.current.instanceId,
          parts: normalizeParts(parts),
        }),
      );
      if (cut) selection.removeText();
      return true;
    };
    const a = editor.registerCommand(
      COPY_COMMAND,
      (event) => copy(event as ClipboardEvent, false),
      COMMAND_PRIORITY_HIGH,
    );
    const b = editor.registerCommand(
      CUT_COMMAND,
      (event) => copy(event as ClipboardEvent, true),
      COMMAND_PRIORITY_HIGH,
    );
    const c = editor.registerCommand(
      PASTE_COMMAND,
      (event) => {
        if (!event || !("clipboardData" in event) || !event.clipboardData)
          return false;
        if (event.clipboardData.files.length) {
          event.preventDefault();
          propsRef.current.onFiles(Array.from(event.clipboardData.files));
          return true;
        }
        const selection = $getSelection();
        if (!$isRangeSelection(selection)) return false;
        event.preventDefault();
        let parts: MessagePart[] = [
          { kind: "text", text: event.clipboardData.getData("text/plain") },
        ];
        try {
          const value = JSON.parse(
            event.clipboardData.getData("application/x-agent-core-parts"),
          );
          if (
            propsRef.current.instanceId &&
            value.instanceId === propsRef.current.instanceId
          )
            parts = normalizeParts(value.parts);
        } catch {
          /* External paste remains literal plain text. */
        }
        dismissed.current = "paste";
        setPicker(null);
        selection.insertNodes(
          parts.map((p) =>
            p.kind === "text"
              ? $createTextNode(p.text)
              : new ComposerChipNode(p),
          ),
        );
        const current = $getSelection();
        if ($isRangeSelection(current))
          dismissed.current = current.anchor
            .getNode()
            .getTextContent()
            .slice(0, current.anchor.offset);
        return true;
      },
      COMMAND_PRIORITY_HIGH,
    );
    return () => {
      a();
      b();
      c();
    };
  }, [editor]);
  return (
    <>
      {error && !picker ? (
        <Alert type="warning" title={error} showIcon />
      ) : null}
      {picker ? (
        <div
          className="composer-picker"
          role="dialog"
          aria-label={
            picker.mode === "invocation" ? "Use Skill" : "Reference resource"
          }
          style={{
            left: picker.x,
            top: picker.y,
            maxHeight: Math.max(48, Math.min(420, picker.y - 8)),
          }}
        >
          <div className="composer-picker-heading">
            {picker.mode === "invocation"
              ? "Use Skill · next turn"
              : "Reference resource"}
            <Button
              type="text"
              size="small"
              onClick={() => {
                setPicker(null);
                editor.getEditorState().read(() => { const selection=$getSelection(); if($isRangeSelection(selection)) dismissed.current=selection.anchor.getNode().getTextContent().slice(0,selection.anchor.offset); });
                editor.focus();
              }}
              aria-label="Close picker"
              icon={<CloseOutlined aria-hidden />}
            />
          </div>
          {picker.mode === "reference" ? (
            <div className="composer-picker-categories">
              {categories.map((c) => (
                <Button
                  size="small"
                  key={c.key}
                  type={category === c.key ? "primary" : "text"}
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => setCategory(c.key)}
                >
                  {c.label}
                </Button>
              ))}
            </div>
          ) : null}
          {picker.start === null ? (
            <Input
              autoFocus
              aria-label="Search composer choices"
              placeholder="Search choices"
              value={picker.query}
              onChange={(e) =>
                setPicker((p) => (p ? { ...p, query: e.target.value } : p))
              }
              onKeyDown={(e) => {
                if (e.nativeEvent.isComposing || e.nativeEvent.keyCode === 229)
                  return;
                if (e.key === "Enter") {
                  e.preventDefault();
                  e.stopPropagation();
                  if (items[active]) choose(items[active]);
                } else if (e.key === "Escape") {
                  e.preventDefault();
                  setPicker(null);
                  editor.focus();
                } else if (e.key === "ArrowDown") {
                  e.preventDefault();
                  setActive((i) => Math.min(i + 1, items.length - 1));
                } else if (e.key === "ArrowUp") {
                  e.preventDefault();
                  setActive((i) => Math.max(0, i - 1));
                }
              }}
            />
          ) : null}
          <span className="sr-only" role="status" aria-live="polite">
            {loading
              ? "Loading choices"
              : `${items.length} choices available. Use arrow keys and Enter to select.`}
          </span>
          <div
            id="composer-choices"
            role="listbox"
            aria-label="Composer choices"
            className="composer-picker-list"
          >
            {items.map((item, i) => (
              <button
                type="button"
                id={`composer-choice-${i}`}
                role="option"
                aria-selected={i === active}
                aria-disabled={Boolean(item.unavailableReason)}
                className={
                  i === active ? "composer-choice active" : "composer-choice"
                }
                key={item.id}
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => choose(item)}
              >
                <strong>{item.label}</strong>
                <span>{item.unavailableReason ?? item.description}</span>
              </button>
            ))}
          </div>
          {loading ? (
            <Spin size="small" />
          ) : !items.length && !error ? (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="No matching choices"
            />
          ) : null}
          {error ? (
            <Alert
              type="warning"
              title={error}
              action={
                <Button size="small" onClick={() => void load()}>
                  Retry
                </Button>
              }
            />
          ) : null}
          {cursor ? (
            <Button
              size="small"
              disabled={loading}
              onClick={() => void load(cursor)}
            >
              Load more
            </Button>
          ) : null}
        </div>
      ) : null}
    </>
  );
}
export function StructuredMessageEditor(props: Props) {
  const config = useMemo(
    () => ({
      namespace: "AgentCoreComposer",
      nodes: [ComposerChipNode],
      onError: (error: Error) => {
        throw error;
      },
      theme: {},
      editable: props.ready,
    }),
    [],
  );
  return (
    <LexicalComposer initialConfig={config}>
      <div className="composer-editor">
        <PlainTextPlugin
          contentEditable={
            <ContentEditable
              className="message-field structured-message-field"
              aria-label="Message"
              aria-placeholder={props.placeholder}
              placeholder={
                <span className="composer-placeholder">
                  {props.placeholder}
                </span>
              }
              role="textbox"
              aria-multiline="true"
              aria-description="Enter sends or queues. Command or Control Enter steers. Shift Enter adds a line. Slash selects a Skill; at sign references a resource."
            />
          }
          ErrorBoundary={LexicalErrorBoundary}
        />
      </div>
      <HistoryPlugin />
      <Controller {...props} />
    </LexicalComposer>
  );
}
