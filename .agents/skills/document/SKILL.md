---
name: document
description: "Create, review or update Agent Core specifications, architecture decisions and documentation consistency. Defaults to documentation only."
---

# Document

Read root [AGENTS.md](../../../AGENTS.md) and [README.md](../../../README.md).

Default mode is docs-only. Do not create application source, solutions/projects, package manifests, migrations, deployment configuration or generated implementation artifacts unless explicitly requested. [Milestone 0](../../../docs/18-implementation-plan.md) is Markdown plus small repository agent/tooling configuration. An explicit request to maintain agent instructions/skills permits that tooling work without starting Milestone 1.

1. Determine review, decision or edit intent. Reviews report findings; they do not automatically rewrite documents or implement code.
2. Use [docs-consistency](../docs-consistency/SKILL.md) to identify canonical owners and affected references.
3. Read [architecture](../architecture/SKILL.md) for technical decisions. Load only relevant specialists: [backend](../backend/SKILL.md), [frontend](../frontend/SKILL.md), [realtime](../realtime/SKILL.md), [providers](../providers/SKILL.md), [persistence](../persistence/SKILL.md), [operations](../operations/SKILL.md), or [testing](../testing/SKILL.md). Their guidance informs the spec without changing this working mode.
4. Check duplicate ownership, conflicting decisions, stale terminology, broken references and roadmap/milestone/implementation drift. Inspect code only when it exists and is relevant to a claim.
5. For edits, update the canonical owner first and propagate only necessary summaries/references. Record approved decision changes in Technology Decisions and reflect milestone implications without inventing completion.
6. Verify README and the implementation plan remain aligned. Run docs-consistency checks; application tests are unnecessary for docs-only changes.
7. Report changed documents or review findings, decisions changed, inconsistencies fixed, checks and implementation implications.
