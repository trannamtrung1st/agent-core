---
name: Agent Core
description: Deep navy Ant Design v6 working environment for conversation, creation and administration.
colors:
  primary: "#245fbe"
  primaryBg: "#1b3455"
  linkText: "#91bdff"
  focus: "#b9d5ff"
  text: "#eaf1fa"
  textSecondary: "#b1c1d7"
  textTertiary: "#98abc4"
  layout: "#0b1322"
  container: "#121e30"
  elevated: "#1b2b42"
  border: "#374b66"
  borderStrong: "#657d9c"
typography:
  headline:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'Noto Sans', sans-serif"
    fontSize: "28px"
    fontWeight: 600
    lineHeight: 1.3
  body:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'Noto Sans', sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5714285714
  label:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'Noto Sans', sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.4
rounded:
  control: "8px"
  surface: "10px"
spacing:
  tight: "4px"
  compact: "8px"
  default: "12px"
  section: "16px"
  controlInner: "8px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "#ffffff"
    rounded: "{rounded.control}"
    height: "32px"
  composer-shell:
    backgroundColor: "{colors.elevated}"
    textColor: "{colors.text}"
    padding: "{spacing.compact}"
  selected-navigation:
    backgroundColor: "{colors.primaryBg}"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
---

# Design System: Agent Core

## Overview

One deep navy working environment across conversation, creation and administration. The owner-selected navy direction contributes quiet navy grounds, cool white text, readable blue links and precise contextual identity. Standard task layouts and native controls carry interaction. `/docs` owns behavior; this guide describes presentation. Acceptance belongs to the delivery ledger.

`web/src/app/antdTheme.ts` owns colors/controls. The dark algorithm explicitly resolves primary seed `#245fbe` to `#245fbe`; links/surfaces and readable placeholder aliases are explicit. AppShell projects resolved tokens into `--ac-*` variables on the shell/document root, including portals. Never create another page token source.

## Colors

Blue links and focus contrast with navy grounds. Layout/container/elevated surfaces establish hierarchy. Error/warning/success retain Ant Design status palettes and text/icon meanings. Native Office/document canvases retain white presentation within the dark frame.

## Typography

Use the existing AntD system sans and scale, without a font dependency. Task H1 is 28px wide/24px narrow. Branding is text, not a heading. Secondary owner identity is 16px with lifecycle/version context. Body 14px, labels 12px, explanatory measure near 72ch. Code retains the existing monospace renderer; inspection identifiers wrap.

## Layout

AdminShell owns the 248px rail, task heading, scope trail, page insets and native navigation Drawer. Rail switches to Drawer below 1024px; page insets are 32px wide/16px narrow. Native details/summary groups disclose contextual destinations. Tables own intentional local overflow. Drawer bodies scroll without hiding actions.

Parent gaps use 8/12/16px compact/ordinary/section roles. The additive inset rule retains 8px native inner text-control padding inside compact composer and Model overlay shells. Do not zero text-button padding or double outer insets. Narrow header/tool hits are 40px. Chat retains a bounded reading/composer column and labeled tool/context disclosures. Workflow palette is a collapsed full-width disclosure above structure/properties; Outline supports every edit without dragging. Settings groups retain controller-owned drafts.

## Elevation & Depth

Native AntD portal elevation and tonal surfaces. The existing bounded composer shadow remains. No ornamental glow/glass/backgrounds or nested-card system. Visible focus remains independent of borders.

## Shapes

Control radius seed is 8px; resolved large surfaces use 10px. Existing message/composer/file silhouettes retain their product role. Use tables, sections and dividers directly instead of nesting cards.

## Components

Consume native AntD primitives directly. Reuse AdminShell, AdminPageHeading/AdminOwnerHeading, AgentConfigurationPanel/Layout, AdminCollectionToolbar, useAdminDetailLayout, SessionSandboxFrame, ChatFileCard and FilePreview. Keep their inset, lifecycle, confirmation, save/cancel, per-field inheritance/reset, dirty-guard and uncertain-outcome owners. Tools has named actions and stable opener restoration. Scope menus distinguish organization/account/platform. Text selection and scrollbar roles come from shared tokens. Custom Chat/Workflow motion and scrolling respect reduced-motion CSS; short native AntD transitions remain library-owned. Primary hover is `#2867cc`; its white labels measured 5.3815:1 in the browser, with normal/hover/active contrast guarded by theme tests.

## Do's and Don'ts

- Do inspect actual desktop/tablet/mobile and long/empty/error variants, including shared consumers.
- Do preserve native semantics, meaningful labels, authority distinctions and protected previews.
- Do record visual review separately from interaction proof.
- Don't add another UI kit, generic wrappers, per-page palette or decorative theme switch.
- Don't infer permission, availability or completion from styling or screenshots.

## Structured settings

AgentConfigurationPanel owns cohesive section boundaries; AgentSettingRow owns compact divided descriptive rows and bounded trailing controls. Scalar settings use two columns only when the container permits (34rem); narrow rows stack in reading order. Instructions, scripts, complex lists, budget matrices and native auth forms retain full-width editors. Footers stay adjacent to their section. Do not add autosave, nested cards or a generic settings renderer. Nav groups use 14px semibold labels and a 12px chevron gap; semantic icons follow the shared registry map. Chat groups Agent/retention and keeps option metadata outside the short selected name. Tables use meaningful columns and labeled overflow actions.
