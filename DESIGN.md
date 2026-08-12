# Design Context

This file is the implementation contract for extending the existing console.
Preserve established components and behavior before adding a new visual pattern.
Product intent and risk language live in [`PRODUCT.md`](PRODUCT.md).

## Visual Register

The visual register is an operational security console: quiet, precise, dense,
and approachable. It uses warm ink-green neutrals and restrained teal accents to
feel trustworthy without looking clinical or militarized.

- Treat the current console as the primary visual reference.
- Optimize for scanning and repeated action, not presentation screenshots.
- Keep desktop information density high and mobile controls comfortably
  touchable.
- Let domain data, status, and next actions create hierarchy. Decoration should
  not compete with them.
- Use cards to frame metrics, bounded tools, or coherent panels—not as the
  default wrapper for every section.
- A planned dark theme should extend the upstream brand's ink-panel treatment
  across the console without changing its density or information hierarchy.

DMARC products such as dmarcian and EasyDMARC are information-domain references,
not visual templates. Do not import their marketing surfaces or invent a second
brand for the MTG fork.

## Typography

Fonts are self-hosted through Fontsource; do not add a CDN dependency.

| Use | Family | Existing token |
| --- | --- | --- |
| Page and panel headings, large metrics, brand wordmark | Space Grotesk | `font-display` / `--font-display` |
| Body copy, controls, navigation, tables | Public Sans | `font-body` / `--font-body` |
| Domains, IPs, policies, keys, versions, identifiers | JetBrains Mono | `font-mono` / `--font-mono` |

The base UI is 14px with a 1.55 line height. The existing scale is 12, 13, 14,
15, 18, 22, 28, 36, 48, and 60px. Authenticated console pages normally use a
22px display heading, 15px panel headings, 13–14px supporting copy, and 12px
labels. Larger display sizes are for exceptional branded surfaces, not routine
console pages.

- Use sentence case for headings, buttons, labels, and badges.
- Use tabular numbers for aligned metrics.
- Use monospace selectively for technical values, never for paragraphs.
- Allow domains and other unbreakable identifiers to wrap or truncate with an
  accessible full-value path; never let them force the viewport wider.

## Color

Use semantic aliases from `src/web/src/index.css` and `tailwind.config.js`.
Avoid new literal colors in page code.

### Core surfaces and text

| Role | Token | Current value |
| --- | --- | --- |
| Page background | `--surface-page` | `#f5f8f7` |
| Card/dialog surface | `--surface-card` | `#ffffff` |
| Recessed surface | `--surface-sunken` | `#eef3f1` |
| Dark ink surface | `--surface-ink` | `#0b1d18` |
| Body text | `--text-body` | `#101f1c` |
| Secondary text | `--text-secondary` | `#54685f` |
| Faint text | `--text-faint` | `#5c6f67` |
| Default border | `--border-default` | `#e3eae8` |
| Strong border | `--border-strong` | `#cfdad6` |

### Brand and interaction

| Role | Token | Current value |
| --- | --- | --- |
| Primary action/link | `--brand` | `#0c7568` |
| Hover | `--brand-hover` | `#0b5d54` |
| Active | `--brand-active` | `#0a4a44` |
| Subtle selection | `--brand-subtle` | `#effaf7` |
| Focus ring | `--brand-ring` | `rgba(14, 148, 129, 0.28)` |

`--brand` is intentionally dark enough for white button text. Do not revert a
primary button to teal-600. Teal-600 remains appropriate for non-text chart
fills, dots, and borders.

### Planned dark theme

Dark theme is planned, not implemented. The canonical brand reference is the
upstream [brand page](https://dmarc-analyzer.net/brand/). It defines ink green
for dark panels, mint for accents and buttons on those panels, and a reversed
light wordmark. The live brand-site stylesheet currently supplies the supporting
on-ink tokens below.

| Dark role | Upstream source token | Current value |
| --- | --- | --- |
| Base ink surface | `--surface-ink` / ink green | `#0b1d18` |
| Raised ink surface | `--surface-ink-raised` | `#0e2620` |
| Primary text on ink | `--text-on-ink` | `#e8f2ee` |
| Muted text on ink | `--text-on-ink-muted` | `#8fa8a0` |
| Border on ink | `--border-on-ink` | `rgba(255, 255, 255, 0.12)` |
| Primary dark-panel action | mint | `#3ae0b0` |
| Reversed wordmark | logo artwork | `#f0fdfa` |

The brand site is light overall and uses these values for terminal, CTA, footer,
and reversed-logo panels. It does not define a complete dark application theme.
Treat this table as the source palette, not permission to guess the remaining
semantic mappings.

The dark-theme design slice must:

- map page, card, sunken, overlay, hover, selected, and disabled surfaces from
  the existing ink/teal scales;
- define body, secondary, faint, link, border, and focus tokens with WCAG 2.2 AA
  contrast evidence;
- create dark variants for every success, warning, danger, and neutral
  foreground/background/dot triplet rather than reusing light tinted
  backgrounds;
- remap chart grids, table headers, row hover, code blocks, inputs, dialogs,
  backdrops, and shadows so depth does not depend on a barely visible shadow;
- use mint with ink text for primary actions on dark surfaces and mint for the
  focus indicator; do not place white text on mint;
- use the reversed `BrandLogo` treatment on a dark shell while preserving the
  shield gradient exactly; and
- verify the same loading, empty, error, partial, selected, disabled, keyboard,
  mobile, and high-risk states in both schemes.

Prefer semantic custom-property overrides at the theme root so existing
components inherit the scheme. Do not add per-page `dark:` class patches or a
parallel component set. Theme selection behavior—system preference, persisted
user choice, or both—belongs to the implementation slice and is not decided by
the brand palette.

Before implementation, recheck the canonical brand page because the website is
upstream-owned and may evolve independently of this fork. Avoid pure black,
generic charcoal, blue-purple gradients, neon teal overuse, and a dark sidebar
attached to otherwise unmapped light content.

### Semantic state

Use the existing foreground/background/dot token triplets:

- success/healthy/enforced: `--status-ok-*`;
- warning/ramping/needs review: `--status-warn-*`;
- danger/failure/critical: `--status-danger-*`; and
- neutral/inactive/no data: `--status-neutral-*`.

Every semantic color needs a text label, icon, position, or other non-color cue.
Do not use green to mean merely "enabled" when the state is not evidence of
health. The only decorative gradient is the established shield brand mark.

## Spacing, Shape, And Elevation

The spacing scale is 4, 8, 12, 16, 20, 24, 32, 40, 48, and 64px. Prefer these
values over one-off spacing.

- Main content uses 16px horizontal padding on phones, 24px at `sm`, and 32px at
  `lg`, with a current maximum content width of 1040px.
- Page headings sit about 20px above primary content.
- Related panels and metric tiles normally use 14px gaps.
- Cards use 20px internal padding unless a table or composed section owns its
  edge.
- Controls use 6–10px radii; cards and dialogs use 14px. Pills are reserved for
  badges and compact segmented status.
- Use `shadow-card` for bordered panels, `shadow-raised` sparingly, and
  `shadow-overlay` for dialogs. Avoid floating layers and heavy shadows in the
  normal page flow.

## Shell And Navigation

Desktop (`lg`, 1024px and wider) uses the established 230px sticky sidebar and
the groups **Overview** and **Manage**. Keep information architecture based on
operator intent rather than backend service boundaries. Filter navigation by
role so a user does not land on a page that can only return an authorization
error.

Below `lg`, navigation becomes the existing 280px off-canvas drawer with a top
bar, backdrop, Escape handling, body scroll lock, focus transfer, and focus
return. Hidden navigation must be removed from the tab order.

Keep the signed-in identity, sign-out action, and running version in the sidebar
footer. Version links must resolve to the matching release or commit; unknown
and local builds remain plain text.

Account-wide service API keys belong under administrator-only Settings. Keep
source-scoped report-upload keys on the report source whose routing they control.

## Page Hierarchy

Use the established page pattern:

1. A compact page heading and one-line evidence-oriented subtitle.
2. Time window, refresh, search, filters, or one primary action aligned opposite
   the heading on wider screens and stacked on narrow screens.
3. Persistent feedback immediately below the header.
4. Summary metrics only when they help choose the next action.
5. The primary list, investigation panel, or form.

Keep analytics windows in the URL. Preserve filters or expanded source state in
the URL when a view is worth linking, bookmarking, or returning to.

The Dashboard first viewport keeps the current priority: monitored domains,
compliance, analyzed messages, spoofing blocked, reporting-window context,
mailbox health, and domains needing attention.

## Components

### Buttons and actions

Use the existing `Button` variants. One primary action per local decision area
is enough. Secondary actions use bordered or ghost treatments; destructive
confirmation uses `danger`.

- Include a text label for primary and destructive actions.
- Use Lucide icons as supporting cues, normally 14–16px.
- Light-theme teal actions darken on hover. Dark-theme mint actions may brighten
  within the existing mint scale; focus uses the theme's high-contrast ring.
- Disabled controls keep their label visible, use 50% opacity, and cannot fire.
- Do not hide essential actions behind an ellipsis solely to make a row cleaner.

### Forms

Use `Input`, `Select`, native checkbox/radio controls, and existing field-label
patterns. Labels remain visible; placeholders are examples, not labels. Put
short validation or recovery guidance beside the field or in a persistent
notice.

Use native platform controls before adding a dependency. Keep technical input in
monospace where recognition matters. On screens narrower than `sm`, text inputs
and selects stay at 16px and 40px high to avoid iOS zoom; button targets approach
44px.

Create and edit flows normally use a dialog when the form is bounded and the
underlying list provides context. Use a full page for recovery, multi-stage,
investigation, or other workflows whose consequences need room.

### Tables and lists

Tables are the default for operational collections. They should support the
smallest useful set of search, filters, and sortable columns.

- Put the primary identifying value first.
- Stack closely related secondary identity, such as client beneath domain, when
  it prevents unnecessary width.
- Right-align numeric values and actions; use tabular numbers.
- Keep technical values monospace.
- Use compact row height and subtle hover, not zebra stripes.
- Preserve meaningful group order; do not add headings for single-item groups.
- Wrap every table in horizontal overflow protection on small screens.
- A row that navigates must expose a semantic link or button to keyboard and
  assistive-technology users; pointer-only row handlers are insufficient.
- Empty results must distinguish no data from no matches for current filters.

### Cards and panels

Use `Card` and `CardHeader` for a coherent metric, chart, repeated item, or tool.
Do not nest cards for visual interest. A page containing a single primary table
usually needs one card, not a card per row.

Stat cards use a small secondary label, a large display value, and at most one
compact badge or delta. They must tolerate long values and two-column mobile
layouts without overflow.

### Dialogs and high-risk actions

Use the Radix-based `Dialog` primitive for focus management and accessible
labelling. Existing dialogs cap at `calc(100dvh - 2rem)`, scroll internally, and
keep 16px mobile / 24px desktop padding.

Before a high-risk mutation, state:

- the exact object and scope;
- the immediate consequence;
- whether credentials or access stop working;
- whether data is deleted or merely deactivated; and
- the available recovery path.

Avoid confirmation theater for routine reversible edits.

### Badges and policy values

Badges communicate compact state and always contain text. Use `PolicyBadge` for
`p=none`, `p=quarantine`, and `p=reject`; use monospace rather than translating
the underlying policy into marketing language. Keep policy separate from
enforcement status and compliance.

### Data visualization

Reuse `StatCard`, `ComplianceBar`, `TrendChart`, and existing disposition
visualizations before adding a chart package.

- Prefer compact comparison over decorative charting.
- Teal represents compliant/healthy volume; danger red represents failing
  volume; amber signals a review threshold.
- Keep gridlines faint and labels sparse but sufficient.
- A chart must have a nearby textual value, legend, table, or accessible summary
  that communicates the same decision-relevant result.
- Analytics windows anchor to the newest report date when the API says they do;
  the interface must not imply the data is current to today.
- Do not add gauges, 3D charts, pie charts with many segments, or animation that
  delays reading.

## Interaction States

### Loading

For initial page loads, use a centered 24px spinner or a stable reserved panel.
For refreshes, preserve existing data and reduce opacity rather than replacing
the whole page. Disable controls that would create overlapping requests.

### Empty

Name the missing object and explain the next useful action. Staff may receive a
scoped setup link; read-only viewers should not see an action they cannot take.
Use a quiet icon only as a supporting cue.

### Error

Use a persistent inline danger notice near the action or page header. Preserve
the server's useful configuration error when it is safe to display. Explain
whether existing data is stale, unavailable, or unchanged, and offer a retry
when retrying is meaningful. Do not use transient toast-only errors for
operational results.

### Success

Use a persistent success notice for exports, imports, tests, key actions, and
other results the operator may need to record. Include the affected object or
filename. Routine saved form state may close the dialog and refresh the list
when the changed row is self-evident.

### Partial and stale

Partial success is a first-class result: show created, updated, skipped,
conflicted, not delivered, or requiring manual repair separately. When a live
lookup fails but last-known evidence remains, label both the failure and the
provenance of the retained value.

## Motion

Motion is functional and restrained:

- 120ms for color, focus, and compact control transitions;
- 200ms for drawer movement, chart width, and larger state changes;
- the existing `cubic-bezier(.2,.8,.3,1)` ease-out curve; and
- only `transform` and `opacity` for layout movement where possible.

Do not use parallax, looping motion, count-up metrics, pulsing status, or animated
decoration. A loading spinner is the exception. Respect `prefers-reduced-motion`
by removing nonessential transitions and ensuring no meaning depends on motion.

## Accessibility

- Meet WCAG 2.2 AA contrast. The current secondary and faint text tokens were
  chosen to clear text contrast on page and card surfaces; do not lighten them
  for aesthetic reasons.
- Every interactive element needs a visible focus state and a semantic control
  or link.
- Dialogs need a title; icon-only controls need an accessible name.
- Preserve logical heading order and DOM reading order when layouts reflow.
- Use `aria-sort`, `aria-pressed`, `aria-expanded`, and live/alert semantics only
  for the state they actually represent.
- Danger notices may interrupt with `role="alert"`; routine success and warning
  notices should not.
- Never rely on color, hover, placeholder text, or motion alone.
- Keep repeated touch targets near 44px on mobile and prevent hidden drawers or
  overlays from retaining focus.

## Responsive Rules

The existing console uses one structural breakpoint at `lg` (1024px). Add
smaller breakpoints only for local layout pressure, not a second information
architecture.

- Stack page headers and action rows on narrow screens.
- Make search and filters full-width before allowing overlap or clipped labels.
- Let cards collapse to one column; metric cards may remain two-up when values
  fit.
- Tables may scroll horizontally. Do not hide decision-critical columns without
  providing an equivalent detail view.
- Dialogs must fit the dynamic viewport and keep their submit/cancel actions
  reachable with the on-screen keyboard open.
- Test long domains, large counts, translated browser chrome, and 320px-wide
  viewports for overflow.

## Performance And Assets

- Reuse `public/logo.svg`, `public/favicon.svg`, and `BrandLogo`; do not redraw
  the mark or change its established teal gradient.
- Use `lucide-react` through the existing `Icon` wrapper. Do not mix icon sets.
- Keep fonts local and subset imports to weights the app uses.
- Prefer CSS, native controls, and dependency-free SVG for small visualizations.
- Do not add a UI or chart library until existing primitives demonstrably cannot
  express the required behavior.
- Avoid fetching slow live evidence in a way that blocks the primary analytics
  render; show independently loaded panels independently.

## Implementation Notes

The current frontend stack is React 19, TypeScript strict mode, Vite 8,
Tailwind CSS 3, CSS custom properties, shadcn-style local primitives,
Radix Dialog, Lucide icons, and Fontsource. The primitive set lives under
`src/web/src/components/ui`; data-oriented components live under
`src/web/src/components/data`.

Before creating a component, search those directories and the existing pages.
Extend a primitive only when the behavior belongs everywhere it is used; keep
domain-specific presentation near the domain workflow.

Minimum verification for a UI change:

```bash
cd src/web
npm test
npm run lint
npm run build
```

For user-facing behavior, also run the real stack, inspect desktop and mobile
layouts, and verify loading, empty, error, disabled, success, keyboard, and
overflow states. Follow the repository's `AGENTS.md` for broader backend,
ingestion, and PostgreSQL checks when a UI change crosses those boundaries.

## Forbidden Patterns

- purple-blue AI gradients, decorative blobs, glow, glassmorphism, or starfield
  backgrounds;
- oversized hero copy inside the authenticated console;
- card grids used where one sortable table answers the question;
- an all-teal palette that makes status and action indistinguishable;
- emoji, novelty illustrations, and generated stock people;
- placeholder dashboards that obscure real empty states;
- new primitives that duplicate an existing component;
- raw hex colors and one-off spacing in page components;
- silent auto-refresh that moves rows while an operator is acting;
- hover-only disclosure, inaccessible clickable rows, or icon-only destructive
  actions; and
- optimistic security claims without source, time, scope, and failure context.
