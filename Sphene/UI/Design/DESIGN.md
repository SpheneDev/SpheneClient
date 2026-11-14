# Sphene Design System

`SpheneUI` is the single entry point for all shared UI drawing in SpheneClient.
It replaces the previous split between `UiSharedService` helpers,
`SpheneDesign`/`SpheneColors` tokens, `SpheneWindowChrome` and
`SpheneCustomTheme` scopes - those entry points no longer exist.

The facade is a `static partial` class spread over topic files in
`UI/Design/`. Every window except **CompactUI** uses it; CompactUI keeps its
own styling system (`SpheneUI.Theme.CompactScope()` + `Compact*` tokens) until
it gets a dedicated design pass.

```
SpheneUI
├─ Theme        WindowScope / CompactScope / TooltipScope / ContextMenuScope,
│               AccentScope, Current
├─ Spacing      fixed grid + semantic aliases (Space token type)
├─ Palette      static brand colors (crystalline blue/purple/gold)
├─ Colors       theme-resolved semantic colors (TextMuted, FrameBg, Accent,
│               AccentRamp…)
├─ Metrics      control metrics (ControlHeight, ControlRadius, SwatchSize,
│               RowHeight, GridMinCell…)
├─ Density      Compact/Balanced/Comfortable profiles, Factor, AutoWindowScope
├─ Text         Plain/Wrapped/Colored/ColoredWrapped/Muted/Big/Bullet/Outlined/
│               Display (crisp ~26px headings via the DisplayFont handle -
│               never SetWindowFontScale)/SectionHeader/Header/GroupedCentered/
│               PageIntro (one-line muted blurb: ellipsized, full text on
│               hover - for pages whose title lives in the breadcrumb)/
│               Timestamp/TimestampRight/FormatDate*/Ellipsize/WrapLines
│               (Text.PageHeader is superseded - use Layout.PageHeader)
├─ Icons        Icon/BoolIcon/Button/TextButton/ActionButton + sizing
│               (Size, PushFont, ButtonSize, TextButtonWidth)
├─ Controls     Toggle/Switch/ToggleChip/Checkbox/SmallButton/Slider*/Drag*/
│               Input*/Combo/LabeledCombo/BeginComboBox/Button/SegmentButton/
│               Segmented/PrimaryButton/HeroButton/IconButton/DialogButtons/
│               Selectable/CollapsibleSection/FieldRow/SegAutoWidth/
│               ColorEdit/ColorSwatch/GlyphSwatch/FrameSwatch/TagChips/
│               TransparentSwatch/StatusDot/ProgressBar/DockToCardEdge/
│               TransmissionBar/HelpText/InputTextWithButtons (+InputButtonSpec)
│               /BoundedAvailX/FillRowWidth/DockToRightEdge/SameLineIfFits
├─ Rows         Toggle/Switch/Segmented/Combo/Slider*/Input/Nav/Value/Action/
│               Note/StatusPill - group-aware settings rows
├─ Layout       AccentCard/SectionCard/Surface/SurfaceFlat/RowGroup/CardGrid/
│               HeadedRowGroup/HeadedSurface/CardHeader/HeaderBodyGap/
│               SettingsGroup/ContentRegion/ScrollAreas(Begin/End/FillToBottom)/
│               BeginPane/TabStrip/VerticalTabStrip/VerticalTabNav/
│               NavRail(BeginNavRail[embedded]/NavRailItem/NavRailSegmentItem/
│               NavRailCategory/NavRailCategoryLabel/NavRailDivider/
│               EndNavRail/BeginNavContent[scrollable]/EndNavContent/
│               NavRailWidth/NavRailSegmentedWidth/NavRailPadX/
│               PinnedSectionLabel)/Separator/SectionSeparator/
│               DistanceSeparator/ScrollAreaSeparator/Section/Callout(+title)/
│               Badge/Group/Expander(Begin/End/Toggle/SetOpen/IsOpen/
│               ResetExpanders)/Collapsible/Tree/ActionBar/VerticalSpace/Space/
│               HorizontalSpace/IndentScope/FlowIndentTo/FlowUnindent/
│               PageHelp/PageHeader/RegisterPageHelp/EnsurePageHelp/HelpExtra/
│               ScaledNextItemWidth/ScaledSameLine/WindowContentWidth/
│               FillToViewBottomHeight/CenterNextWindow/SetScaledWindowSize/
│               SlotIcon/IconPlaceholder/FeatureTile/EmptyState/SuppressBound
├─ Buttons      Row(id, params Spec[])/Spec - non-clipping toolbar rows
├─ Tables       Begin/Column/HeaderRow/HeaderFitWidth/BeginRow/Row/SetColumn/
│               SelectableRow/SelectableRowSpan/Cell*/EmptyRow/PathCell
├─ Lists        Begin/Item/EmptyHint - bordered pick lists
├─ Fx           HoverT/HoverRow/HoverLift/Sheen/Constellation/RailMotes/
│               AmbientGlow/Reset - micro-animations
├─ Chrome       TitleBar/Collapse/WindowBlur/ThemedWindowBlur/CrumbPill/
│               FitToWorkArea/WorkAreaMax/ScrollFades
├─ Popups       BeginStyled/BeginModalStyled/BeginContextStyled/EndStyled/
│               BeginDropdown/ToggleDropdown/EndDropdown/CloseDropdown/
│               BeginFlyout/ToggleFlyout/EndFlyout/IsFlyoutOpen/CloseFlyout/
│               ResetDropdowns
├─ Media        Texture cache/Animated/Image/ImageInRect/Sprite/SpriteInRect/
│               Avatar/Placeholder/ImageBackground + ImageFit (Fit/Cover/
│               Stretch)/GameIcon (shared in-game UI icons via ITextureProvider
│               GetFromGameIcon - safe every frame, returns wrap or null)/
│               FillRoundedGradient/PresenceDot
├─ Profile      Banner/Hero/HeroSkeleton/HeroEdit/StatTile/ThemeColorRow/
│               NsfwChip*/RowFx/FxChips/FriendRow/RowImageStrip/RowBackdrop
├─ ContentBounds right/left flow bounds (pushed by cards, scroll areas,
│               ContentRegion) - see "Content bounds"
└─ Lint         runtime design checker (see "Design violations")
```

`SpheneUI.AttachTooltip` sits on the facade root and handles `--SEP--`
(`SpheneUI.TooltipSeparator`) segment splitting.

## Design principles

Sphene is a **desktop** Dalamud application - the principles below are
canonical; the detailed pattern notes in the rest of this file implement
them.

- **Desktop-first layout.** Use horizontal space: side rails, multi-column
  regions, right-docked panels and inline previews wherever width allows.
  Stack vertically only when *measured* width requires it - the approved
  pattern is "compute whether the side element fits, fall back to inline"
  (see the hero bio panel). `Density` adapts spacing and metrics to window
  size; it never changes a page's layout structure.
- **Structural chrome stays put; decorative chrome scrolls.** Rails,
  header bands, tab strips and action bars are structural - pin or dock
  them. Banner art, heroes and previews are decorative - they scroll with
  content or retract into a compact strip (the community page's collapsing
  toolbar). Not every hero is sticky.
- **Content never overlaps the nav rail.** Flow code that repositions
  wrapped lines must re-anchor the line origin - `FlowIndentTo`/
  `FlowUnindent`, not bare `SetCursorScreenPos`.
- **One primary heading per surface.** A card's integrated header is its
  title; a rail page is identified by rail selection + breadcrumb. No
  duplicate in-body page titles, no oversized body headings repeating a
  card title.
- **Help is a "?" flyout at a consistent spot** - on the nav item for
  rail pages, docked in `Layout.PageHeader` for standalone windows, never
  a dedicated content row and never inside breadcrumbs. See "Page help".
- **Restraint over decoration.** Tonal fills, hairlines and theme accents;
  icon plates only on tiles/markers; effects opt-in per element. New
  decoration needs a reason - removing decoration does not need a
  replacement.

## Buttons

Every push button renders through `Controls.SegCore` - the shared segmented
renderer in the nav-menu language: a tonal shape (corner radius capped at
25 % of the button height so short buttons stay button-shaped, never full
pills) with a **square icon zone** on the left separated by a hairline
seam, plus a quiet `Colors.Border` hairline (α 0.30 idle → 0.50 hover) for
edge definition. Idle is a
quiet gray (white α 0.075 pill / α 0.13 zone - a step brighter than the
surrounding band so buttons read as clickable), hover lifts toward the
accent (0.13 / 0.27), press sits at 0.17 / 0.38, and an optional `active`
state lights the zone in solid accent with a contrast glyph - the same
color logic as `Layout.NavRailSegmentItem`, just without a mandatory
active indicator. Under the tint layers every button gets an opaque
`Colors.FrameBg` scrim (≥ α 0.66) so translucent fills stay legible over
banner art and imagery - the frosted look without a per-rect blur pass.

Three modes from one core: icon + label → segmented button, icon only →
square zone button, label only → zone with a subdued default chevron
(`ChevronRight` at 55 % alpha) so every labeled button is recognizably a
button. Public surface: `Controls.SegmentButton` is the canonical helper;
`Controls.Button` (`danger:` maps to `Colors.Danger` accent), `SmallButton`,
`IconButton`, `DialogButtons`, `Icons.Button`, `Icons.TextButton` and
`Icons.ActionButton` all delegate to it - so every button in the app shares
the look. `Controls.PrimaryButton` is the accent-solid variant (the whole
pill carries the accent with contrast text) - reserve it for the single
primary action on a page so confirms like "Use image" stay findable.
`Icons.TextButtonWidth` matches `SegAutoWidth` outside compact
scopes; explicit `Vector2` sizes keep working but include the zone width -
when in doubt pass `new Vector2(0, height)` for auto width + fixed height.

Scope gates: inside `ThemeScopeTracker` compact scopes and for buttons with
a `styleKey` theme override (or while the style picker is enabled) the
legacy themed path still runs, so CompactUI keeps its look and the
button-style customizer keeps working. Value controls (inputs, sliders,
checkboxes, toggles, selectables) keep the `ControlRadius` frame language
via `PushValueControlStyle` instead.

### Actionable landing rows

When an entry point *leads somewhere* rather than triggering an action
(opening a page, picking an item, expanding a list), use a full-width
action row instead of a button: same pill + icon zone + seam language,
plus a right-aligned muted meta string, a trailing `ChevronRight`, and an
optional second muted description line. Reference: `HubRow` in
`SpheneHubUi` (Syncshells landing - join/create rows, shell list rows,
area-syncshell browse toggle). Keep descriptions to one clipped line and
separate stacked rows with `Spacing.Inline`.

Sibling entry points that sit side-by-side use the half-width variant
`HubTile` (same pill + icon zone + seam, single title line, trailing
`InfoCircle` glyph) - the explanatory text moves to the caller-attached
tooltip via `AttachTooltip` so two tiles share one row. Reference:
join/create tiles on the Syncshells landing page.

### Page-level save/apply actions

Save/Apply/Reset buttons that commit page state must stay reachable
without scrolling - never at the bottom of a scrollable page. Dock them
top-right beside the page's help/intro row (right-aligned via
`GetContentRegionAvail().X - totalButtonWidth`, `SameLine`-chained), or
into a pinned/sticky header row when the page already has one
(`DrawStylePage` hosts Save/Reset on its pinned tab strip).
Dirty state gating an Apply button must persist across frames in an
instance field - a per-frame local makes the button exist only during
the frame the control changed and is effectively unreachable
(reference: `SyncshellAdminUI._areaSettingsDirty`).

## Page anatomy

Pages should read as the same app even when their content differs.
Two page shapes exist:

**Rail-navigated pages** (hub pages, settings sub-pages, embedded hosts):
the rail selection and the breadcrumb pill already identify the page -
**do not open with an in-body page title**. An optional `Text.PageIntro`
(one-line muted blurb, full text on hover) is the most a page may lead
with. Page help lives on the nav item's `helpId` dock (see "Page help").

**Standalone windows/dialogs** (Join/Create/Permissions, IntroUI,
EventViewer, …): keep `Layout.PageHeader(title, helpId, drawHelp)` -
the title row with the docked "?" - since no rail names them.
`Text.PageIntro` beneath it is optional: use it only where extra
orientation or explanation is genuinely useful, not on every dialog.

Below the identity layer, the shared anatomy top to bottom:

1. **Action header (one row, where the page has one):** context left -
   a `Sources`-style back button or the page's current focus text - the
   destination action right, with badges/metadata beside it. Never a
   prose block; one clipped line max, `AttachTooltip` carries
   explanations. Pages whose chrome already carries context (rail +
   crumb) skip this row.
2. **Content region:** the scrollable body. Sections are introduced by
   `Text.SectionHeader` and grouped in `Layout.Surface`/`Layout.RowGroup`
   - no free-floating controls between sections. Multi-column tools
   (browsers, editors) split the region into a narrow nav/list column
   and a flexing content column rather than stacking full-width blocks.
   Structural chrome (header bands, tab strips) is pinned via the
   recipes in "Navigation rails", not scrolled away.
3. **Footer action bar:** docked at the bottom, never scrolled away -
   `Layout.ActionBar(rightWidth, drawRight, drawLeft)` renders it:
   secondary/status content flows from the left, the **single primary
   action** sits far right, docked via `DockToCardEdge` with exact
   `Icons.TextButtonWidth`/`SmallButtonWidth` math so it can never clip
   the edge. Reserve `GetFrameHeight() + Spacing.Row` from the scroll
   region above it.

Action hierarchy: one `PrimaryButton` per page - the action that commits
or advances. Everything else stays a tonal `SegmentButton`/`TextButton`;
destructive commits use `PrimaryButton(accent: Colors.Danger)` (dialog
confirms via `DialogButtons` do this automatically). Two co-equal
choices ("Join Selected" / "Join All") may both be primary - three or
more means the page wants restructuring.

Reference implementation: `PhotoStudioUi` - pick stage (header row,
three-column browser, footer bar) and edit stage (header, canvas +
sidebar, footer action bar).

## Page help

Every page's explanation lives in a "?" **flyout** - not in on-page prose,
not in a dedicated content row, never inside breadcrumbs. The placement
depends on the page shape:

- **Rail pages:** the nav item carries `helpId:` + `helpDrawer:` -
  `NavRailSegmentItem` paints a hover-revealed "?" dock at the row's right
  edge that opens the flyout *without navigating* (`NavHelpDock` in
  `SpheneUI.Layout.cs`). While the page draws, it calls
  `Layout.RegisterPageHelp(helpId, drawHelp)` once per frame so every nav
  entry pointing at that id resolves the same flyout;
  `Layout.HelpExtra(draw)` merges extra sections into the active page's
  flyout from anywhere below its registration (replacing on-page
  "What is this?" foldouts). `Layout.EnsurePageHelp` pre-registers a
  flyout for entries whose page was never opened.
- **Standalone windows:** `Layout.PageHeader(title, helpId, drawHelp)`
  renders the title row with the "?" docked at the far right - the same
  flyout, the same spot.
- **In-page anchors:** `Layout.PageHelp(id, drawHelp)` drops a lone "?"
  button anywhere context justifies one (e.g. beside an article's title
  cluster).

`Text.PageHeader` is **obsolete** - a zero-callsite leftover kept until a
future API cleanup; do not call it. Standalone headers are
`Layout.PageHeader`; rail pages draw no in-body title at all.

## Spacing rules

All vertical rhythm and horizontal insets come from the `Spacing` token
grid (`1/2/4/8/10/12/16/20/24/32`) - never `ScaledDummy`/`ImGui.Dummy`/
`ImGui.Spacing` literals outside `UI/Design`. The public API on
`SpheneUI.Layout`:

| Helper | Shape | Use |
|---|---|---|
| `VerticalSpace(Space)` | `0 × n` | row/group breaks |
| `VerticalSpace()` | themed `ItemSpacing.Y` | minimal same-group gap |
| `Space(Space)` | `n × n` | block gaps that also reserve X |
| `Space(Space, Space)` | `x × y` | asymmetric dummy |
| `HorizontalSpace(Space)` | `n × 0` | pure horizontal spacer |

Semantic tokens: `Row` (8) between consecutive controls, `Section` (12)
between logical groups and **before a primary action row**,
`CardGap`/`CardPadding`/`PagePadding` for surfaces, `Inline` (4) for
same-row neighbors. The rules that keep pages uniform:

- A button or action row that follows text or controls gets at least
  `VerticalSpace(Spacing.Row)`; a primary/section-ending action gets
  `Spacing.Section`.
- Consecutive stacked buttons (e.g. context-menu items) stay tight - no
  gap between sibling buttons.
- Table cells and `SameLine` rows never get `VerticalSpace` inside.
- `Spacing.Custom` exists only as a lint-flagged escape hatch.
- **Nothing sits flush against a container edge.** Header labels, table
  cells, list rows, section titles and drawlist text always carry a
  token-driven inset (the container's padding or an explicit `Spacing`
  offset) - text touching a frame edge is a defect, fix the inset rather
  than shipping it. For tables the horizontal label/cell inset is the
  `CellPadding` pushed in `Tables.Begin` (8px scaled): ImGui snapshots
  `CellPaddingX` at `BeginTable`, so a later style push can never move
  header labels - it only affects row height.

## Initialization

`UiSharedService`'s constructor calls `SpheneUI.Initialize(logger, mediator,
iconFont, uidFont, displayFont, headingFont, gameFont)` and
`SpheneUI.Media.SetTextureProvider(...)`.
The service keeps owning and disposing the font handles; the facade only
holds references. `IconFont`/`UidFont`/`DisplayFont`/`HeadingFont`/`GameFont`
throw if accessed before `Initialize`. `UidFont` is Lato 35px (UIDs, big stat
numbers); `DisplayFont` is Lato 26px - use `Text.Display` for headings
instead of `SetWindowFontScale`, which raster-scales glyphs and blurs.
`HeadingFont` is Lato 20px - the mid-size face behind markdown h2/h3 and
`CardHeader` card titles, so headings stay crisp without raster scaling.

## Spacing grid

All gaps, paddings and indents come from a fixed scale (unscaled values,
multiplied by `ImGuiHelpers.GlobalScale` via `Space.Scaled`):

```
1, 2, 4, 8, 10, 12, 16, 20, 24, 32
```

Prefer semantic aliases over raw steps: `Inline` (row gaps), `Row` (control
stacking), `Section` (group gaps), `CardGap`, `CardPadding`, `PagePadding`,
`ScrollInset`, `TitlePadX/Y`, `TitleButtonGap`. `Spacing.Custom(v)` is an
escape hatch that always records a lint violation - extend the scale instead.

## Layout density (`SpheneUI.Density`)

Three application-wide density profiles adapt spacing, row heights and
headline sizes to the space a window actually has:

| Profile | Auto rule (unscaled window units) | Effect |
|---|---|---|
| `Compact` | height ≤ 640 **or** width ≤ 760 | style vars ×0.75, tighter token steps |
| `Balanced` | otherwise | unchanged baseline |
| `Comfortable` | height ≥ 880 **and** width ≥ 1400 | style vars ×1.15, looser token steps |

- **Scope**: the window base class resolves the profile once per frame -
  `PushFrame` carries the previous frame's value through PreDraw (fold math)
  and `AutoWindowScope` inside `Draw()` pushes the real profile plus a
  style-var pass (`ItemSpacing`, `ItemInnerSpacing`, `FramePadding`,
  `CellPadding`, `IndentSpacing`). Embedded pages inherit the host's scope;
  CompactUI stays Balanced.
- **Manual override**: `SpheneConfig.LayoutDensity` (`LayoutDensityMode`
  Auto/Compact/Balanced/Comfortable) via `Density.SetOverrideProvider` -
  Settings ▸ Appearance ▸ "Layout density".
- **Token remap**: semantic `Spacing` aliases step to a neighbor grid value
  per profile (e.g. `CardPadding` 10/16/20, `Row` 4/8/10); raw steps never
  move. `Metrics.RowHeight` 24/28/32, `GridMinCell` 180/200/230,
  `NavRailItemHeight` 26/30/34, `NavRailDividerHeight`, `BadgeHeight`,
  `Chrome.TitleBarPaddingY` and `Profile.HeroBannerHeight`/`HeroAvatarSize`
  follow the same pattern.
- **Typography**: `Text.Big` renders in the 26px display font under Compact
  instead of the 35px Uid font - page titles stay proportional.
- **Hardcoded sizes**: multiply page-level structural dimensions (hero
  strips, cover thumbs, tile grids) by `Density.Factor` (0.75/1/1.15)
  instead of adding new constants.
- **Viewport fit**: `Chrome.FitToWorkArea` clamps every window's
  `SizeConstraints.MinimumSize` and default `Size` to 94% of the viewport
  work area each PreDraw - no window can demand more than the screen has
  (`Chrome.WorkAreaMax` exposes the same bound for raw
  `SetNextWindowSize*` call sites).

## Theming

`WindowMediatorSubscriberBase.Draw()` wraps every design-system window in
`SpheneUI.Theme.WindowScope()` automatically - windows never push theme
colors themselves. CompactUI uses `CompactScope()` deliberately.

## Window chrome

Every `WindowMediatorSubscriberBase` window renders the design-system chrome
automatically: `PreDraw` adds `NoTitleBar | NoCollapse | NoBackground` and
hides the native resize grip, and `Draw` paints the themed window surface
plus a custom `Chrome.TitleBar` (drag region, title, icon, right buttons,
fold toggle) before `DrawInternal`. Content fades and skips while the fold
animation runs.

Per-window customization via `protected override` properties:

- `UseSpheneChrome` - opt out entirely (overlays/hosts: `DownloadUi`,
  `PopupHandler`, `SpheneIcon`; CompactUI via `AppliesOwnThemeScope`).
- `ChromeCollapsible` - off for `AlwaysAutoResize` windows where fold
  cannot shrink.
- `ChromeTitle` - defaults to `WindowName` minus the `##id` suffix.
- `ChromeIcon`, `ChromeTrailingText`, `ChromeRightButtons` - the default
  right button is a Close `Times` button honoring `ShowCloseButton`.
  `SettingsUi` overrides these for the Discord/Ko-fi buttons + version text.
- `ChromeIconTexture` - optional `IDalamudTextureWrap` drawn instead of
  `ChromeIcon` when set (cover-fit, rounded).
- `ChromeMiddleContent` - optional `Action<Vector2, Vector2>` drawn inside
  the title bar between the title and the right buttons; receives the
  zone's min/max screen positions. `SettingsUi` uses it for its
  back/forward + crumb-pill cluster.
- `Chrome.CrumbPill(min, maxWidth, parentLabel, currentLabel,
  onParentClick)` - the shared breadcrumb pill ("Parent › Current"),
  dark scrim + thin border, ellipsizes (parent drops first). Parent is
  clickable when `onParentClick` is set; the hub's nav crumb and the
  settings page crumb both render through it.
- `TitleBarButton.BadgeCount` renders a red counter badge on the button's
  top-right corner (0 = hidden, capped at "9+") - the design-system
  notification indicator for title bar buttons. `SpheneHubUi` reuses the
  drawer (`Chrome.DrawTitleBarButtonBadge`) to badge in-band buttons.
- `ChromeHasTitleBar` - false lets the content own the window top: the
  chrome then paints only the themed surface + border ring and the
  window's own header band acts as the title bar. `SpheneHubUi` is the
  reference - its band carries drag space between the widgets plus the
  fold and close buttons itself.
- `DrawCollapsedDuringFold` + `DrawCollapsedContent` - replaces
  `DrawInternal` for the entire fold animation instead of the alpha-fade
  default; only sensible with `ChromeHasTitleBar == false`. The hub draws
  a "status strip" (profile card + fold/close buttons) while folded.
- `ChromeCollapsedHeight`/`ChromeCollapsedWidth` - folded window size in
  unscaled units; `Width` > 0 shrinks the window horizontally alongside
  the fold. The hub computes it from the rail width plus its strip
  buttons so the status card is never clipped.
- `Chrome.ResetFold` runs in `OnOpen` for every collapsible chrome window -
  a window closed while collapsed always reopens maximized.

`SpheneBrandIcons.SpheneLogo()` returns the embedded Sphene logo texture
(`images/icon.png`) - colored artwork, do not tint; used by `SpheneHubUi`
for the hero banner watermark.

Windows must NOT draw their own `Chrome.TitleBar` when `UseSpheneChrome`
applies - the base does it. Windows overriding `PreDraw`/`PostDraw` must
call `base.` so chrome flags and the hidden-grip colors stay balanced.

Windows with their own styling system can still drive the fold machinery
for a custom bar (CompactUI's profile title bar): call `UpdateCollapse` in
`PreDraw`, draw the bar first in `DrawInternal`, gate the remaining content
on `CollapseAmount`/`IsCollapsed`, and wire the chevron to the `internal`
`Chrome.ToggleFold`/`Chrome.FoldCollapsed` pair - `ToggleFold` is not
public so generic windows keep going through `Chrome.TitleBar`.

- `SpheneUI.Theme.Current` exposes the active `ThemeConfiguration`.
- `SpheneUI.Colors.*` resolves semantic slots (`TextPrimary`, `FrameBg`,
  `Accent`, `Border`, `ButtonBg`…) against the theme; `SpheneUI.Palette.*`
  holds fixed brand constants. Use `Colors` in controls.
- Tooltip/context-menu chrome comes from `TooltipScope()`/`ContextMenuScope()`
  - already applied inside `SpheneUI.AttachTooltip` and the `Popups.*Styled`
  helpers. Always open popup windows through `Popups.BeginStyled` /
  `BeginModalStyled` / `BeginContextStyled` + `EndStyled` so the popup frame
  itself is themed; pushing a scope *after* `BeginPopup*` only styles content.

## Cards and section panels

Grouping surfaces are **borderless tonal panels**, not outlined boxes - the
same language as nav rails and the header band:

- `Layout.HeadedRowGroup(title, content, icon, description?, accent?)`
  is **the canonical settings card**: a `RowGroup` under an integrated
  `CardHeader` - accent-tinted header band, bare accent icon glyph (no
  plate), `HeadingFont` title, optional one-line muted description,
  optional `· N` count and right-docked icon actions - then a full-width
  divider and the body. All ~45 `DrawSettingsCard` sites route through it
  plus the Edit Profile privacy card. `Layout.HeadedSurface` is the
  identical chrome for free-content bodies. `Layout.CardHeader` is the
  standalone geometry helper (icon/title/description/actions measured and
  painted into a caller-managed surface) for bespoke headed panels.
- **Card-header icons are required and semantic (Variant B).** Every
  integrated card header takes a `FontAwesomeIcon` chosen for the card's
  *purpose* - never the parent page/rail icon inherited by default, and
  never an arbitrary glyph picked only to look different. The same
  semantic concept may reuse the same icon across pages; unrelated cards
  must not share one. `DrawSettingsCard(title, icon, content, …)` makes
  the icon a required positional argument so every settings card declares
  its own. The helper renders the bare accent glyph centered inside a
  fixed invisible slot (`CardHeaderIconSlot` ≈ 20 unscaled px + `X8`
  icon→title gap, both scaled): `TitleX = left + slot + gap` for every
  card, so glyph widths never shift the shared title column and headers
  stay vertically centered against the title line. The slot is plain
  space - no plate, tile, border, hover or background.
- **Header→body spacing is owned by the helper.** `Layout.HeaderBodyGap`
  positions the first body element one uniform offset below the divider -
  `X4` of air plus the row-band pad a grouped control would add itself -
  and `GroupState.SuppressNextRowPadding()` keeps that first control from
  double-adding its pad. Callers own spacing *between* body elements only:
  no leading `VerticalSpace`/`Dummy`/cursor offsets before the first body
  element, for any content type. Reference card: Service Configuration.
- **One primary heading per card.** The integrated header *is* the title -
  body `Text.Big`/`Text.Display`/`Text.SectionHeader` repeating it are
  defects (nested subsections and alerts are fine). Card-header icons are
  bare accent glyphs - and so is every other ordinary icon: stat tiles,
  `FeatureTile` and list-row kind markers all draw `SlotIcon` (a bare
  glyph in an invisible slot), never a colored tile.
- `Layout.SettingsGroup(label, icon?, firstGroup)` is the in-card
  **secondary** group label for long settings lists - a medium-weight
  `TextSecondary` label (faux-bold double-draw; no semibold face is
  loaded) with an optional bare accent icon centered on the text line.
  Groups separate by plain vertical whitespace (`Spacing.Section`), never
  a horizontal line above a heading; `firstGroup: true` emits no
  pre-heading gap since the card divider already separates it, and
  `GroupState.BreakRow` keeps any pending row hairline from landing under
  the heading. The helper owns heading typography, pre-group spacing and
  heading→first-row gap; the card keeps owning `HeaderBodyGap`. It
  subdivides one card without nested cards - the Community Notifications
  card's five sections are the reference.
- `Layout.Surface` is the base primitive: the "card that does not look like
  a card" - borderless `NavRailBandColor` fill, `AccentCardRounding`
  corners, generous padding, **no built-in title**. Sections above it are
  introduced by `Text.SectionHeader` (the community view's rail uses its
  own local `RailHeader` label style - a view detail, not a facade
  member). `interactive: true` lifts the
  fill slightly on hover - for clickable cards where the caller owns the
  click handling. `border: true` adds a hairline `Colors.Border` outline and
  `sheen: true` a fading accent kiss along the top edge - both opt-in for
  surfaces that need definition against busy backgrounds.
- `Layout.RowGroup` is the iOS-style settings group: a `Surface` plus an
  ambient `GroupState`. Rows drawn inside separate themselves with inset
  hairlines and share `Metrics.RowHeight`. Any group-aware control
  participates without call-site changes: `Controls.Toggle`,
  `PrepareLabeledControl`-based controls (combos, sliders, inputs at line
  start with a visible label) and every `Rows.*` helper. Pending rows
  auto-complete on the next row or at group end, so help icons and subtext
  stay attached to their row. Non-row content (lists, previews, custom
  blocks) renders inside the same surface unseparated.
- `Rows.*` are the row helpers: `Toggle`/`Switch` (slide switch right),
  `Segmented`, `Combo`, `SliderInt`/`SliderFloat`, `Input`,
  `Nav` (muted value + `›` chevron, full-row hit area),
  `Value` (read-only label/value, optional `valueColor`),
  `Action` (full-row hit area, `danger` tint), `Note` (unseparated
  subtext), `StatusPill`. They also work standalone outside a group.
- `Controls.Segmented(id, ref index, items, width)` is the connected
  segment bar - use it instead of a `LabeledCombo` whenever ≤4 options
  exist; `Controls.SegmentedWidth(items)` computes the natural bar width.
- `Layout.CardGrid<T>(items, minCellWidth, drawCell, gap, maxCols)` lays
  items out in 1..maxCols equal columns by available width (default cell
  minimum `Metrics.GridMinCell`). Cells get a bounded `ContentRegion` -
  draw a `Surface` or cover card inside. Use for albums, links, staff,
  directory cards.
- `Layout.SectionCard` is the titled free-content panel for non-settings
  surfaces (admin pages, dialogs, Intro - its ~80 call sites make it the
  workhorse outside Settings): same tonal fill, the accent seam drawing
  **only when a title or explicit accent is passed** - untitled calls
  render as plain surfaces. `border: true` adds a hairline outline for
  actionable cards. Pick by context, not by era: settings pages use
  `HeadedRowGroup`/`HeadedSurface`; `SectionHeader` + `Surface`/`RowGroup`
  covers untitled sections.
- `Layout.AccentCard` stays the prominent/hero card: `AccentCardTint` body,
  optional `Chrome.AccentCardBackdrop` shimmer, `topBleed` for full-bleed
  media - and a gradient-ramp edge (deep stop ring + bright top kiss)
  instead of the old pulsing border.
- Animated effects live on `Profile.RowFx`/`ProfileRowFx` layers (ListRows,
  hero cards), not on container outlines. Keep effects inside surfaces; do
  not reintroduce outlined group boxes.
- `Layout.Badge(text, color)` draws an inline tag pill (accent underlay α
  0.14 + hairline α 0.35) at the cursor - the standard marker for "NEW",
  "TEST", "BETA"-style flags on rows; `solid: true` renders the loud variant
  (accent-ramp fill + contrast text) for state markers that must read at a
  glance. Use `Controls.TagChips` for comma-separated tag lists.
- **Icons are bare glyphs, not tiles.** `Layout.SlotIcon(drawList, min,
  side, icon, color)` centers a glyph in an invisible `side`-sized slot -
  the standard icon treatment everywhere: no fill, gradient, rim or sheen
  behind the glyph; the slot only guarantees a shared title column.
  Glyph color is `TextSecondary`/`TextMuted` by default; accent/kind/
  lifecycle colors only when the color carries meaning. Never draw colored
  app-style icon plates behind ordinary icons - the surrounding card/row
  carries the interactive affordance. The one justified container is
  `Layout.IconPlaceholder(drawList, min, side, icon, tint, radius?)`: a
  tonal `NavRailBandColor` rounded rect + `Border` hairline + faint glyph,
  reserved for *missing media/avatar* fallback slots ("no image" markers).
  `Layout.FeatureTile(id, icon, tint, title, sub?, width?, showChevron?)`
  is the clickable dashboard tile built on `SlotIcon`: glyph left (eases
  toward the tint on hover), title + optional subtitle in a shared text
  column, trailing chevron, eased `Fx.HoverLift` - the quick-nav /
  jump-off tile; lay out rows of them with `Layout.CardGrid`.
- **Selection on cards and card-like rows is a surface treatment - never an
  edge attachment.** The selected state is a complete rounded outline in
  the accent (~0.7-0.75α, ~1·scale) plus a subtle accent-tinted fill
  (~0.10-0.12α), drawn inside the item's existing bounds: no glued vertical
  strip on the card edge, no pseudo-tab, no glow, and zero geometry change
  (neighbors, text and icons never shift when selection toggles). Hover
  keeps its eased `Fx.HoverLift`; hover, focus and selected remain
  distinguishable without stacked decorations. Selection is a separate
  layer from lifecycle/status styling - status lives on dedicated slots
  (badge/pill, date or icon accent, dimming) so *status + selected* reads
  as both at once; never let selection erase status identity or vice
  versa. Status always carries a text counterpart - color is never the
  only signal. Scope: selectable content cards/rows (event rows, pickers,
  lists). Navigation chrome - the NavRail active indicator, HubRow/HubTile
  icon zones, tab strips - follows its own rules and is not item selection.

## Content bounds

Cards and content regions push a right-edge bound (`Layout.ContentRightBound`)
that `Controls.BoundedAvailX()`, `FillRowWidth()`, `DockToRightEdge()` and
`SameLineIfFits()` respect. `ImGui.GetContentRegionAvail()` lies inside card
regions - never use it for row math; use the bound helpers so controls dock
flush to the shared edge instead of overshooting cards.

A card's right edge is clamped **geometrically**: `avail`, the current
window's edge and the innermost scroll area's right bound
(`scroll child right − 1px − ScrollAreaContentInset`) all compete in a `Min()`.
Cards drawn directly in the scroll child keep its inset; cards inside nested
children (expanders, embedded panels) are bounded by their own child's edge
and are never double-inset. Windows outside the scroll area (popups) skip the
bound entirely. The same math backs `Layout.CardAlignedAvailX()` for
non-card elements that must share the card edge - and `BeginExpander` clamps
its panel to it, so the panel outline lands exactly on the card edge.

Raw `ImGui.BeginChild` regions do NOT push a bound - right-docked controls
(`Controls.Toggle`, `InputInt`, `Combo`…) inside them dock to the outer area
and overflow the child. Wrap such children with
`SpheneUI.ContentBounds.Push(new Vector2(minX, maxX))` / `Pop()` around the
content (or use `BeginScrollArea`, which pushes bounds itself).

### Docked button widths - measure, never estimate

`DockToCardEdge(width)` places the cursor so the next item's right edge
lands flush on the bound. Passing an *estimated* width that is smaller than
the control's real width pushes it past the bound and clips it against the
card edge - a recurring bug, always with the same shape: an icon button
half-visible at a card's right edge.

Never estimate with `ImGui.GetFrameHeight()` (that's a height, not the
segmented button's width) or hand-counted pixel sums. Measure with the
helpers the button itself uses:

```csharp
// icon buttons (Icons.Button / Icons.IconButton)
var w = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Times).X;

// labeled buttons (SegmentButton / Button / TextButton)
var w = SpheneUI.Controls.SegAutoWidth(label, icon, ImGui.GetFrameHeight());
// or: SpheneUI.Icons.TextButtonWidth(icon, label)

// a row of N buttons = each measured width + spacing between them
var rowW = SpheneUI.Icons.ButtonSize(FontAwesomeIcon.ArrowRight).X
    + SpheneUI.Icons.ButtonSize(FontAwesomeIcon.Times).X
    + ImGui.GetStyle().ItemSpacing.X;
SpheneUI.Controls.DockToCardEdge(rowW);
```

When a row docks several buttons, measure each one - padding differs per
glyph, and the segmented icon zone is a square whose width comes from the
button's height, not the frame height.

## Navigation rails

Multi-page windows use a left rail instead of `ImGui.BeginTabBar`:

```csharp
var railW = SpheneUI.Layout.NavRailWidth(labels);
SpheneUI.Layout.BeginNavRail("id", railW);          // rail band + scroll area
SpheneUI.Layout.NavRailCategory("Group", accent);
if (SpheneUI.Layout.NavRailItem("Home", page == P.Home, accent)) page = P.Home;
SpheneUI.Layout.NavRailDivider(accentA, accentB);
SpheneUI.Layout.EndNavRail();
if (SpheneUI.Layout.BeginNavContent("id")) { /* page content */ }
SpheneUI.Layout.EndNavContent();
```

`NavRailItem` paints the Settings-style pill: accent fill, left edge bar and
top sheen when active. Pass `icon:` to switch the item to icon mode - a
**square** button (height = rail content width) rendered as the standalone
version of `NavRailSegmentItem`'s icon zone: gray rounded square idle with
the accent-colored glyph, accent tint on hover, solid accent with a contrast
glyph when active - no left edge bar or sheen. The label shows as a tooltip,
so the rail stays narrow; size it with the `NavRailWidth` overload taking
`(FontAwesomeIcon Icon, string Label)` tuples (label is used for the
tooltip). Icon mode is intended for tight sub-rails where labels would not
fit.

`NavRailSegmentItem(label, icon, active, accent, special?)` is the labeled
variant for primary rails: a square icon zone on the left separated from the
label by a thin seam. Idle, the zone is a shade lighter than the pill and the
glyph carries the accent color; when active, only the zone lights up in solid
accent (glyph flips to contrast color) while the label area picks up a faint
accent tint. `special` tints the idle label in the accent color. Size the
rail with `NavRailSegmentedWidth(items)` - `SpheneHubUi`, the embedded
`SyncshellAdminUI` rail and the `SettingsUi` sidebar are the reference
implementations.

All pills keep a visible margin to the rail edges: the nav child itself is
inset by `NavRailPadX` (10px scaled, public token) on both sides - enforced
by the child rect, not `WindowPadding` - and all `NavRailWidth`/
`NavRailSegmentedWidth` formulas include that inset. Custom rails (like the
`SettingsUi` sidebar with its pinned category headers) should replicate the
same inset geometry instead of relying on child `WindowPadding`. The content
scroll area starts right of the rail; rail and content scroll independently
with their own fade hints.

### Hub-hosted settings

The Sphene Hub hosts `SettingsUi` embedded: the rail slot swaps between the
hub nav and the settings nav with a vertical pan (hub items slide up and
out, the settings list slides in from below, attached at its bottom edge -
see `SpheneHubUi.DrawInternal`). Shared pieces:

- `SettingsUi.EmbeddedNavItems` - the static nav list; the hub lerps the
  rail width between `NavRailSegmentedWidth(hub)` and `(settings)` with the
  pan animation (`SpheneHubUi.CurrentRailWidth`), so hub pages keep their
  narrow rail and the band never leaves a dead strip inside the content
  edge.
- `SettingsUi.DrawEmbeddedRail()` - settings items + category labels drawn
  in-flow (no pinned band) into the hub rail child, plus a "Pop Out" item
  that opens the standalone `SettingsUi` window.
- `SettingsUi.DrawEmbeddedContent()` - the active settings page rendered
  inside the hub's padded nav content child (no own scroll wrapper),
  including the CompactUI theme-stick and the save/backup modals.

Both surfaces share `_activeSettingsPage`, so embedded and standalone stay
in sync. The standalone window remains the popout path (CompactUI toggle,
commands, the burger menu's "Pop out Settings" and the rail's "Pop Out"
item).

### Full-width header band (seamless rail transition)

For a community-style header spanning the window's whole width above the
rail, reserve a `Dummy` band before `BeginNavRail`, then after `EndNavRail`
paint the band with `Layout.NavRailBandColor()` - the exact fill the rail
band uses - so header and rail merge seamlessly. `SpheneHubUi` (see
`DrawHubHeaderBand`) shows the recipe: left zone holds the persistent
profile identity (avatar + name + truncated status line with full-text
tooltip, clickable to the profile page), right zone holds page title and
branding, with an accent seam along the band's bottom edge and a subtle
zone divider at the rail border.

### Embedded rails and host pages

- `BeginNavRail("id", width, embedded: true)` anchors the rail to the
  current child window's top edge instead of the parent window's title bar.
  Use it when a rail layout renders inside another window's content area
  (e.g. the admin panel inside Sphene Hub).
- `BeginNavContent("id", scrollable: false)` opens a plain (non-scrolling)
  child instead of the fade scroll area. Required when the page itself
  contains a rail or self-managed scroll regions - nesting a rail inside an
  outer scroll area mispositions its band. The child uses zero
  `WindowPadding`: embedded layouts provide their own rail + scroll insets,
  so their content lands on the exact same edges as scrollable pages.
- Scrollable nav content is a **content wrapper**: it carries
  `WindowPadding = PagePadding` (16) on all sides, so pages never add their
  own top/side margins - they draw flush from the content origin. The
  scroll fades still render across the full child bounds (`EndScrollArea`
  draws them on the overlay child, not the padded content region), so the
  padding never narrows the fades. `padded: false` opts out for pages with
  edge-to-edge chrome of their own. Implementation detail: borderless
  children ignore `WindowPadding` by default - `BeginScrollArea` sets
  `ImGuiWindowFlags.AlwaysUseWindowPadding` whenever `contentPadding` is
  passed, otherwise the push is silently a no-op (that is also why the
  `noPadding` path uses `Indent` instead of a style var).
- Rail hosts are **fixed surfaces**: `BeginNavRail` registers the host
  window's rect and `EndScrollArea` never chains wheel input into a
  registered host (programmatic `SetScrollY` would bypass `NoScrollWithMouse`
  and slide the whole edge-to-edge layout out of the window). The rail
  scrolls internally only when its own items overflow the band.
- The nav-content child spans to the **parent window's right edge** (the
  content region ends `WindowPadding` short of it). Combined with
  `ScrollAreaRightBound` this makes the right card margin equal the left
  inset: `windowRight − 1px − ScrollAreaContentInset`. Never size the child
  by raw `GetContentRegionAvail` - cards would sit one padding too narrow
  on the right.
- `BeginNavContent` paints an **opaque backing** under the whole content
  region: the surface gradient sampled at the child's top/bottom edge,
  alpha forced to 1. Without it the page-padding inset would leave a
  translucent strip along the right and bottom frame edges where the
  window blur shows through. The fades blend into the same tone (a
  mid-sample of the backing), so the scroll lip reads as part of the
  surface rather than a gray veil.
- Scrollable and non-scrollable nav content nest correctly (mode is tracked
  per open rail).

### Structural chrome & pinned headers

Structural chrome (rails, header bands, tab strips, action bars) never
scrolls away; decorative chrome (banner art, heroes, previews) scrolls or
retracts. Two pinning recipes exist - pick by input needs:

- **Clamped last-drawn block** - reserve the element's flow space with a
  `Dummy`, draw the block *after* the page content, and clamp its Y
  between its natural slot and the child's visible clip top so content
  scrolls underneath. Cheapest option when the pinned element only needs
  to stay visible (the hub Style page's preview + tab strip).
- **Overlay child** - a spacer `Dummy` reserves the expanded header inside
  the scroll child while the real header draws in an overlay returned by
  the content call and invoked after `EndScrollArea`, so its hit regions
  live outside the child's clip. Required when the pinned surface takes
  its own input (the community page's collapsing toolbar -
  `ComputePinnedGeom` + `DrawPinnedHeader`: hero retracts into a compact
  strip as scroll consumes the spacer 1:1 with the wheel, tabs stay
  docked underneath). Standalone community windows reuse the same recipe
  over the window bleed.

### Hub navigation history & breadcrumb

Sphene Hub treats a page plus its detail context as one *location* -
`HubLocation(Page, GroupGid, Friend)` - and keeps back/forward stacks.
`NavigateTo` pushes the current location and clears forward;
`NavigateBack`/`NavigateForward` move without losing context;
`ApplyLocation` performs the leaving-cleanup (embedded admin panels,
profile edit mode, friend texture caches) then applies the target. Every
in-hub navigation (rail items, friend rows, syncshell cards, back-rail
headers, hero actions, `OpenSpheneHubMessage`) goes through these -
never assign `_activePage`/`_selectedGroupGid`/`_viewingFriend`
directly, or history and breadcrumbs desync. System-driven corrections
(a group vanishing) use `ApplyLocation` directly so they do not pollute
the stacks; both stacks reset when the window closes
(`ResetTransientState`).
The header band is a slim toolbar (~52u): the **profile card claims its
rail-colored left segment** (the sidebar owns that part of the header) -
circular avatar with `Media.PresenceDot` badge (fixed 45° position on
the circle edge - avatars are always circles), name + status, hugging
the rail width, click → profile page. The nav cluster sits right behind
it at the content edge: `[←] [→]` plus the breadcrumb pill on one row
(`ImRaii.Disabled` when the stack is empty), bell + menu buttons
mirrored on the right - all at one vertical center. The pill is a dark
55% scrim with a 10% hairline, fully rounded - legible over bright
banner art and dark surfaces alike. Depth is two segments max
(`Page › detail`); the parent segment is clickable and navigates one
level up.
The style editor is its own page (`SpheneHubPage.ProfileStyle`, crumb
"My Profile › Style") reached via the hero's always-visible "Style"
action; it reuses the clamped-block pinning recipe described in
"Structural chrome & pinned headers" and returns via
`NavigateBack` on Save/Reset.

### Embedding existing windows (`DrawEmbedded`)

Windows whose content must also render inside a host window (Sphene Hub,
`ModSharingUi`) expose `internal void DrawEmbedded()` that draws *content
only* - no own `Window` frame, no own scroll area (the host provides it).
Hosts inject the concrete window type (register it `AddScoped<T>` plus a
forwarding `AddScoped<WindowMediatorSubscriberBase>(s => s.GetRequiredService<T>())`)
so embedded and standalone views share one instance and state. Per-instance
windows (e.g. `SyncshellAdminUI`, created via `UiFactory` per group) are
created by the host and *not* added to the `WindowSystem`; the host owns
their `Dispose()`. If the embedded page has visibility-dependent cleanup,
expose a paired `internal void OnEmbeddedHidden()` the host calls when the
page is left (see `CharaDataHubUi`).

`SpheneHubUi` is the reference host implementation.

## Toggle vs. checkbox

`Controls.Toggle` always renders a full-width row: label left, switch docked
to `ContentRightBound`. Never place it in `SameLine` chains, table cells,
inside narrow children, or before text that must follow on the same row -
use `Controls.Checkbox` (compact themed box, label flows inline) there.

## Images

`SpheneUI.Media` owns every texture draw - raw `ImGui.Image`/
`ImDrawListPtr.AddImage` are banned outside `UI/Design` (RS0030, see
`BannedSymbols.txt`; CompactUI is exempt).

- `Media.Texture(key, dataSource)` - async cache: returns `null` until the
  wrap is ready, never blocks the UI thread (no `.Result`). Evict via
  `Media.Evict(key)`.
- `Media.Image(wrap, size, fit, rounding, border)` - inline image;
  `ImageInRect(drawList, …, rounding, corners)` for draw-list placement -
  pass `ImDrawFlags` corner flags to round only the edges that sit on a
  window/child silhouette (e.g. `RoundCornersRight` for a backdrop that
  hugs the frame); `Sprite(handle, uv0, uv1)` for atlas regions;
  `SpriteInRect(drawList, handle, min, max, uv0, uv1)` for draw-list
  placement in manually composed previews/rows.
- `Media.Avatar(wrap, size)` - profile images with rounding/badge support.
- `Media.Placeholder(size)` / `PlaceholderInRect` - empty-state tile while
  textures resolve.
- `Media.ImageBackground(wrap, content)` - tinted card/surface backdrops.
- `ImageFit.Fit` letterboxes, `Cover` crops via UVs, `Stretch` fills.

## Profile (community pages)

`SpheneUI.Profile` renders community-profile surfaces. The same helpers back
the Hub profile page, the Friends profile view, and CompactUI's profile
popout - a profile must look identical wherever it appears.

- `Profile.Banner(drawList, min, max, texture, theme, defaultTop, defaultBottom,
  rounding = null, corners = All)` - banner strip: cover-draws the owner's
  uploaded banner, otherwise a gradient from the owner's theme colors (falling
  back to the accent defaults). Always passes the *owner's* theme, never the
  viewer's. `rounding`/`corners` override the theme rounding and restrict which
  corners round - the hero uses `RoundCornersTop` for its full-bleed edge.
- `Profile.Hero(displayName, metaLine, profile, avatarWrap, bannerWrap,
  theme, …)` - full-width profile header **without card chrome**: the banner
  bleeds into its host surface (see below), the avatar half-overlaps the
  banner edge with a surface-colored cut-out ring plus an optional
  `presence` dot. The desktop composition, left to right: the identity
  column (name + optional NSFW chip, tag chips directly beneath - bound so
  it can never underlap the right-hand panel), the **thought bubble**
  floating above-right of the avatar for the status (a curved trail of
  three growing dots walks a quadratic bezier from the avatar rim into the
  bubble; ≤3 clipped lines, hover tooltip for overflow), and the right-
  docked **bio panel** ("About me" label + ≤3-line preview + "More…" →
  anchored scrollable flyout - the full bio never shifts page layout).
  `actions` (`HeroAction` records) dock beneath the bio panel when it
  exists, else at the banner's bottom-right; `drawStats` spans full width
  below the identity block. `actions` is the extension slot for future
  community features (house travel, etc.). Narrow headers degrade, never
  break: no room for the bubble → the status returns as an inline line;
  no room for the panel → a ≤140-char inline preview under the tags.
  Identity flow is re-anchored with `FlowIndentTo`/`FlowUnindent` inside a
  `ContentBounds` push - repositioned lines can never snap back beneath
  the nav rail.
- **Cardless full-bleed hero** - the hero is no longer wrapped in an
  `AccentCard`. The banner's horizontal bounds come from `host` (a
  `HeroHost` record: `Min`/`Max` + `CornerRounding`/`Corners` for drawn
  panels like the CompactUI popout, whose outer top corner shares the
  window rounding). With `host = null` - the page form - the banner bleeds
  to the enclosing scroll child's full width; when the hero is the page's
  first item it also covers the top padding so it lands flush on the pane
  edge and scrolls away with the content. The bottom edge dissolves into
  the window surface via an alpha ramp of `ImGuiCol.WindowBg` (fraction
  `HeroBannerBodyFade` = 45%), which adapts per host (hub pages vs.
  compact surfaces). A full-width hairline under the identity block
  separates the cardless hero from page sections below; embedded `host`
  hosts skip it. Mid-page previews pass their column rect as `host` so the
  banner does not bleed over surrounding content.
- `Profile.HeroSkeleton(accent, statsRows, host)` - loading twin with the
  same cardless silhouette: same banner bleed + fade, pulsing placeholder
  blocks for avatar/name/meta/status/stats.
- `Profile.StatTile(...)` - the dashboard tile (bare slot icon + value +
  label) used where profile stats still make sense.
- `Profile.HeroEdit` + the `edit:` parameter - own-profile edit mode: banner
  and avatar get click overlays (`OnBannerClicked`/`OnAvatarClicked`,
  typically routed to `ImageCropUi`), status and description render as inline
  inputs whose `StatusBuffer`/`DescriptionBuffer` commit via the
  `On*Committed` callbacks on Enter or focus loss. Buffers are seeded by the
  host when edit mode is toggled on, not by the hero.
- Editable tag chips - in edit mode the hero tag row gains a trailing "+"
  chip that opens an inline `InputTextWithHint` (`AddingTag`/`NewTagBuffer`/
  `TagInputFocusPending`), and each chip shows a "×" remove affordance on
  hover; add/remove fire `OnTagsCommitted` with the full comma-joined list.
  Only the × zone removes - clicking the chip body is inert.
- NSFW chip - rendered next to the name whenever `isNsfw` or editing; with
  `OnNsfwToggled` set it becomes a clickable toggle chip.
- `Profile.ThemeColorRow(label, ref enabled, ref color)` - single settings
  row for an optional theme color: a `Controls.ColorSwatch` LEADS the row
  on the left (always rendered, dimmed while disabled so it still reads as
  a color column), then label + slide toggle docked to the row's right
  bound; the swatch opens the styled ColorPicker4 popup. **Design-system
  rule for every settings row combining a toggle with extra controls:
  auxiliary controls go BEFORE the label/toggle, never behind or after
  the switch - switch tracks form one unbroken vertical column and may
  never shift based on optional trailing content.** Shared by the hub
  "Style" panel and `EditProfileUi`'s appearance card. Inside a
  `BeginDropdown` scope the picker opens through
  `Popups.ToggleFlyout`/`BeginFlyout` instead of
  `OpenPopup`/`BeginStyled` - the native popup pair loses its first frame
  against the dropdown's own `Popup` layer and focus pinning, while the
  flyout is a plain pinned window that opens on the first click. Everywhere
  else (in-window expanders, normal windows) the regular `BeginStyled`
  popup path is used - it is reliable there.
- `Profile.NsfwChipSize(active, scale)` / `Profile.NsfwChipAt(drawList, pos,
  active, scale, hoverT)` - draw-list chip renderer for NSFW badges anywhere
  (friend rows, lists). The active state always shows a check glyph so
  "enabled" is unambiguous; `hoverT` accepts an eased `Fx.HoverT` value.

## Anchored dropdowns (`Popups.BeginDropdown`)

An anchored menu that visually flows out of its trigger button - a sliver
under the button eases down and left to the content size, right-aligned
with the button's right edge so it expands leftward. While open the
trigger is repainted as a flat-bottomed **tab** in the menu's surface
color (icon + label in accent). Instead of a window border plus a seam,
`DrawDropdownTab` strokes **one closed path around the union of button
and window** (`AddDropdownOutline`): the outline climbs the button's
rounded top corners and rejoins the panel, so both read as a single cast
shape - the window's own `ImGuiCol.Border` is transparent - and the
window alpha-fades during the grow:

- Trigger: call `Popups.ToggleDropdown(id)` inside the button's click
  handler (it captures `ImGui.GetItemRectMin/Max`, which is still the
  button's rect then).
- `Popups.BeginDropdown(id, contentSize, icon, label)` - returns true
  while visible; render content after it and pair with
  `Popups.EndDropdown()`. Pass the trigger's icon and label so the
  repainted tab keeps its glyph and text.
- The window background is forced opaque (`Surface` at alpha 1) - the
  themed popup bg is translucent and would let the host window shine
  through as displaced content. The cove left by the window's rounded
  corner next to the tab is filled so the junction stays solid.
- `contentSize` is the *maximum* opened size - the height auto-fits:
  `EndDropdown` measures the content bottom each frame and the next
  `BeginDropdown` clamps `size.Y` to it, so menus never reserve dead
  space below their cards nor clip them. For side-by-side columns do
  **not** use nested `BeginChild`s - a fixed-height child scrolls its
  content internally, which shifts the measured bottom and feeds back
  into the scroll height (visible as constant height flicker), and it
  swallows wheel events meant for the outer scroll area. Instead
  position the cursor per column (`SetCursorScreenPos`) and push a
  `ContentBounds` range so `SectionCard`/`AccentCard` (which clamp their
  right edge to `Layout.ContentRightBound`) and bound-docked controls
  respect the column width. The window clamps inside the viewport and
  flips above the button near the bottom edge (tab repaint mirrors).
- The window is flagged `ImGuiWindowFlags.Popup` + `NoScrollbar` and
  focus-pinned via `SetNextWindowFocus` while open, so it always renders
  above the host window - while open/close stays manually animated. The
  pin yields while an inner popup (combo) or flyout (color picker) is
  open, so the overlay can come to front.
  For scrollable menu content use `Layout.BeginScrollArea` inside: it
  provides wheel scrolling with edge fades instead of a native bar.
- Close paths: clicking the trigger again, outside click (popups and
  flyouts opened from inside don't count), Escape while focused, or
  `Popups.CloseDropdown(id)`. `Popups.ResetDropdowns()` drops all
  dropdown and flyout state on window/page resets.

Prefer this over `BeginStyled` for button-anchored menus that should feel
connected to their trigger - and over `Layout.BeginExpander` only when the
menu genuinely has to float *above* other content instead of living in the
page flow.

## Collapsible sections (`Layout.Collapsible`)

The designed section foldout - prefer it over raw `ImGui.TreeNode` for
collapsible sections (`Layout.Tree` stays as the thin leaf-node wrapper):
a tonal header pill (square chevron zone + seam + label, optional icon and
muted right-aligned `hint`, e.g. "2 active") that opens a bordered body
sharing the header's bottom hairline. State persists per window via
`ImGui.GetStateStorage()` keyed on the label; `"##id"` suffixes stay hidden.
`setOpen:` force-opens for deep links (SetNextItemOpen equivalent),
`defaultOpen:` seeds the closed-state default. Use it for filters, help
foldouts and grouped settings - CharaDataHub is the reference.

## Inline expanders (`Layout.BeginExpander`)

An expanding panel rendered **inside** the host window's layout flow -
unlike `BeginDropdown` it is not a separate overlay window, so it needs
no focus pinning, z-order or outside-click handling at all and simply
vanishes with the host window. `Layout.ToggleExpander(id)` flips the
open state (typically from a button click), `BeginExpander(id)` returns
true while visible - render content after it and pair with
`EndExpander()`. The region grows from zero to the measured content
height with a smoothstep height + alpha animation and clips content
while animating; once fully open it is exactly content-sized with no
inner scrolling (the host page scrolls instead). External close paths:
`Layout.SetExpanderOpen(id, false)`; `Layout.ResetExpanders()` drops all
state on window/page resets. The panel paints its own background on the
parent draw list (a barely-lifted copy of the host window's `WindowBg`)
while pushing `ChildBg` transparent - child windows paint `ChildBg` as
their own background, so an opaque push would let nested children (the
hero's text block) overlay the accent-tinted card surfaces beneath them.
The tone is `WindowBg` + 4% - semi-transparent card fills composite
identically to the page.

Used by the hub hero's "Style" panel: the preview switches between the
live profile card and the friends-list row demo via `Layout.TabStrip`,
and the settings cards stack at full width below it, filtered to the
controls that apply to the active preview tab. Also used by the Release
Notes page: each changelog row toggles `changelog_rel_{version}` so the
newest release opens once via `SetExpanderOpen` while every entry stays
user-collapsible.

### Flyouts (`Popups.BeginFlyout`)

Small anchored panels for controls inside a dropdown that need their own
floating surface (the theme color picker). `Popups.ToggleFlyout(id)`
toggles and captures the trigger rect; `BeginFlyout(id)` opens an
`AlwaysAutoResize` window at the trigger's right edge (below it,
flipping above near the viewport bottom), focus-pinned and alpha-faded
like a dropdown - outside clicks and Escape close it, other flyouts
close when a new one toggles. Pair with `Popups.EndFlyout()`.
The flyout window is deliberately **not** `ImGuiWindowFlags.Popup`:
`ImGui.Begin` resolves a Popup-flagged window's PopupId from the live
popup stack, which is empty for a manually begun window - the bogus id
makes open/close/focus erratic (opens only after several clicks). A
plain pinned window plus the manual close paths open reliably on the
first click. The dropdown's focus pin and outside-click handler both
yield while `AnyFlyoutOpen` holds, so flyouts stay interactive above
their parent.

#### Contextual editors (`Components/CommunityComposers.cs`)

The community page creates and edits its content **in place** - no
separate studio route. Section affordances (`+` on News/Events, the
gallery camera, banner/avatar pens, the Listed pill, Rules/About pens)
open anchored composer flyouts or inline expands; saves go through the
feed APIs (`GroupUpsertCommunityNews/-Event/-GalleryImage`) or
`CommunityComposers.PatchCommunity` (`GroupSetCommunity` on a `with`-
patched aggregate), then the host refetches its cached DTO. Structural
editors (links list, staff matrix, album manager, activation) keep
routing to the admin surface via `OnEditLinks`/`OnManageStaff`/
`OnManageAlbums`/`OnOpenStudio`.

Multi-field structural edits **stage locally, commit once**: the album
manager binds an `AlbumEditDraft` (name, event link, cover, audience,
member list) per album, surfaces a Save/Discard affordance only while the
draft differs, and keeps the draft across background refreshes - one
deliberate save is one upsert, never a write per keystroke.

#### Chronicles (`UI/Chronicles/`)

Personal publishing uses the durable term **Chronicles** in every
user-facing string - "Write a Chronicle", "My Chronicles", "Edit
Chronicle". Internal wire/database identifiers stay `Blog`
(`UserProfileBlogPostDto`, `blog:` entity tokens, `BlogVisibilityScope`)
for compatibility; never rename them for cosmetics.

- **Authoring is a dedicated workspace**, not a flyout: the profile
  Chronicles tab flips into the editor (~70% form column - title, cover,
  `MarkdownComposer` - against a ~30% settings rail with the privacy
  selector, pinned/draft toggles and publishing metadata; stacks below
  ~560·scale px). Footer: destructive Discard left, Live Preview +
  Publish/Save right.
- **Live Preview previews once** - `ChroniclePreviewUi` is a movable,
  resizable draft window bound to the shared `ChronicleComposer`; it
  renders `ChronicleReaderView`, the *same* surface the own/friend reader
  lists draw - never a second renderer. Writes stay inert; a draft marker
  sits in the window chrome.
- **Author identity is part of the surface** - every Chronicle card,
  detail and preview leads with the author's avatar + recognizable
  alias + publication date. No raw UIDs; the author's stable uid stays
  the data key.
- **Privacy selector speaks plainly** - Everyone / Friends / Only me
  (wire `Public`/`Pairs`/`Private`); server enforces on every read path.
- **Covers** reuse the content-addressed community-media pipeline
  (`CoverMediaHash`) and the photo studio's banner preset - never
  hand-sized crops.

## Micro-animations (`SpheneUI.Fx`)

Reusable, framerate-independent (DeltaTime-driven) hover animation
primitives:

- `Fx.HoverT(id, hovered, speed)` - eased 0..1 hover progress per widget id
  (`ImGui.GetID` with the widget label), smoothstepped ramp in/out.
- `Fx.HoverRow(drawList, min, max, accent, t, radius)` - standard hover
  treatment: tinted fill + accent border fading in.
- `Fx.Sheen(drawList, min, max, phase, bandWidth, tint)` - clipped
  traveling light band; cycle `phase = (time * speed) % 1.5` for a sweep
  with a rest interval (phases outside 0..1 draw nothing).
- `Fx.Constellation(drawList, min, max)` - static star-chart window
  backdrop: a sparse field of twinkling stars (a few accent-tinted),
  faint chart lines between close neighbours and a handful of bright
  stars with cross glints. Draw after the window surface, before
  content; opt in per window via `ChromeWindowPattern`.
- `Fx.RailMotes(drawList, min, max)` - slow aether motes drifting upward
  inside a nav rail band; painted by `Layout.BeginNavRail` so every
  sidebar shares it.
- `Fx.AmbientGlow(drawList, min, max, radius, color, strength)` - two
  expanding accent washes reading as light spilling from an element's edge.
  Cheap (two draw calls); reserve for spotlight moments (primary actions,
  active nav pills) - not every control.
- `Fx.Reset()` - clears cached animation state (wired into hub resets).

## Owner-selected row effects (`Profile.RowFx`)

Friend-list rows (and any future row/card surface) render the **row
owner's** chosen effects - not the viewer's settings:

- `Profile.RowFx(drawList, min, max, accent, hovered, hoverT, theme,
  radius)` - paints the owner's theme layers behind row content: the
  `RowIdle` flags (always visible) then the `RowHover` flags driven by
  the eased `hoverT`. Any animated row image must be drawn *beneath* this
  call. A flags-taking overload (`ProfileRowFx hover, ProfileRowFx idle`)
  serves ad-hoc surfaces like the style preview strip.
- `ProfileRowFx` (Services, `[Flags]`) - every set bit draws one layer,
  so owners stack effects freely: hover mask `Glow` (fill + accent
  border), `Sheen` (traveling light band), `Pulse` (fast breathing accent
  border), `Sweep` (accent comet orbiting the border), `Lift` (drop shadow
  + top edge highlight); idle mask `Tint` (subtle accent gradient),
  `Pulse` (slow breathing border), `Sparkle` (twinkling accent sparks),
  `Wave` (continuous slow luminance drift). The retired `EdgeBar` bit
  stays in the enum so stored themes still parse - masks strip it and
  nothing draws it.
  `HoverMask`/`IdleMask` bound what editors offer; rendering masks
  defensively too.
- `SpheneProfileTheme` serializes the picks alongside the colors:
  `rowHover`/`rowIdle` keep the legacy single-name contract (the first
  set flag's name, so older clients render a sane effect), while
  combinations additionally write `rowHoverFx`/`rowIdleFx` with the full
  comma-joined flag list (`"Glow, Sheen"`). New clients prefer the Fx
  keys on parse; unknown names fall back to defaults. The same dual-key
  scheme carries the profile-card effects `cardHover`/`cardIdle` +
  `cardHoverFx`/`cardIdleFx` (defaults `None`, so untouched profiles keep
  their plain card).
- Profile-card effects reuse the exact same `ProfileRowFx` vocabulary:
  `Layout.AccentCard` accepts `fxHover`/`fxIdle` and stacks the layers on
  the card chrome via `Profile.RowFx` - drawn on the card's chrome
  channel so they sit above the card background but beneath the content.
  `Profile.Hero` passes the theme's `CardHover`/`CardIdle`, so the real
  card, the style preview and every other hero render identical effects
  from the same code path. The card's built-in ambient animation (the
  `Chrome.AccentCardBackdrop` sheen band) is gated by the theme's
  `cardShimmer` flag -
  Hero forwards it as `animatedBackdrop`, so a card with shimmer off and
  no picked effects is fully still. Defaults to on; only the profile
  card honors it, other AccentCards keep their chrome.
- `Profile.FxChips(groupLabel, ref value, allowed, accent)` renders the
  effect picker as wrapping toggle pills. The style preview renders the
  REAL `Profile.FriendRow` - the identical renderer the friends list
  uses (`FriendRowContent` carries avatar/name/presence/status/NSFW;
  the preview feeds it the owner's own live profile data) - so what you
  see in settings is pixel-for-pixel what friends see. Same for the
  card tab: the preview IS `Profile.Hero` bound to the unsaved buffers.
  Owners pick effects on the hub's Style page (`SpheneHubPage.ProfileStyle`);
  its two
  preview tabs gate which settings show: the "Profile card" tab carries
  accent + banner controls + card effects, the "List row" tab carries
  accent + row effects + the supporter row-image upload - controls only
  appear where they apply. The tab strip + preview form a sticky
  header: their flow space is reserved via `Dummy` while the block is
  drawn last, clamped between its natural slot and the child's visible
  clip top - settings scroll underneath, the preview stays put.

Used by hub friend rows (per-friend accent + theme drive idle/hover,
chevron slides in) and `Profile.StatTile` (eased fill/border).

## Animated images (`Media.Animated`)

- `Media.Animated(key, byte[])` - cache-owned `AnimatedImage` decoding
  off the UI thread: GIF/WebP frames are composited into a horizontal
  atlas in a single texture wrap with per-frame durations (PNG decodes to
  a single static frame). Capped at 48 frames and 512px per side.
- `AnimatedImage.FrameIndexAt(time)` / `IsAnimated` - looping frame
  lookup; `AnimatedInRect(drawList, anim, min, max, fit, tint, rounding)`
  draws the current frame with the standard fit/tint/rounding pipeline.
- `Media.Evict(key)` drops both the static and animated cache entries -
  call it whenever the underlying bytes change.

Used for the supporter-only animated row background: owners upload a PNG
or GIF via the Style page - `ImageCropUi` (`CropKind.RowImage`) locks
the selection to `Profile.RowImageAspect` (4:1, the exact strip aspect)
and outputs ≤512×128/≤400KiB; multi-frame sources stay animated
(crop+resize applies to every frame, GIF-encoded on save). The server
enforces the supporter gate + limits in `UserSetProfile`, and
`UserProfileDto.RowImageBase64` carries it to viewers.
`Profile.RowImageStrip` renders the strip left-anchored at row height and
fades to TRUE transparency toward the right edge - it tessellates the
rounded strip as a textured perimeter fan (`PushTextureID` + per-vertex
atlas UVs and alpha), so the image's own opacity dissolves and the row
background shows through instead of blending into a solid color;
readability dimming is baked into the vertex color. `Profile.RowBackdrop`
is the shared composition - row image beneath the owner's idle/hover
`RowFx` layers - used identically by the friends list and the style
preview's `FxPreviewStrip`, so previews can never drift from real rows.
Drawn only when the row owner `IsSupporter` (non-supporters can't smuggle
one in via stale data). `""` clears it server-side; `null` preserves the
stored image on partial updates.

`ImageCropUi` (UI/Components) is the interactive crop window used for all
profile images: `Open(CropKind, bytes, w, h, onConfirm)` shows the source
with an aspect-locked selection (1:1 avatar, 3:1 banner, 4:1 row strip -
move by dragging inside, resize via corners, click outside recenters).
"Apply" crops in source pixels, downscales to the server limits and
PNG-encodes on a task (GIF-encodes when the source was animated) - only
the cropped bytes are handed to `onConfirm` on the UI thread.
- `SpheneProfileTheme` (Services) - the owner's stored theme
  (`accent`/`bannerTop`/`bannerBottom`/`rowHover`/`rowIdle`/
  `rowHoverFx`/`rowIdleFx`/`cardHover`/`cardIdle`/`cardHoverFx`/
  `cardIdleFx`/`cardShimmer`/`bannerTopOver`/`bannerBottomOver`, JSON,
  forward-tolerant). Null fields mean "use design defaults"; parse
  failures fall back to defaults. `bannerTopOver`/`bannerBottomOver`
  blend that half's color (55% alpha) over the banner image instead of
  the image replacing the gradient - `Profile.Banner` honors all of them,
  so standalone and popout profiles show the same look. Rounded gradient
  fills render through
  `Media.FillRoundedGradient`, which tessellates a vertex-colored fan
  with bilinear corner colors plus a 1px alpha fringe (MultiColor rects
  can't round; the earlier 2×2-texture + `AddImageRounded` path visibly
  offset the blend under rounding).
- `ProfileVisibility` flags (SpheneAPI `Data/Enum`) - owner-controlled
  section hiding, enforced server-side in `UserGetProfile`. Hidden sections
  arrive empty; render them as absent rather than "hidden" placeholders,
  except on the owner's own view.

## Controls quick reference

- `Controls.Toggle` - full-width label+switch row; `Controls.Switch` renders
  the pill inline for rows composed via `FieldRow` or custom layouts.
- `Controls.Checkbox` - compact themed box for table cells/`SameLine` rows.
- `Controls.Combo` - typed enum/list combos; `Controls.BeginComboBox` +
  `Controls.EndComboBox` for arbitrary preview text and item content
  (styled popup, `ImGuiComboFlags` passthrough, stack-dispose pattern like
  `Popups.BeginStyled`).
- `Controls.ColorEdit` / `Controls.ColorPicker` - themed color fields;
  `ColorPickerSwatch` hosts the styled picker popup.
- `Controls.FieldRow(label, trailingIcon, fill)` - label + fill-width
  control row; replaces manual `GetContentRegionAvail` width math.
- `Layout.Tree` - themed foldout; `Layout.Collapsible`/`BeginExpander` for
  richer sections.
- `Tables.Begin(id, cols, flags, size, accent)` - slim themed data table in
  the settings-card language: tight `CellPadding`, subtle row striping, and -
  for tables that call `HeaderRow` - a rounded **card frame** drawn on dispose
  (`Layout.AccentCardRounding` radius, `Chrome.CardBorderThickness`,
  accent border @0.38 like AccentCard). ImGui's square `BordersOuter*` flags
  are stripped in `Begin` - the frame replaces them; inner dividers callers
  set are kept. Tables without `HeaderRow` are treated as invisible layout
  plumbing and get no frame. `ImGuiTableFlags.ScrollY` tables hide the native
  scrollbar entirely - the inner scroll child is created with
  `ScrollbarSize = 0` and edge fades (`Chrome.ScrollFades`, incl. the
  chevron pill) paint over the table body on dispose, starting below the
  frozen header row. `Tables.Column(label, flags, width)` renders header
  labels uppercase (same quiet caps as rail category labels) -
  `Tables.HeaderRow` draws the designed header chrome: a tonal rounded
  cap inset inside the frame (concentric corner radii), quiet vertical
  ticks between columns, an accent hairline at the header/body seam and
  muted caps labels (the native `TableHeaderBg` fill stays transparent).
  All of it paints into the OUTER window draw list captured in `Begin` -
  after `BeginTable` the current window is the inner scroll child, so
  header chrome drawn there would scroll away with the body. Header label
  inset is forced by writing `table->CellPaddingX` before
  `TableHeadersRow` runs layout - the value ImGui snapshotted at
  `BeginTable` is refreshed, so labels always honor the design inset.
  `Tables.HeaderFitWidth(label)` returns a compact fixed width that fits
  the caps header plus sort arrow - prefer it over all-stretch layouts.
  Rows: `Tables.BeginRow` for plain rows, `Tables.Row()` +
  `Tables.SetColumn(i)` for explicit column order,
  `Tables.SelectableRow(id, label, selected, flagged, accent)` and
  `Tables.SelectableRowSpan(id, selected, flagged, accent)` render
  click/hover/selection as an inset rounded chip in the accent color
  (same language as `Lists.Item` - soft fill + accent outline, never a
  flat edge-to-edge bar); the Span variant leaves the label invisible and
  the cursor in column 0 - the standard for click-to-select rows (pair with
  `CellText`/`PathText`, which are `Cell`/`PathCell` without the column
  advance). `Tables.Cell`/`MutedCell`/
  `NextColumn` emit cell content. `Tables.EmptyRow(message)` draws a
  centered muted note across the whole table width (regular cells clip to
  their column - never put empty-state text in a narrow first column).
  `Tables.PathCell(text, muted)`
  truncates file/game paths from the LEFT so the informative file name
  stays visible (full path on hover). Raw `ImGui.BeginTable`/
  `EndTable` and `ImRaii.Table` are banned outside `UI/Design` (RS0030).
  Never nest anything that re-splits the window draw-list channels inside
  a card's content: `ImGui.Columns`, `SectionCard` - and every surface
  helper (`Surface`, `SurfaceFlat`, `HeadedSurface`, `HeadedRowGroup`,
  `AccentCard`, `SectionCard`, `RowGroup`) - all render their fill by
  splitting channels, `ImDrawListSplitter` cannot nest, and the outer
  card's deferred background ends up drawn over its own content. Inner
  panels inside cards stay flat: hairlines, muted captions, bounded
  groups. Since Run 031 a depth counter makes a nested call degrade to a
  channel-free outline instead of corrupting the draw list, and Design
  Lint flags it as `nested-surface`.
- `Lists.Begin(id, width, height, title)` - bordered pick list with
  muted header, scroll fades and `Lists.Item(id, label, selected,
  accent, muted, icon, tooltip)` rows that read as **button chips**: an
  inset rounded rect (~2px side inset) with a soft accent fill and a
  visible accent outline when hovered/selected - never an edge-to-edge

- **Event media has two distinct assets** - the **event banner** (3:1,
  1200×400, `ImageMediaHash`) renders in the detail header + attendee page;
  the **event preview image** (16:9, 800×450, `PreviewMediaHash`) renders in
  list rows and selection cards (falls back to the banner when absent). Both
  are optional - never show broken-image placeholders. The studio enforces
  both via `PhotoStudioTarget.EventBanner`/`EventPreview` (fixed aspect +
  target width); never hand-size event crops in the UI. The planner editor
  offers both pickers labelled by purpose; unsaved picks ride
  `PreviewBase64`/`ImageBase64` into Live Preview with no server write.

- **@ mentions resolve to stable identity** - one shared typeahead in
  `MarkdownComposer` (`MentionProvider`, host-supplied): a whitespace-bounded
  trailing `@query` opens the candidate flyout; rows show the recognizable
  alias + a disambiguating detail ("Syncshell member", "Friend"); selection
  stores `@uid` (the same token the reference flyout builds and the server
  resolves). Never raw UIDs, never alias-as-key, never a second picker.

- **Toolbar edits respect the caret** - `MarkdownComposer` routes every
  formatting action through one edit queue that executes inside the
  input's `CallbackAlways` callback (`InsertChars`/`DeleteChars` on UTF-8
  byte offsets): selected text wraps, an empty selection inserts the marker
  pair at the caret and lands it inside, links take the selection as the
  label. The last focused-frame snapshot is what a toolbar click splices
  around - no appending at the document end, no clipboard tricks.
  `Controls.InputTextMultiline` carries the callback-capable overload;
  editors outside `UI/Design` must use it, never the raw binding.

- **Event authoring previews once** - the Event Planner uses the full
  attendee-facing Live Preview (`OnPreviewEvent`), never a second inline
  markdown preview under the description editor; the editor shows a small
  hint that opens the real preview instead.
  block. `icon` draws a muted/accent FontAwesome glyph before the label
  (pet markers etc.); `gameIcon` (icon id) draws the in-game UI texture
  via `Media.GameIcon` instead - e.g. the glowing journal-style job
  icons (0623xx classes, 0624xx jobs). `tooltip` overrides the hover
  text (full label shows automatically when the label ellipsizes).
  `EmptyHint(text)` renders
  the empty state. Width `-1` fills the cell/region; height `-1` fills
  to the view bottom. The box is bounded to its own width, so it
  composes inside `SameLine` groups and table cells - do NOT put a
  full-width `Layout.Separator` inside a `Group` you intend to
  `SameLine` past (it inflates the group bounds and pushes later
  columns off-screen). Raw `ImGui.BeginListBox`/`ImRaii.ListBox` are
  banned (RS0030). Do NOT put fill-to-bottom children inside
  layout-table cells - the row's `CellPadding` lands below the child
  and produces phantom page scroll; use sibling `ImRaii.Group` columns
  or an absolute bottom edge instead.
- `Layout.BeginPane(id, title, size, scrollable, padding)` - the same
  titled bordered box for arbitrary content (toolbars + tables). `size`
  (0,0) fills the cell/region; `scrollable: true` keeps scroll fades;
  `padding` overrides the standard ~5px inner content inset - panes
  never draw flush to their border.
- `Buttons.Row(id, params Spec[])` - toolbar row that never clips:
  `Buttons.Spec(icon, label, tooltip, onClick, enabled, danger)` specs
  render as icon+label buttons while they fit the bounded width; the
  rightmost buttons degrade to icon-only (label moves into the
  tooltip) and whatever still overflows wraps to a new line. Keep
  labels short - long descriptions belong in `Tooltip`.

## Shared components

Reusable sections that render both standalone and embedded in the Hub:

- `PairRequestsSection` (`UI/Components/`) - incoming/outgoing pair request
  list; hosted by `PairRequestsUi` and the Hub Friends page.
- `MarkdownFormatterComponent` (`UI/Components/`) - split editor with live
  `MarkdownRenderer` preview; hosted by `MarkdownFormatterUi` and the Hub
  Tools > Formatter page.

## Markdown engine (announcements & articles)

`MarkdownRenderer.RenderMarkdown(text, wrapPos, imageSource)` parses through
**Markdig** (`UI/Design/Markdown/`) and renders the AST with ImGui - the plugin
never emits HTML. Parsed documents are cached per body text so redraws do not
reparse.

**Vertical rhythm** - one rule, `GapBefore(block)`: each block type owns a
top margin emitted only *between* siblings, so first children and nested
containers never double margins. H1/H2: X16, H3-H6 + footnote section: X12,
paragraphs/lists/quotes/code/tables/rules/containers/HTML: X8, invisible
blocks (link refs, empty): 0, fallback: X4. List *items* stay compact at X2
(set in `RenderList`, not `GapBefore`); soft line breaks are inline flow and
never produce block gaps. Supported surface:

- `#`-`######` headers (H1/H2 underlined), `**bold**` (faux-bold double-draw),
  `*italic*`/`_italic_` (synthetic oblique - glyph vertices sheared in the
  draw list, same text color; no loaded font ships an italic face),
  `***bold italic***`, `~~strike~~`,
  `__underline__`, `~sub~`/`^sup^`, `++inserted++`, `` `code` `` (mono + bg).
- `- [ ]` / `- [x]` task lists, `- ` / `* ` / `1. ` lists with nesting.
- `> ` blockquote (muted + accent bar; `>>` nests deeper).
- `---` / `***` / `___` - hairline separator.
- `[text](url)`, `<https://…>` and bare `https://…` autolinks - underlined,
  clickable (`Util.OpenLink`); http/https and `mailto:` are clickable
  (`IsSafeLinkUrl`), anything else renders muted (javascript:/file: never
  execute). Image fetches remain gated by `IsSafeUrl` (http/https only).
- Bare `name@example.com` addresses are rewritten to `mailto:` links at parse
  time (AST transform - autolink extension only covers `mailto:`/`www.`/`http`);
  a detected email stays plain text inside links and code.
- `![alt](url)` - full-width image via the lazy `imageSource` callback.
- ` ``` ` fenced code blocks - framed mono box, language label, copy button.
- GFM pipe tables - measured columns, header rule, cell alignment honored.
- `[^1]` footnotes - raised superscript `[n]` marker (hover = content
  tooltip, click = jump to definition); definitions collected in a separated
  section at document end, each with a return arrow that jumps back to the
  reference. Markdig internals never reach the UI.
- `||spoiler||` - opaque concealment (opaque FrameBg rect over the text, no
  text shows through); click toggles reveal and conceal again; state is keyed
  per spoiler occurrence and session-scoped.
- `:::callout info|warning|danger|success` - icon + colored accent bar block.
- `:::side img="url" side="left|right" width="38"` - image column beside the
  text column (sidecar; text does not flow under the image).
- `:::anything-else` - bordered container box.
- `&lt;color=name|#rrggbb&gt;` / `&lt;u&gt;` / `&lt;size=N&gt;` Sphene tags
  (sizes clamp at 32 pt through cached real font handles - no raster
  upscaling), `&amp;`/`&lt;` entities, `\` escapes.
- **HTML: intentionally not rendered.** Raw HTML tags/blocks display as muted
  literal text - nothing is executed or interpreted (no `<details>` expansion,
  no `<strong>` formatting, no script). Authors use markdown equivalents.
- **Math: no LaTeX typesetting.** `$x$`/`$$x$$` render as muted mono source
  text - real TeX layout would need a glyph/raster pipeline ImGui does not
  provide; kept as readable source rather than pretending support.
- Soft line breaks render as breaks (GitHub-issue convention, `breaks: true`
  equivalent).

**Syntax provenance & conflicts:**

- CommonMark core: headings, lists, blockquotes, code, links, emphasis.
- GFM extensions: pipe tables, task lists, autolinks, footnotes, math.
- Discord-compatible extensions (syntax compat only - Sphene presentation,
  never Discord's): `__underline__`, `||spoiler||`, `>>>` multiline quote.
- **Conflict - `__text__`:** CommonMark bold vs Discord underline. Chosen:
  **underline** (authors write Discord-style; `**` remains bold).
- **`>>>` semantics:** a `>>>` line quotes that line and everything to
  end-of-message as ONE blockquote (fence-aware preprocessing rewrites the
  tail to `>` lines; CommonMark would nest three levels). `>>` still nests.
- Unsupported Discord syntax (renders literal/muted, by design): `#channel`/
  `@user`/`@role` mentions, `<:emoji:>`, unix timestamps `<t:…>`, `-#`
  subtext, masked-link embeds suppression `<>`.

Authoring rules for announcements: the body is canonical markdown; the hero
image always travels via the `BannerImageUrl` field, never inline; URLs are
http/https only. The same body feeds the website - renderers stay responsible
for their own platform's presentation.

## Supporting services

- `UiSharedService` (partial: core / `UiSharedService.CachePaths.cs` /
  `UiSharedService.OAuth.cs` / `UiSharedService.ServerSelection.cs`) -
  shared window plumbing: fonts + `SpheneUI.Initialize`, localization,
  notes helpers, cache/download-folder setting blocks, OAuth + server
  selection blocks, pending window sizes.
- `PluginInstallerService` (`UI/Components/`) - third-party plugin status
  checklist (intro + settings), repo registration and
  install/enable/disable through Dalamud internals via reflection.

## Design violations

`SpheneUI.Lint` is a runtime checker, toggled on the Diagnostics settings
page ("Design Lint" card). Each frame it records:

- `overhang` - control extends past the active card/region bound.
- `off-grid` - spacing/size value not on the grid (incl. `Spacing.Custom`).
- `no-theme-scope` - design control drawn outside a theme scope.

Violations get red outlines + rule labels via a foreground-draw-list overlay
and are listed in the card. A "Design System" styleguide card next to it
renders live controls for visual checking.

## Extending the system

1. Recurring raw-ImGui patterns used in **two or more windows** must become a
   facade helper in the matching `SpheneUI.<Module>.cs` partial - don't grow
   window-local helpers.
2. New spacing/sizing values go through `Spacing`/`Metrics` tokens; never
   literal pixels or `ScaledVector2` literals for gaps.
3. New helpers must validate themselves: call `Lint.CheckBounds(context)`
   after drawing and use `Space` tokens for spacing parameters.
4. Everything public gets an XML doc comment explaining *when* to use it.

## Rules recap

- One entry point: `SpheneUI.*`. No new `SpheneDesign`/`SpheneColors`/
  `SpheneWindowChrome`-style parallel systems.
- Text via `Text.*`, icons via `Icons.*`, values via `Controls.*`, structure
  via `Layout.*`, chrome via `Chrome.*`, popups via `Popups.*`, images via
  `Media.*`.
- Never `.Result`/`.Wait()` on texture loads - `Media.Texture` resolves async.
  The same rule applies to network/API calls in draw code - start a task,
  poll `IsCompletedSuccessfully`, never block the UI thread.
- CompactUI is the only exempt window; it owns its styling end to end, along
  with its private components under `UI/Components`/`UI/Handlers`
  (folder rows, pair rows, top tab menu, tag/id handlers - see the
  `.editorconfig` RS0030 exemption list for the exact set).
- Chromeless overlay hosts (`PopupHandler`, `DownloadUi`, `SpheneIcon`) keep
  `UseSpheneChrome=false` - they draw no window chrome, but their *content*
  must still be fully facade-styled (`Popups.BeginStyled`, `Text.*`,
  `Controls.*`).
- ShrinkU is a separate assembly (SpheneClient references it, not vice versa)
  with its own styling layer - it cannot consume `SpheneUI` and is outside
  this system's scope. Its windows render inside Sphene's `WindowSystem`
  via `ShrinkUHostService`.
- Raw `ImGui` primitives stay allowed where a facade call would be wrong:
  `ImGui.Separator()` inside tooltips, native context menus and `ImGui.Columns`
  headers; `MenuItem`/`BeginCombo` popups with custom item content; low-level
  layout calls (`SameLine`, `BeginChild`, `PushStyleColor`, `SetNextItemWidth`)
  inside bespoke draw code. Standard controls (`Button`, `Checkbox`, `Input*`,
  `Selectable`, text, separators in regular flow) must go through the facade -
  in table cells use `Controls.Checkbox`, not the row-spanning `Toggle`.
- Enforcement is compile-time: `BannedSymbols.txt` (RS0030 via
  BannedApiAnalyzers) bans raw image APIs and standard controls outside
  `UI/Design/**` and the exempt CompactUI set - the build fails on
  violations, so the facade stays the single source of truth.
- Two structural invariants the compiler can't see: card/surface helpers
  never nest (they re-split draw channels - the outer card's deferred fill
  overdraws its own content), and selection on cards/rows is a full
  outline + tinted fill inside existing bounds - never an edge strip.

## Community refresh (v1.1.29)

New shared vocabulary added for the community pass - usable on every page:

- `Theme.AccentScope(Vector4)` - scoped override of `Colors.Accent` so
  community surfaces tint controls with the owner's accent without a full
  theme push (LIFO nesting).
- `Fx.HoverLift` / `Fx.HoverLiftT` - eased lift (shadow + brighten + accent
  edge) for clickable cards; `Surface(interactive: true)` now uses it, so
  card hovers ease instead of snapping.
- `Layout.EmptyState(icon, title, hint?, actionLabel?, onAction?, actionIcon?,
  accent?)` - the standard "nothing here" block: centered muted glyph,
  title, hint and optional action. Replaces bare `Muted("No …")` dead ends -
  empty lists/tabs must explain themselves and offer the next step.
- `Text.Timestamp` / `Text.TimestampRight` - dimmer meta text for feed rows
  and card footers (times, counts, separators).
- `Popups.IsFlyoutOpen` / `Popups.CloseFlyout` - programmatic flyout
  control for anchors that are not ImGui items (e.g. the icon bubble).

## Violet pass (mockup language)

The neon-fantasy pass shifted the default theme to violet/magenta
(`#8B5CF6`/`#A855F7` family) and made every accent-driven fill ride a
shared two-stop ramp:

- `Colors.AccentRamp(accent)` → `(Deep, Hi)` - the darker violet-shadowed
  low stop and the lightened high stop every gradient fill samples:
  `PrimaryButton`/solid pills, active `NavRailSegmentItem`/`TopSlot` pills
  (with `Fx.AmbientGlow`), `SwitchVisual` on-tracks, `Segmented` active
  segments, `Badge(solid)`, `AccentCard` edges, the `TabBar`
  trace. Never hardcode a gradient pair - sample the ramp so custom themes
  follow.
- `Colors.AccentHi`/`AccentDeep` are the ramp stops of the current accent;
  `Colors.Accent` itself resolves `Theme.Current.AccentCyan` (still
  overrideable via `Theme.AccentScope`).
- `Chrome.RoundedHorizontalGradient(dl, a, b, radius, start, end, corners)`
  accepts an `ImDrawFlags` corner mask - edge slices round only the sides
  present in the mask, so segmented bars keep square inner corners.
- Informational blues stay: `Palette.CrystalBlue`/`DeepCrystal` remain for
  semantic highlights (stats, links, kind chips) - the mockups mix violet
  accents with restrained blue; don't retint them to violet.

Reusable components in `UI/Components/` (not design-layer - they need DTOs,
services or brand textures):

- `LinkChips` - `Card` (full-width link row; opens on click, copies URL on
  right-click), `Chip` (compact pill for hero/footers), `Grid`. Canonical
  link rendering everywhere; `SyncshellCommunityView.DrawLinkCard` forwards.
- `AvatarStack` - overlapping circle avatars + "+N" overflow chip.
- `LinkableText.UserLink` / `.SyncshellLink` - clickable names (tint, eased
  underline, hand cursor); navigation stays with the caller.
- `PersonCard` - compact profile preview for flyouts (mini hero + status +
  links + "Open profile"); same language as `Profile.Hero`.
- `EventCard` - date-block event card with countdown pill.
- `FeedItemRow` - activity-feed row (bare kind glyph, title, "Shell · time"
  meta, photo thumbnail) + `RelativeTime`.
- `NotificationInbox` - shared pending-items list (live pair requests with
  inline accept/decline + `NotificationInboxService` items: mod transfers,
  updates, area-bound consents). Backed by `PendingCount` for badges; used
  by the icon bubble/flyout, the hub bell dropdown and the hub pending card.
- `CommunityFeedService` - session cache for `GroupGetCommunityFeed`
  (news/events/photos across joined shells); `EnsureRequested()` dedupes,
  `Refresh()` manual reload, invalidated on hub reconnect/close.
