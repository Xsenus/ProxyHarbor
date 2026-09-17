# ProxyHarbor contributor guidance

## Interface consistency

ProxyHarbor has an established dark, mint-accented design system. Before adding or changing an interactive element, find the existing implementation in `src/proxyharbor-web/src` and reuse it. Do not introduce a parallel visual treatment for the same control.

- Use `StyledSelect` from `components/StyledSelect.tsx` for every single-choice dropdown. Do not render a visible native `<select>`: its expanded menu is controlled by the browser or operating system and breaks the ProxyHarbor theme. Extend the shared component when a new requirement appears.
- Use the shared `ui-checkbox-input` + `ui-checkbox-mark` pattern for checkbox choices. The native input must remain in the accessibility tree, while the visible mark must have explicit SVG width and height. Do not rely on browser `accent-color` or a visible native checkbox.
- Use the existing `Toggle` pattern for boolean on/off settings presented as switches. Preserve `role="switch"`, `aria-checked`, keyboard focus, fixed track/knob geometry, and the existing danger variant.
- Reuse the existing button classes (`primary-admin-button`, `secondary-admin-button`, `icon-button`, `table-action`) and established input, modal, pagination, tabs, notice, badge and table styles before adding CSS.
- Use existing CSS variables, colors, radii, spacing and typography. New controls must provide hover, active, focus-visible, disabled, loading and error states where applicable.
- Icon geometry must be explicit: set both width and height, keep icons centered, and prevent flex shrinking. Lucide defaults must never determine a compact control's final dimensions.

## Interaction and accessibility

- Interactive controls must have an accessible name and correct semantic state (`aria-expanded`, `aria-selected`, `aria-checked`, `aria-current` as applicable).
- Dropdowns must support pointer interaction, Arrow Up/Down, Home/End, Escape, Tab and focus restoration. Modals and filters must remain operable without a mouse.
- Preserve a visible `:focus-visible` state. Do not remove outlines unless an equivalent branded focus indicator is supplied.
- Verify desktop and mobile layouts. Menus must not be clipped by their container, overflow the viewport, or appear behind cards and headers.

## Risk-based verification

Use the smallest sufficient verification set while developing, based on the files changed, the affected modules, public-contract impact, and security, financial, or data risk. Do not run every frontend, backend, PostgreSQL integration, end-to-end, migration, or Docker check after each small edit when that area cannot be affected.

- Run focused checks during iteration, then batch the applicable full checks once after the last relevant change and before push, PR creation, publication, deployment, or handing off a result as final. A local intermediate commit alone does not require repeating the full suite.
- Do not repeat an already successful full check unless related code, configuration, dependencies, migrations, tests, or build inputs changed afterward.
- Before starting a long check, inspect the current diff and state which checks were selected and why.
- If the user explicitly asks to defer tests during a series of small edits, defer the full run until final verification, commit-and-push, PR, publication, or deployment. Still run prompt focused checks when a high-risk change would otherwise be unsafe to leave unverified.
- Never delete or skip tests, weaken coverage or security thresholds, or bypass migration and build gates to make verification faster.

### Change risk

**Low risk** includes documentation, copy, icons, static images, colors, spacing, sizing, alignment, CSS-only responsive adjustments, and moving a control without changing its handler. During development, use only applicable focused checks: an existing related component test, lint/formatting for changed files, a type check when typed code changed, and visual verification at the affected viewport. Do not run backend tests for a purely visual frontend change.

**Medium risk** includes component state, event handlers, form validation, filtering, sorting, pagination, calls to an existing API without a contract change, and a contained backend service change without a public-contract change. Run the related Vitest, xUnit, component, or service tests and build the affected part when compilation or bundling is relevant. Save the applicable full area suite for the final gate.

**High risk** includes payments, balances, pricing, subscriptions and rewards; authentication, authorization, roles and protected data; API contracts; database schema and migrations; imports, exports, backup/restore and external integrations; collectors, validators and other background workers; concurrency, leases, transactions, caching and distributed validation; dependency updates; and shared UI primitives or infrastructure used broadly. Run focused extended tests immediately, including important success and failure paths. Before push or final delivery, run every applicable full suite and specialized project gate for the affected areas.

### Checks by affected area

- **Frontend only, no API contract change:** during development run the related test file with `npm test -- <test-file>` from `src/proxyharbor-web`, and use ESLint on the changed typed files when useful. Before the final gate run `npm run lint`, `npm test`, and `npm run build` once. Do not run backend tests solely for this change.
- **Backend only, no frontend contract change:** during development run focused tests with `dotnet test tests/ProxyHarbor.Tests/ProxyHarbor.Tests.csproj --filter <filter>` and build the changed project when needed. Before the final gate run `dotnet build ProxyHarbor.slnx -c Release`, `dotnet test ProxyHarbor.slnx -c Release --no-build`, and `dotnet format ProxyHarbor.slnx --verify-no-changes --no-restore` once. Do not run frontend tests solely for this change.
- **API contract or frontend/backend interaction:** test both producer and consumer during development; run the full applicable frontend and backend suites at the final gate.
- **Database or migrations:** add the EF Core migration, run focused persistence/integration tests, and verify `dotnet ef migrations has-pending-model-changes` as documented in `CONTRIBUTING.md`. Use the PostgreSQL-backed suite when production database behavior is involved.
- **Docker, deployment, backup/restore, workflows, feeds, security, or release metadata:** run the matching contract, audit, Compose, restore-drill, actionlint, documentation, or security scripts listed in `CONTRIBUTING.md` and CI. Run broad Docker and end-to-end scenarios only when the change can affect them or as part of the final publication/deployment gate.
- **Documentation or `AGENTS.md` only:** do not run application tests. Check the diff, Markdown structure, changed links, and the relevant documentation contract/link scripts when applicable.

CI remains the mandatory full barrier before publication or deployment. Keep the existing build, test, coverage, dependency audit, migration, documentation, security, Docker, integration, and release gates intact. If CI fails, diagnose the exact failing step, fix the cause, and rerun the relevant local check before relying on CI again.

## Required checks for UI changes

1. When adding or changing an interactive control, search the whole frontend for native or duplicate controls (`<select>`, `type="checkbox"`, `role="switch"`, listboxes and local button variants).
2. Update or extend the shared primitive instead of patching one page in isolation.
3. When behavior, state, or keyboard interaction changes, add or update an interaction test covering it. Confirm that no unintended native control remains.
4. During iteration, run the related frontend test and the smallest applicable lint, type, or build check. Before the final gate, run `npm run lint`, `npm test`, and `npm run build` once in `src/proxyharbor-web`; do not rerun them after purely unrelated changes.
5. Visually verify the affected control on the real page at desktop and mobile widths before considering the work complete.
