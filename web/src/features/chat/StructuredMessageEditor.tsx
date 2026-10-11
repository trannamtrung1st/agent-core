import { useEffect, useMemo, useRef, useState } from "react";
import { Alert, Button, Empty, Spin, Input } from "antd";
import { CloseOutlined, FileOutlined, MessageOutlined, InboxOutlined, FileDoneOutlined, PlayCircleOutlined, CodeOutlined } from "@ant-design/icons";
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
  type LexicalNode,
  type RangeSelection,
} from "lexical";
import { loadComposerChoices, type ChoiceCursors } from "./composerChoices";
// Keep the registered Lexical constructor outside React's refresh boundary.
import { ComposerChipNode } from "./ComposerChipNode";
import {
  messageText,
  normalizeParts,
  type ComposerChoice,
  type MessagePart,
} from "../../services/messageParts";

type ChipPart = Exclude<MessagePart, { kind: "text" }>;
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
const choiceTypes = {
  homeFile: { label: "File", Icon: FileOutlined },
  session: { label: "Chat", Icon: MessageOutlined },
  backgroundSession: { label: "Background work", Icon: InboxOutlined },
  artifact: { label: "Artifact", Icon: FileDoneOutlined },
  agentRun: { label: "Run", Icon: PlayCircleOutlined },
  skill: { label: "Skill reference", Icon: CodeOutlined },
  invocation: { label: "Skill", Icon: CodeOutlined },
};
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
    width: number;
    placement: "above" | "below";
    maxHeight: number;
  } | null>(null);
  const savedSelection = useRef<RangeSelection | null>(null);
  const pickerRef = useRef(picker);
  pickerRef.current = picker;
  const panelRef = useRef<HTMLDivElement>(null);
  const [items, setItems] = useState<ComposerChoice[]>([]);
  const [cursors, setCursors] = useState<ChoiceCursors>({});
  const [failed, setFailed] = useState<ChoiceCursors>({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [active, setActive] = useState(0);
  const [focusedControl, setFocusedControl] = useState<"editor" | "search" | null>(null);
  const activeOptionId = items[active] ? `composer-choice-${active}` : undefined;
  useEffect(() => {
    const trackFocus = () => setFocusedControl(
      document.activeElement === editor.getRootElement() ? "editor" :
      document.activeElement === panelRef.current?.querySelector('[aria-label="Search composer choices"]') ? "search" : null,
    );
    trackFocus();
    document.addEventListener("focusin", trackFocus);
    document.addEventListener("focusout", trackFocus);
    return () => {
      document.removeEventListener("focusin", trackFocus);
      document.removeEventListener("focusout", trackFocus);
    };
  }, [editor]);
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
    const width = Math.min(640, root?.closest(".composer-shell")?.clientWidth ?? 640, window.innerWidth - 16);
    // The caret may be clipped by the scrolling editor or a short viewport.
    const caretTop = Math.max(8, Math.min(rect?.top || host?.top || 8, window.innerHeight - 8));
    const caretBottom = Math.max(caretTop, Math.min(rect?.bottom || caretTop, window.innerHeight - 8));
    const above = Math.max(0, caretTop - 16);
    const below = Math.max(0, window.innerHeight - caretBottom - 16);
    const placement = above >= below ? "above" as const : "below" as const;
    // Keep room for chrome plus a complete choice even when neither side fits.
    // In that case overlap the caret rather than clipping the controls/results.
    const maxHeight = Math.min(420, window.innerHeight - 16, Math.max(224, placement === "above" ? above : below));
    return {
      width,
      x: Math.max(
        8,
        Math.min(rect?.left || host?.left || 8, window.innerWidth - width - 8),
      ),
      y: placement === "above"
        ? Math.max(caretTop - 8, maxHeight + 8)
        : Math.min(caretBottom + 8, window.innerHeight - maxHeight - 8),
      placement,
      maxHeight,
    };
  }
  function dismissPicker(restoreFocus = false) {
    editor.getEditorState().read(() => {
      const selection = $getSelection() ?? savedSelection.current;
      if ($isRangeSelection(selection))
        dismissed.current = selection.anchor.getNode().getTextContent().slice(0, selection.anchor.offset);
    });
    pickerRef.current = null;
    requestEpoch.current++;
    setPicker(null);
    setError(null);
    if (restoreFocus) editor.focus();
  }
  const dismissRef = useRef(dismissPicker);
  dismissRef.current = dismissPicker;
  useEffect(() => {
    if (!picker) return;
    const contains = (target: EventTarget | null) => target instanceof Node &&
      (editor.getRootElement()?.contains(target) || panelRef.current?.contains(target));
    const outside = (event: PointerEvent | FocusEvent) => {
      if (!contains(event.target)) dismissRef.current();
    };
    const focusOut = (event: FocusEvent) => {
      if (!contains(event.target) || contains(event.relatedTarget)) return;
      if (event.relatedTarget) dismissRef.current();
      else {
        // Removing a focused Retry/Load more control during loading is not an outside blur.
        const target = event.target;
        requestAnimationFrame(() => {
          if (target instanceof Node && target.isConnected && !contains(document.activeElement)) dismissRef.current();
        });
      }
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Tab" && event.target === editor.getRootElement()) dismissRef.current();
      if (event.key === "Escape" && !event.isComposing) {
        event.preventDefault();
        event.stopPropagation();
        dismissRef.current(true);
      }
    };
    const blur = () => dismissRef.current();
    document.addEventListener("pointerdown", outside, true);
    document.addEventListener("focusin", outside);
    document.addEventListener("focusout", focusOut);
    document.addEventListener("keydown", escape, true);
    window.addEventListener("blur", blur);
    return () => {
      document.removeEventListener("pointerdown", outside, true);
      document.removeEventListener("focusin", outside);
      document.removeEventListener("focusout", focusOut);
      document.removeEventListener("keydown", escape, true);
      window.removeEventListener("blur", blur);
    };
  }, [Boolean(picker), editor]);
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
          if (!parts.length) dismissed.current = null;
          propsRef.current.onValidationChange?.(true);
          if (!pickerRef.current) setError(null);
          const serialized = JSON.stringify(parts);
          if (serialized !== last.current) {
            last.current = serialized;
            if (propsRef.current.onPartsChange)
              propsRef.current.onPartsChange(parts);
            else propsRef.current.onChange(messageText(parts));
          }
          if (tags.has("external-draft") || editor.isComposing() || document.activeElement !== editor.getRootElement()) return;
          const selection = $getSelection();
          if ($isRangeSelection(selection))
            savedSelection.current = selection.clone();
          if (
            !$isRangeSelection(selection) ||
            !selection.isCollapsed() ||
            !$isTextNode(selection.anchor.getNode()) ||
            selection.anchor.getNode() instanceof ComposerChipNode
          ) {
            if (pickerRef.current && pickerRef.current.start !== null) {
              setPicker(null);
              dismissed.current = null;
            }
            return;
          }
          if (pickerRef.current?.start === null) return;
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
              old.y === location.y &&
              old.width === location.width &&
              old.maxHeight === location.maxHeight &&
              old.placement === location.placement
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
  }, [props.instanceId, props.ready]);
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
    if (picker && focusedControl === "editor") {
      root.setAttribute("aria-controls", "composer-choices");
      root.setAttribute("aria-autocomplete", "list");
      if (activeOptionId) root.setAttribute("aria-activedescendant", activeOptionId);
      else root.removeAttribute("aria-activedescendant");
    } else {
      root.removeAttribute("aria-controls");
      root.removeAttribute("aria-autocomplete");
      root.removeAttribute("aria-activedescendant");
    }
  }, [editor, picker, focusedControl, activeOptionId]);
  useEffect(() => {
    panelRef.current?.querySelector(`[id="composer-choice-${active}"]`)?.scrollIntoView({ block: "nearest" });
  }, [active]);
  function reloadChoices(next: ChoiceCursors) {
    // Retry/Load more can disappear during loading. Keep focus on the stable
    // editing control so removing the button does not dismiss the picker.
    const search = panelRef.current?.querySelector<HTMLInputElement>('[aria-label="Search composer choices"]');
    if (search) search.focus();
    else editor.focus();
    void load(next);
  }
  async function load(next?: ChoiceCursors) {
    const p = pickerRef.current;
    const instance = propsRef.current.instanceId;
    if (!p || !instance) {
      setError("Choose an active Agent Instance to select Skills and resources.");
      setLoading(false);
      return;
    }
    const epoch = ++requestEpoch.current;
    setLoading(true);
    setError(null);
    const page = await loadComposerChoices(instance, p.mode, p.query, next);
    if (requestEpoch.current !== epoch || propsRef.current.instanceId !== instance) return;
    setItems((old) => {
      const merged = next ? [...old, ...page.items] : page.items;
      return [...new Map(merged.map((item) => [`${item.category}:${item.id}`, item])).values()];
    });
    setCursors((old) => {
      const remaining = next ? { ...old } : {};
      for (const source of Object.keys(next ?? {})) delete remaining[source];
      return { ...remaining, ...page.next };
    });
    const remainingFailed = next ? { ...failed } : {};
    for (const source of Object.keys(next ?? {})) delete remainingFailed[source];
    Object.assign(remainingFailed, page.failed);
    setFailed(remainingFailed);
    setError(Object.keys(remainingFailed).length ? "Some choices could not load. Retry to load the missing results." : null);
    if (!next) setActive(0);
    setLoading(false);
  }
  useEffect(() => {
    if (!picker) {
      requestEpoch.current++;
      return;
    }
    setItems([]);
    setCursors({});
    setFailed({});
    setLoading(true);
    const timer = window.setTimeout(() => void load(), 150);
    return () => { window.clearTimeout(timer); requestEpoch.current++; };
  }, [picker?.mode, picker?.query, props.instanceId]);
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
            ["ArrowDown", "ArrowUp", "Enter"].includes(event.key)
          ) {
            event.preventDefault();
            if (event.key === "ArrowDown")
              setActive((i) => Math.max(0, Math.min(i + 1, items.length - 1)));
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
        // Remove the replacement range before deduplicating against retained chips.
        selection.removeText();
        const retainedSkills = new Set(
          $getRoot().getAllTextNodes().flatMap((node) =>
            node instanceof ComposerChipNode && node.__part.kind === "invocation"
              ? [node.__part.skillKey]
              : [],
          ),
        );
        parts = parts.filter((part) =>
          part.kind !== "invocation" || !retainedSkills.has(part.skillKey),
        );
        if (parts.length) {
          selection.insertNodes(
            parts.map((p) =>
              p.kind === "text"
                ? $createTextNode(p.text)
                : new ComposerChipNode(p),
            ),
          );
        }
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
          ref={panelRef}
          className="composer-picker"
          role="dialog"
          aria-label={
            picker.mode === "invocation" ? "Use Skill" : "Reference resource"
          }
          style={{
            left: picker.x,
            top: picker.y,
            width: picker.width,
            maxHeight: picker.maxHeight,
            transform: picker.placement === "above" ? "translateY(-100%)" : "none",
          }}
        >
          <div className="composer-picker-heading">
            {picker.mode === "invocation"
              ? "Use Skill · next turn"
              : "Reference resource"}
            <Button
              type="text"
              size="small"
              onClick={() => dismissPicker(true)}
              aria-label="Close picker"
              icon={<CloseOutlined aria-hidden />}
            />
          </div>
          {picker.start === null ? (
            <Input
              aria-label="Search composer choices"
              role="combobox"
              aria-expanded="true"
              aria-autocomplete="list"
              aria-controls={focusedControl === "search" ? "composer-choices" : undefined}
              aria-activedescendant={focusedControl === "search" ? activeOptionId : undefined}
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
                } else if (e.key === "ArrowDown") {
                  e.preventDefault();
                  setActive((i) => Math.max(0, Math.min(i + 1, items.length - 1)));
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
            {items.map((item, i) => {
              const { label, Icon } = choiceTypes[item.category as keyof typeof choiceTypes] ?? choiceTypes.homeFile;
              return (
              <button
                type="button"
                id={`composer-choice-${i}`}
                role="option"
                aria-selected={i === active}
                aria-disabled={Boolean(item.unavailableReason)}
                className={
                  i === active ? "composer-choice active" : "composer-choice"
                }
                key={`${item.category}:${item.id}`}
                title={`${item.label} · ${item.unavailableReason ?? item.description}`}
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => choose(item)}
              >
                <Icon className="composer-choice-icon" aria-hidden />
                <span className="composer-choice-body">
                  <span className="composer-choice-title">{item.label}</span>
                  <span className="composer-choice-description">{item.unavailableReason ?? item.description}</span>
                </span>
                <span className="composer-choice-kind">{label}</span>
              </button>
            ); })}
          </div>
          {loading ? (
            <Spin size="small" />
          ) : !items.length && !error ? (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={Object.keys(cursors).length ? "More choices remain. Load more to continue searching." : "No matching choices"}
            />
          ) : null}
          {error ? (
            <Alert
              type="warning"
              title={error}
              action={
                <Button size="small" onClick={() => reloadChoices(failed)}>
                  Retry
                </Button>
              }
            />
          ) : null}
          {Object.keys(cursors).length ? (
            <Button
              size="small"
              disabled={loading}
              onClick={() => reloadChoices(cursors)}
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
