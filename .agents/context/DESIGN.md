---
name: Agent Core
description: Pixel dialogue-field UI for one persistent text/voice session.
colors:
  mint: "#58e6c2"
  persimmon: "#ff6b3d"
  field: "#08090f"
  obsidian: "#071116"
  panel: "#0d2024"
  bone: "#f2eadf"
  steel: "#7699a8"
  mint-chrome: "#64a08c"
  mint-deep: "#2a6f62"
  input: "#08141a"
  disabled: "#081014"
  key-hover: "#123036"
  danger-well: "#1a0c0a"
  bevel-hi: "#3d7a70"
  bevel-lo: "#0a1c20"
typography:
  display:
    fontFamily: "Martian Mono, ui-monospace, Cascadia Code, SFMono-Regular, monospace"
    fontSize: "44px"
    fontWeight: 500
    lineHeight: 1
    letterSpacing: "0.01em"
  title:
    fontFamily: "Martian Mono, ui-monospace, Cascadia Code, SFMono-Regular, monospace"
    fontSize: "16px"
    fontWeight: 500
    lineHeight: 1.35
    letterSpacing: "0"
  body:
    fontFamily: "Martian Mono, ui-monospace, Cascadia Code, SFMono-Regular, monospace"
    fontSize: "16px"
    fontWeight: 500
    lineHeight: 1.45
    letterSpacing: "0"
  label:
    fontFamily: "Martian Mono, ui-monospace, Cascadia Code, SFMono-Regular, monospace"
    fontSize: "13px"
    fontWeight: 700
    lineHeight: 1.3
    letterSpacing: "0.08em"
rounded:
  window: "4px"
spacing:
  space-1: "8px"
  space-2: "16px"
  space-3: "24px"
  space-4: "32px"
  space-5: "40px"
  inset: "40px"
  chrome: "16px"
  marker: "16px"
components:
  button-key:
    backgroundColor: "{colors.obsidian}"
    textColor: "{colors.bone}"
    rounded: "{rounded.window}"
    padding: "0 24px"
    height: "56px"
    width: "7.5rem"
    typography: "{typography.label}"
  button-key-hover:
    backgroundColor: "{colors.key-hover}"
    textColor: "{colors.mint}"
  button-key-disabled:
    backgroundColor: "{colors.disabled}"
    textColor: "{colors.mint-deep}"
  input-message:
    backgroundColor: "{colors.input}"
    textColor: "{colors.bone}"
    rounded: "{rounded.window}"
    padding: "16px"
    height: "56px"
    typography: "{typography.body}"
  input-select:
    backgroundColor: "{colors.input}"
    textColor: "{colors.bone}"
    rounded: "{rounded.window}"
    height: "56px"
    typography: "{typography.body}"
  input-select-hover:
    backgroundColor: "#0a1820"
    textColor: "{colors.bone}"
  select-list:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.bone}"
    rounded: "{rounded.window}"
    padding: "8px"
  select-option-highlight:
    backgroundColor: "{colors.key-hover}"
    textColor: "{colors.mint}"
  window-panel:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.bone}"
    rounded: "{rounded.window}"
    padding: "16px"
  error-banner:
    backgroundColor: "{colors.danger-well}"
    textColor: "{colors.persimmon}"
    padding: "16px"
    typography: "{typography.title}"
---

# Design System: Agent Core

Visual presentation only. Product behavior, session semantics and architecture remain in `/docs`. If this file conflicts with `/docs` on behavior, `/docs` wins.

This file is the working source of truth for **how the MVP UI should look**. Tokens and rules are taken from the shipped UI (`web/src/styles.css`, `web/src/features/chat/`, `web/src/components/Select.tsx`) and the locked Pixel Dialogue Field / Obsidian Mint direction. The quiet-studio paper-and-ink seed is an anti-reference.

## Overview

**Creative North Star: "Pixel Dialogue Field"**

Agent Core is a sixteen-color dialogue field: a near-black Bayer-dithered plane, beveled mint windows, and one self-hosted mono. Presence is ordered-dither light in the HUD, talk docks as a bordered transcript, and Voice is the same session lighting mint. It is an operate surface for one identity and one conversation — personal and live, not an admin dashboard and not a marketing page.

Density is HUD-tight, then generous empty field. Status is labeled in type (Ready, Starting voice…, Listening, Reconnecting). Synthetic stays named. Motion is stepped phosphor, off under `prefers-reduced-motion`.

Confirmed rejections: cream-paper messengers, Fraunces/Source Sans pairings, bubble columns, fake inboxes, nested cards, Inter-on-white chat clones, smooth gradients, drop-shadow elevation, and native OS select menus.

**Key Characteristics:**

- Full-bleed field with a 256px repeating dither tile; no centered paper column
- 1px mint stroke plus inset bevel on every window and key
- One face (Martian Mono) at four sizes; no second family
- HUD (wordmark, profile, ready, identity) + presence plate + transcript + key dock
- Mint for live connection and focus; persimmon only for interrupt, fail, and error

## Colors

Obsidian ground, bone speech, mint for the live line, persimmon only when something breaks.

### Primary

- **Mint phosphor** (`{colors.mint}`): Ready, Listening, identity role mark, picker Identity legend, agent speaker and dash, window strokes, caret, selection, focus ring, and the wordmark dash. Rarity is the point — fill stays obsidian/panel.

### Secondary

- **Persimmon** (`{colors.persimmon}`): Interrupted/failed entry status, error banner stroke and type. Not a primary button fill in the shipped UI.

### Neutral

- **Field** (`{colors.field}`): Page and dithered plane.
- **Obsidian** (`{colors.obsidian}`): Key faces.
- **Panel** (`{colors.panel}`): Transcript and picker interiors; also the notch behind the Message legend.
- **Bone** (`{colors.bone}`): Wordmark, profile, identity line, user speaker and dash, message body, key labels.
- **Steel** (`{colors.steel}`): Empty transcript prompt only.
- **Mint chrome** (`{colors.mint-chrome}`): Transcript bar, Live, Message legend — chrome meta, not speech.
- **Mint deep** (`{colors.mint-deep}`): Disabled key type and scrollbar thumb.
- **Input well** (`{colors.input}`): Textarea and identity select trigger fill.
- **Disabled well** (`{colors.disabled}`): Disabled key fill.
- **Key hover** (`{colors.key-hover}`): Hover fill on enabled keys.
- **Danger well** (`{colors.danger-well}`): Error banner fill.

**The Mint Is Live Rule.** Mint is connection, focus, and phosphor. Do not flood panels or keys with mint fill.

**The Persimmon Alarm Rule.** Persimmon appears only for interrupt, fail, and recoverable error — never as decoration or as the default Send key.

## Typography

**Display Font:** Martian Mono (self-hosted `web/public/fonts/MartianMono.ttf`; ui-monospace fallbacks)
**Body Font:** same
**Label/Mono Font:** same

**Character:** One compressed mono, unsmoothed, slightly condensed (body stretch 85%, wordmark stretch 75%). It reads as a terminal field, not as “code costume” on a second face.

### Hierarchy

- **Display** (500, 44px / 32px below 880px, line-height 1, +0.01em, uppercase, stretch 75%): Wordmark “Agent Core” only. Dashed mint underline is 2px, 8px-on/8px-off, 17.5rem (12rem on small screens).
- **Title** (500, 16px, 1.35): Profile, Ready/Listening, identity line.
- **Body** (500, 16px, 1.45, measure 72ch): Transcript copy. Speaker is 700 in a 4ch slot; user name and dash are bone, agent name and dash are mint. Empty prompt is steel.
- **Label** (700, 13px, +0.08em, uppercase): Transcript bar, Live, picker Identity legend, interrupted/failed status. Message legend uses the same size/weight in mint-chrome without CSS uppercase. Key letter-spacing 0.06em.

Body and UI share the family. `-webkit-font-smoothing: none`. Do not load Google Fonts at runtime.

**The One Voice Rule.** One self-hosted mono for wordmark, HUD, messages, and keys. Do not introduce a serif display or a sans UI face.

## Layout

Full-bleed CSS grid on `.field`: HUD / transcript / dock, padding `{spacing.inset}` (40px; 16px at ≤879px), region gap `{spacing.space-5}` (40px; 24px at ≤879px). Picker uses HUD / picker with the same inset and gap; the picker window is `min(36rem, 100%)` and top-aligned, not vertically centered in leftover space.

The conversation sits in `.app-shell`, a one-column grid that fills the viewport. There is no session list. When session navigation ships, add a leading track on this wrapper (`minmax(0, 16rem) minmax(0, 1fr)`) without rewriting ChatApp.

HUD is two columns: copy | presence plate. Copy stacks wordmark, then a 16px-gap meta block (profile/ready, then identity in session). Status and identity share a `{spacing.marker}` (16px) marker column and 16px gap. Presence is in-flow in that row (`max-width` 20rem / 36vw, `max-height` 12rem; 7.5rem ≤879px; 5.5rem ≤639px) so it does not overlap the transcript.

Transcript is the stretching mid band. Entries reuse the HUD row grid (16px marker, 16px gap, copy). List gap is 16px; well padding is 16px chrome. Dock stacks optional error then composer. Composer is `1fr + auto` on desktop (field | equal keys); one column below 880px with keys in a 2-up grid and End spanning full width. Pending attachment chips sit in the message stack under the textarea. Control height 56px (44px ≤879px). Key width 7.5rem (7rem ≤879px).

Breakpoints are structural, not fluid type:

- ≥ 880px: three-row session grid, keys in a row, 40px inset
- 640–879px: 16px inset, 32px wordmark, wrapping transcript bar, stacked composer
- < 640px: smaller presence; identity role may wrap

Long conversations scroll only the transcript well. No horizontal page scroll.

Voice is the same shell: labeled composer stays visible. End remains the session-end key until `/docs` and tests change together.

**The Shared Inset Rule.** HUD, transcript, picker, and dock share one inset and one region gap. Do not mix percentage-absolute boxes with the token grid.

**The Shared Marker Column Rule.** HUD rows and transcript entries use a 16px marker column and 16px gap. Do not introduce a second icon family.

## Elevation & Depth

No drop shadows. Depth is a 1px mint stroke plus an inset bevel (`inset 1px 1px 0 {colors.bevel-hi}, inset -1px -1px 0 {colors.bevel-lo}`) on windows, fields, and keys. The field tile and the 32px mint cell grid inside the transcript well are pattern, not elevation. Optional stepped phosphor on the presence plate and a 8px mint text-glow on Ready/Live when motion is allowed.

### Shadow Vocabulary

- **Bevel** (`box-shadow: inset 1px 1px 0 #3d7a70, inset -1px -1px 0 #0a1c20`): Windows, selects, textareas, keys.
- **Phosphor glow** (`text-shadow: 0 0 8px color-mix(in srgb, #58e6c2 55%, transparent)`): Ready and Live only, 2.4s `steps(2)`, off under reduced motion.

**The Bevel Not Drop Rule.** Surfaces sit in the field. Do not add offset drop shadows or glass blur.

## Shapes

Windows and keys use a 4px radius (`{rounded.window}`) and a 1px mint stroke. Corners stay almost square; this is a CRT/window, not a bubble.

Markers are CSS geometry in mint, never emoji or icon fonts: 12px plus, 10px rotated square (diamond), 8px square, and a 10×6px clip-path chevron in mint-chrome for the select trigger. Plus marks HUD status and user lines; diamond marks identity and agent lines; square marks transcript chrome. They sit in the 16px marker column.

The field texture is `assets/plates/field-texture.png` tiled at 256px. Presence is `assets/plates/presence.png` (and `web/public/plates/`), pixelated, decorative (`alt=""`). `image-rendering: pixelated` on body, tile, and plate.

Selection is mint on obsidian. Caret is mint. Scrollbar track `#071016`, thumb mint-deep, 8px.

Focus-visible: 3px `{colors.mint}` outline, 2px offset, on every operable control.

**The Labeled Key Rule.** Attach, Send, Voice, Cancel, Mute, Unmute, End, and Start conversation are visible text with those accessible names. Markers may accompany HUD copy; they are never the sole label. Pending files are chips inside the message stack, not a second dock.

## Components

### Buttons (keys)

Beveled obsidian keys. Letter-spacing 0.06em, weight 700, 4px radius, 56×7.5rem on desktop.

- **Shape:** Near-square window (4px) with 1px mint stroke and bevel
- **Default:** `{colors.obsidian}` fill, `{colors.bone}` type
- **Hover:** `{colors.key-hover}` fill, `{colors.mint}` type
- **Disabled:** `{colors.disabled}` fill, `{colors.mint-deep}` type (not opacity-only)
- **Focus:** 3px mint outline, 2px offset
- **Picker primary:** same treatment, full width of the picker window (“Start conversation”)

### Cards / Containers

- **Transcript window / picker:** `{colors.panel}` fill, 1px mint stroke, bevel, 4px radius. Transcript bar is 48px (wraps on small screens), uppercase mint-chrome, 16px chrome padding, mint rule under the bar. Well has a 32px cell grid at 18% mint.
- **Internal padding:** `{spacing.chrome}` (16px) in bars, wells, and picker
- **No nested cards.** Entries are lines on the cell grid, not wells, bubbles, or stacked headers.

### Inputs / Fields

- **Style:** 1px mint stroke, bevel, 4px radius, `{colors.input}` fill, 16px padding, min-height 56px
- **Legend:** “Message” sits on the top stroke (panel-colored notch), mint-chrome label type — a field legend, not a heading kicker
- **Placeholder:** mint-chrome mixed toward steel
- **Focus:** 3px mint outline; caret mint
- **Disabled:** native disabled on the textarea when connection is not ready
- **Identity select:** custom `Select` in the picker (`web/src/components/Select.tsx`). Legend “Identity” is a sibling `.picker-legend` in mint label type, not a wrapping `<label>` around the listbox. No native `<select>`.
- **Trigger:** same field chrome as the message input (`{colors.input}` fill, mint stroke, bevel, 4px radius, 56px min-height). Value is left-aligned with 16px padding; a 3rem chevron column on the right has a 35% mint divider and a centered clip-path triangle (mint-chrome) that flips 180° when open (`120ms steps(2)`, off under reduced motion). Subtle hover darkens the trigger to `#0a1820` without mint type.
- **Listbox:** absolutely positioned under the trigger with 8px gap; z-index 30; `{colors.panel}` fill, mint stroke, bevel, 8px inner padding, max-height 16rem, themed 8px scrollbar (track `#071016`, thumb mint-deep).
- **Options:** 16px marker column + 8px gap + label; selected row shows an 8px mint square marker and mint type; keyboard/mouse highlight uses `{colors.key-hover}` fill and mint type. Option labels use body weight (500), not key letter-spacing. Pointer-down commits the choice so trigger blur cannot cancel it.
- **Accessibility:** trigger is a `button` with `aria-haspopup="listbox"`, `aria-expanded`, and `aria-label="Identity"`; options are `role="option"` with `aria-selected`. Arrow keys, Enter/Space, Escape, Home/End supported.

**The Themed Listbox Rule.** Identity choice uses a custom listbox with field chrome and a panel overlay. The legend is a sibling of the control. Do not wrap the listbox in a native `<label>` and do not fall back to native browser select menus.

### Navigation

Sessions rail: beveled mint-stroked `{colors.panel}` column (`nav[aria-label=Sessions]`), uppercase chrome bar, compact New chat key, title + agent name/role lines, Ended/Archived labels in steel. Not a bubble inbox. Empty copy: “No sessions yet.”

### HUD

Wordmark, profile (`data-testid="profile"`), connection (`data-testid="connection"`), session identity line, presence plate. The connection node carries the single derived status: Connecting, Ready, Starting voice…, Listening, User speaking, Thinking, Agent speaking, Interrupted, Reconnecting, Connection failed…. Copy stays exact where tests depend on it. Status is type-first: mint for live labels, mint-chrome for Connecting/Reconnecting/Ended, persimmon for Interrupted and Connection failed. Mint glow applies only to live labels. Muted is on the Mute control, not a second HUD color.

### Transcript entries

Semantic list on the HUD row grid: 16px marker column, 16px gap, then one copy column. Same line grammar for both roles: marker, speaker (4ch min), 2px em-dash, bone body. Wrapped lines stay in the copy column. User is the plus marker with `You` and dash in bone; agent is the diamond with `{agent}` and dash in mint. Interrupted/failed as uppercase persimmon `<em>` at 13px label size, not color-only. Empty: “Send a message or start voice.” in steel, inset to the copy column. Rich blocks stay in the copy column as additional mint-labeled lines (Markdown, artifact `Artifact · id`, unknown fallback)—not nested cards or a second dock.

### Error banner

`role="alert"`. Persimmon type and stroke on `{colors.danger-well}`, 16px padding, full dock width. Errors must remain readable as text.

### Presence

Decorative ordered-dither plate in the HUD trailing column. 2.8s `steps(4)` contrast pulse when motion is allowed; still when reduced.

## Do's and Don'ts

### Do:

- **Do** keep HUD, transcript, picker, and dock on the shared inset/gap scale (40/16 desktop, 16/24 ≤879px).
- **Do** self-host Martian Mono; honor `prefers-reduced-motion`; keep body contrast ≥ 4.5:1 on field/panel.
- **Do** preserve testids `connection` and `profile`, and the accessible names Identity, Start conversation, Attach, Send, Voice, Cancel voice, Mute, Unmute, End.
- **Do** keep the labeled composer visible while voice is live until `/docs` and tests change together.
- **Do** render escaped main reply text; sanitize Markdown/reference blocks (no HTML injection); mark interrupted/failed in type.
- **Do** use the custom themed select for identity choice; keep the Identity legend as a sibling of the listbox; keep value text left-aligned and keys centered.

### Don't:

- **Don't** return to cream paper, Fraunces, Source Sans, or message bubbles.
- **Don't** use drop shadows, glass blur, smooth gradients, or a second type family.
- **Don't** fill keys or panels with mint; mint is the live line.
- **Don't** replace Voice / Mute / End with icon-only or emoji controls.
- **Don't** draw a fake inbox, attachment dock, or in-chat agent switcher.
- **Don't** hide status in animation or a color dot alone.
- **Don't** ship Google Fonts or a raster wordmark when the live type is Martian Mono.
- **Don't** use native browser select menus, wrapping `<label>` listboxes, or icon-only chevrons for identity pickers.
