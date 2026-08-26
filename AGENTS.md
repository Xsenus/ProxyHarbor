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

## Required checks for UI changes

1. Search the whole frontend for native or duplicate controls (`<select>`, `type="checkbox"`, `role="switch"`, listboxes and local button variants).
2. Update or extend the shared primitive instead of patching one page in isolation.
3. Add an interaction test covering the new state and keyboard behavior. Confirm that no unintended native control remains.
4. Run `npm run lint`, `npm test -- --run` and `npm run build` in `src/proxyharbor-web`.
5. Visually verify the affected control on the real page at desktop and mobile widths before considering the work complete.
