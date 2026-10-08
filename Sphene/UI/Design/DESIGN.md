# Sphene Design System

`SpheneUI` is the single entry point for all shared UI drawing in SpheneClient.
It replaces the previous split between `UiSharedService` helpers,
`SpheneDesign`/`SpheneColors` tokens, `SpheneWindowChrome` and
`SpheneCustomTheme` scopes — those entry points no longer exist.

The facade is a `static partial` class spread over topic files in
`UI/Design/`. Every window except **CompactUI** uses it; CompactUI keeps its
own styling system (`SpheneUI.Theme.CompactScope()` + `Compact*` tokens) until
it gets a dedicated design pass.

```
SpheneUI
├─ Theme        WindowScope / CompactScope / TooltipScope / ContextMenuScope, Current
├─ Spacing      fixed grid + semantic aliases (Space token type)
├─ Palette      static brand colors (crystalline blue/purple/gold)
├─ Colors       theme-resolved semantic colors (TextMuted, FrameBg, Accent…)
├─ Metrics      control metrics (ControlHeight, ControlRadius, SwatchSize…)
├─ Text         Plain/Wrapped/Colored/ColoredWrapped/Muted/Big/Bullet/Outlined/
│               PageHeader/SectionHeader/Header/GroupedCentered
├─ Icons        Icon/BoolIcon/Button/TextButton/ActionButton + sizing
├─ Controls     Toggle/ToggleChip/Checkbox/SmallButton/Slider*/Drag*/Input*/
│               Combo/LabeledCombo/Button/HeroButton/IconButton/DialogButtons/
│               Selectable/CollapsibleSection/
│               ColorEdit/ColorSwatch/GlyphSwatch/StatusDot/ProgressBar/
│               TransmissionBar/HelpText/InputTextWithButtons (+InputButtonSpec)
├─ Layout       AccentCard/SectionCard/ContentRegion/ScrollAreas/TabStrip/
│               NavRail(BeginNavRail/NavRailItem/NavRailCategory/
│               NavRailDivider/EndNavRail/BeginNavContent/EndNavContent)/
│               VerticalTabNav/Separator/SectionSeparator/DistanceSeparator/
│               Section/Callout(+title)/Group/VerticalSpace/IndentScope/Tree/
│               ScaledNextItemWidth/ScaledSameLine/WindowContentWidth/
│               CenterNextWindow/SetScaledWindowSize
├─ Chrome       TitleBar/Collapse/WindowBlur/ThemedWindowBlur
├─ Popups       BeginStyled/BeginModalStyled/BeginContextStyled/EndStyled
├─ Media        Texture cache/Image/ImageInRect/Sprite/Avatar/Placeholder/
│               ImageBackground + ImageFit (Fit/Cover/Stretch)
└─ Lint         runtime design checker (see "Design violations")
```

`SpheneUI.AttachTooltip` sits on the facade root and handles `--SEP--`
(`SpheneUI.TooltipSeparator`) segment splitting.

## Initialization

`UiSharedService`'s constructor calls `SpheneUI.Initialize(logger, mediator,
iconFont, uidFont, gameFont)` and `SpheneUI.Media.SetTextureProvider(...)`.
The service keeps owning and disposing the font handles; the facade only
holds references. `IconFont`/`UidFont`/`GameFont` throw if accessed before
`Initialize`.

## Spacing grid

All gaps, paddings and indents come from a fixed scale (unscaled values,
multiplied by `ImGuiHelpers.GlobalScale` via `Space.Scaled`):

```
1, 2, 4, 8, 10, 12, 16, 20, 24, 32
```

Prefer semantic aliases over raw steps: `Inline` (row gaps), `Row` (control
stacking), `Section` (group gaps), `CardGap`, `CardPadding`, `PagePadding`,
`ScrollInset`, `TitlePadX/Y`, `TitleButtonGap`. `Spacing.Custom(v)` is an
escape hatch that always records a lint violation — extend the scale instead.

## Theming

`WindowMediatorSubscriberBase.Draw()` wraps every design-system window in
`SpheneUI.Theme.WindowScope()` automatically — windows never push theme
colors themselves. CompactUI uses `CompactScope()` deliberately.

## Window chrome

Every `WindowMediatorSubscriberBase` window renders the design-system chrome
automatically: `PreDraw` adds `NoTitleBar | NoCollapse | NoBackground` and
hides the native resize grip, and `Draw` paints the themed window surface
plus a custom `Chrome.TitleBar` (drag region, title, icon, right buttons,
fold toggle) before `DrawInternal`. Content fades and skips while the fold
animation runs.

Per-window customization via `protected override` properties:

- `UseSpheneChrome` — opt out entirely (overlays/hosts: `DownloadUi`,
  `PopupHandler`, `SpheneIcon`; CompactUI via `AppliesOwnThemeScope`).
- `ChromeCollapsible` — off for `AlwaysAutoResize` windows
  (`StandaloneProfileUi`, `PopoutProfileUi`) where fold cannot shrink.
- `ChromeTitle` — defaults to `WindowName` minus the `##id` suffix.
- `ChromeIcon`, `ChromeTrailingText`, `ChromeRightButtons` — the default
  right button is a Close `Times` button honoring `ShowCloseButton`.
  `SettingsUi` overrides these for the Discord/Ko-fi buttons + version text.

Windows must NOT draw their own `Chrome.TitleBar` when `UseSpheneChrome`
applies — the base does it. Windows overriding `PreDraw`/`PostDraw` must
call `base.` so chrome flags and the hidden-grip colors stay balanced.

- `SpheneUI.Theme.Current` exposes the active `ThemeConfiguration`.
- `SpheneUI.Colors.*` resolves semantic slots (`TextPrimary`, `FrameBg`,
  `Accent`, `Border`, `ButtonBg`…) against the theme; `SpheneUI.Palette.*`
  holds fixed brand constants. Use `Colors` in controls.
- Tooltip/context-menu chrome comes from `TooltipScope()`/`ContextMenuScope()`
  — already applied inside `SpheneUI.AttachTooltip` and the `Popups.*Styled`
  helpers. Always open popup windows through `Popups.BeginStyled` /
  `BeginModalStyled` / `BeginContextStyled` + `EndStyled` so the popup frame
  itself is themed; pushing a scope *after* `BeginPopup*` only styles content.

## Content bounds

Cards and content regions push a right-edge bound (`Layout.ContentRightBound`)
that `Controls.BoundedAvailX()`, `FillRowWidth()`, `DockToRightEdge()` and
`SameLineIfFits()` respect. `ImGui.GetContentRegionAvail()` lies inside card
regions — never use it for row math; use the bound helpers so controls dock
flush to the shared edge instead of overshooting cards.

Raw `ImGui.BeginChild` regions do NOT push a bound — right-docked controls
(`Controls.Toggle`, `InputInt`, `Combo`…) inside them dock to the outer area
and overflow the child. Wrap such children with
`SpheneUI.ContentBounds.Push(new Vector2(minX, maxX))` / `Pop()` around the
content (or use `BeginScrollArea`, which pushes bounds itself).

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
top sheen when active. `SyncshellAdminUI` is the reference implementation.
The content scroll area starts right of the rail; rail and content scroll
independently with their own fade hints.

## Toggle vs. checkbox

`Controls.Toggle` always renders a full-width row: label left, switch docked
to `ContentRightBound`. Never place it in `SameLine` chains, table cells,
inside narrow children, or before text that must follow on the same row —
use `Controls.Checkbox` (compact themed box, label flows inline) there.

## Images

`SpheneUI.Media` owns every texture draw — raw `ImGui.Image`/
`ImDrawListPtr.AddImage` are banned outside `UI/Design` (RS0030, see
`BannedSymbols.txt`; CompactUI is exempt).

- `Media.Texture(key, dataSource)` — async cache: returns `null` until the
  wrap is ready, never blocks the UI thread (no `.Result`). Evict via
  `Media.Evict(key)`.
- `Media.Image(wrap, size, fit, rounding, border)` — inline image;
  `ImageInRect(drawList, …)` for draw-list placement; `Sprite(handle, uv0,
  uv1)` for atlas regions.
- `Media.Avatar(wrap, size)` — profile images with rounding/badge support.
- `Media.Placeholder(size)` / `PlaceholderInRect` — empty-state tile while
  textures resolve.
- `Media.ImageBackground(wrap, content)` — tinted card/surface backdrops.
- `ImageFit.Fit` letterboxes, `Cover` crops via UVs, `Stretch` fills.

## Design violations

`SpheneUI.Lint` is a runtime checker, toggled on the Diagnostics settings
page ("Design Lint" card). Each frame it records:

- `overhang` — control extends past the active card/region bound.
- `off-grid` — spacing/size value not on the grid (incl. `Spacing.Custom`).
- `no-theme-scope` — design control drawn outside a theme scope.

Violations get red outlines + rule labels via a foreground-draw-list overlay
and are listed in the card. A "Design System" styleguide card next to it
renders live controls for visual checking.

## Extending the system

1. Recurring raw-ImGui patterns used in **two or more windows** must become a
   facade helper in the matching `SpheneUI.<Module>.cs` partial — don't grow
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
- Never `.Result`/`.Wait()` on texture loads — `Media.Texture` resolves async.
- CompactUI is the only exempt window; it owns its styling end to end.
- Raw `ImGui` primitives stay allowed where a facade call would be wrong:
  `ImGui.Separator()` inside tooltips, native context menus and `ImGui.Columns`
  headers; `MenuItem`/`BeginCombo` popups with custom item content; low-level
  layout calls (`SameLine`, `BeginChild`, `PushStyleColor`, `SetNextItemWidth`)
  inside bespoke draw code. Standard controls (`Button`, `Checkbox`, `Input*`,
  `Selectable`, text, separators in regular flow) must go through the facade —
  in table cells use `Controls.Checkbox`, not the row-spanning `Toggle`.
