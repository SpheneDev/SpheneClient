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
│               NavRail(BeginNavRail[embedded]/NavRailItem/NavRailCategory/
│               NavRailDivider/EndNavRail/BeginNavContent[scrollable]/
│               EndNavContent)/
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
  `SettingsUi` overrides these for the Discord/Ko-fi buttons + version text;
  `SpheneHubUi` uses them as a status/notification strip (connection text)
  and moves window actions into the header band (bell + burger menu).
- `ChromeIconTexture` — optional `IDalamudTextureWrap` drawn instead of
  `ChromeIcon` when set (cover-fit, rounded). `SpheneHubUi` uses it to put
  the Sphene logo in the title bar.
- `TitleBarButton.BadgeCount` renders a red counter badge on the button's
  top-right corner (0 = hidden, capped at "9+") — the design-system
  notification indicator for title bar buttons. `SpheneHubUi` reuses the
  drawer (`Chrome.DrawTitleBarButtonBadge`) to badge in-band buttons.

`SpheneBrandIcons.SpheneLogo()` returns the embedded Sphene logo texture
(`images/icon.png`) — colored artwork, do not tint; used by `SpheneHubUi`
for the title-bar icon and the hero banner watermark.

Windows must NOT draw their own `Chrome.TitleBar` when `UseSpheneChrome`
applies — the base does it. Windows overriding `PreDraw`/`PostDraw` must
call `base.` so chrome flags and the hidden-grip colors stay balanced.

Windows with their own styling system can still drive the fold machinery
for a custom bar (CompactUI's profile title bar): call `UpdateCollapse` in
`PreDraw`, draw the bar first in `DrawInternal`, gate the remaining content
on `CollapseAmount`/`IsCollapsed`, and wire the chevron to the `internal`
`Chrome.ToggleFold`/`Chrome.FoldCollapsed` pair — `ToggleFold` is not
public so generic windows keep going through `Chrome.TitleBar`.

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

A card's right edge is clamped **geometrically**: `avail`, the current
window's edge and the innermost scroll area's right bound
(`scroll child right − 1px − ScrollAreaContentInset`) all compete in a `Min()`.
Cards drawn directly in the scroll child keep its inset; cards inside nested
children (expanders, embedded panels) are bounded by their own child's edge
and are never double-inset. Windows outside the scroll area (popups) skip the
bound entirely. The same math backs `Layout.CardAlignedAvailX()` for
non-card elements that must share the card edge — and `BeginExpander` clamps
its panel to it, so the panel outline lands exactly on the card edge.

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

### Full-width header band (seamless rail transition)

For a community-style header spanning the window's whole width above the
rail, reserve a `Dummy` band before `BeginNavRail`, then after `EndNavRail`
paint the band with `Layout.NavRailBandColor()` — the exact fill the rail
band uses — so header and rail merge seamlessly. `SpheneHubUi` (see
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
  contains a rail or self-managed scroll regions — nesting a rail inside an
  outer scroll area mispositions its band. The child uses zero
  `WindowPadding`: embedded layouts provide their own rail + scroll insets,
  so their content lands on the exact same edges as scrollable pages.
- The nav-content child spans to the **parent window's right edge** (the
  content region ends `WindowPadding` short of it). Combined with
  `ScrollAreaRightBound` this makes the right card margin equal the left
  inset: `windowRight − 1px − ScrollAreaContentInset`. Never size the child
  by raw `GetContentRegionAvail` — cards would sit one padding too narrow
  on the right.
- Scrollable and non-scrollable nav content nest correctly (mode is tracked
  per open rail).

### Hub navigation history & breadcrumb

Sphene Hub treats a page plus its detail context as one *location* —
`HubLocation(Page, GroupGid, Friend)` — and keeps back/forward stacks.
`NavigateTo` pushes the current location and clears forward;
`NavigateBack`/`NavigateForward` move without losing context;
`ApplyLocation` performs the leaving-cleanup (embedded admin panels,
profile edit mode, friend texture caches) then applies the target. Every
in-hub navigation (rail items, friend rows, syncshell cards, back-rail
headers, hero actions, `OpenSpheneHubMessage`) goes through these —
never assign `_activePage`/`_selectedGroupGid`/`_viewingFriend`
directly, or history and breadcrumbs desync. System-driven corrections
(a group vanishing) use `ApplyLocation` directly so they do not pollute
the stacks; both stacks reset when the window closes
(`ResetTransientState`).
The header band is a slim toolbar (~52u): the **profile card claims its
rail-colored left segment** (the sidebar owns that part of the header) —
circular avatar with `Media.PresenceDot` badge (fixed 45° position on
the circle edge — avatars are always circles), name + status, hugging
the rail width, click → profile page. The nav cluster sits right behind
it at the content edge: `[←] [→]` plus the breadcrumb pill on one row
(`ImRaii.Disabled` when the stack is empty), bell + menu buttons
mirrored on the right — all at one vertical center. The pill is a dark
55% scrim with a 10% hairline, fully rounded — legible over bright
banner art and dark surfaces alike. Depth is two segments max
(`Page › detail`); the parent segment is clickable and navigates one
level up.
The style editor is its own page (`SpheneHubPage.ProfileStyle`, crumb
"My Profile › Style") reached via the hero's always-visible "Style"
action; it reuses the sticky-header recipe below and returns via
`NavigateBack` on Save/Reset.

### Embedding existing windows (`DrawEmbedded`)

Windows whose content must also render inside a host window (Sphene Hub,
`ModSharingUi`) expose `internal void DrawEmbedded()` that draws *content
only* — no own `Window` frame, no own scroll area (the host provides it).
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

## Profile (community pages)

`SpheneUI.Profile` renders community-profile surfaces. The same helpers back
the Hub profile page, the Friends profile view, `StandaloneProfileUi`, and
`PopoutProfileUi` — a profile must look identical wherever it appears.

- `Profile.Banner(drawList, min, max, texture, theme, defaultTop, defaultBottom,
  rounding = null, corners = All)` — banner strip: cover-draws the owner's
  uploaded banner, otherwise a gradient from the owner's theme colors (falling
  back to the accent defaults). Always passes the *owner's* theme, never the
  viewer's. `rounding`/`corners` override the theme rounding and restrict which
  corners round — the hero uses `RoundCornersTop` for its full-bleed edge.
- `Profile.Hero(displayName, metaLine, profile, avatarWrap, bannerWrap,
  theme, …)` — full-width profile header: banner backdrop, scrim, overlapping
  avatar, name/status/tag slots, `actions` (`HeroAction` records) at the
  banner's bottom-right and `drawStats` for full-width content beneath the
  identity block. `actions` is the extension slot for future community
  features (house travel, etc.).
- **Full-bleed hero banner** — the hero banner bleeds into the card padding:
  its left/right/top edges sit on the `AccentCard` outline, top corners
  rounded at `AccentCardRounding` so they match the card silhouette exactly
  (no separate banner radius), and the bottom edge dissolves into the card
  body via a vertical
  alpha ramp of the *card's own tint* (`Layout.AccentCardTint`, fraction
  `HeroBannerBodyFade` = 45%). Do not re-introduce a hard banner edge or an
  inset strip — the banner owns the card's top edge.
  It is drawn through `AccentCard`'s `topBleed` chrome callback, which
  receives the card's real outer rect — content callbacks run inside a
  `ContentRegion` table cell, so `GetCursorScreenPos` there cannot be used
  to reconstruct edge-aligned geometry.
- `Profile.StatTile(...)` — the dashboard tile (icon plate + value + label)
  used where profile stats still make sense.
- `Profile.HeroEdit` + the `edit:` parameter — own-profile edit mode: banner
  and avatar get click overlays (`OnBannerClicked`/`OnAvatarClicked`,
  typically routed to `ImageCropUi`), status and description render as inline
  inputs whose `StatusBuffer`/`DescriptionBuffer` commit via the
  `On*Committed` callbacks on Enter or focus loss. Buffers are seeded by the
  host when edit mode is toggled on, not by the hero.
- Editable tag chips — in edit mode the hero tag row gains a trailing "+"
  chip that opens an inline `InputTextWithHint` (`AddingTag`/`NewTagBuffer`/
  `TagInputFocusPending`), and each chip shows a "×" remove affordance on
  hover; add/remove fire `OnTagsCommitted` with the full comma-joined list.
  Only the × zone removes — clicking the chip body is inert.
- NSFW chip — rendered next to the name whenever `isNsfw` or editing; with
  `OnNsfwToggled` set it becomes a clickable toggle chip.
- `Profile.ThemeColorRow(label, ref enabled, ref color)` — single settings
  row for an optional theme color: a `Controls.ColorSwatch` LEADS the row
  on the left (always rendered, dimmed while disabled so it still reads as
  a color column), then label + slide toggle docked to the row's right
  bound; the swatch opens the styled ColorPicker4 popup. **Design-system
  rule for every settings row combining a toggle with extra controls:
  auxiliary controls go BEFORE the label/toggle, never behind or after
  the switch — switch tracks form one unbroken vertical column and may
  never shift based on optional trailing content.** Shared by the hub
  "Style" panel and `EditProfileUi`'s appearance card. Inside a
  `BeginDropdown` scope the picker opens through
  `Popups.ToggleFlyout`/`BeginFlyout` instead of
  `OpenPopup`/`BeginStyled` — the native popup pair loses its first frame
  against the dropdown's own `Popup` layer and focus pinning, while the
  flyout is a plain pinned window that opens on the first click. Everywhere
  else (in-window expanders, normal windows) the regular `BeginStyled`
  popup path is used — it is reliable there.
- `Profile.NsfwChipSize(active, scale)` / `Profile.NsfwChipAt(drawList, pos,
  active, scale, hoverT)` — draw-list chip renderer for NSFW badges anywhere
  (friend rows, lists). The active state always shows a check glyph so
  "enabled" is unambiguous; `hoverT` accepts an eased `Fx.HoverT` value.

## Anchored dropdowns (`Popups.BeginDropdown`)

An anchored menu that visually flows out of its trigger button — a sliver
under the button eases down and left to the content size, right-aligned
with the button's right edge so it expands leftward. While open the
trigger is repainted as a flat-bottomed **tab** in the menu's surface
color (icon + label in accent). Instead of a window border plus a seam,
`DrawDropdownTab` strokes **one closed path around the union of button
and window** (`AddDropdownOutline`): the outline climbs the button's
rounded top corners and rejoins the panel, so both read as a single cast
shape — the window's own `ImGuiCol.Border` is transparent — and the
window alpha-fades during the grow:

- Trigger: call `Popups.ToggleDropdown(id)` inside the button's click
  handler (it captures `ImGui.GetItemRectMin/Max`, which is still the
  button's rect then).
- `Popups.BeginDropdown(id, contentSize, icon, label)` — returns true
  while visible; render content after it and pair with
  `Popups.EndDropdown()`. Pass the trigger's icon and label so the
  repainted tab keeps its glyph and text.
- The window background is forced opaque (`Surface` at alpha 1) — the
  themed popup bg is translucent and would let the host window shine
  through as displaced content. The cove left by the window's rounded
  corner next to the tab is filled so the junction stays solid.
- `contentSize` is the *maximum* opened size — the height auto-fits:
  `EndDropdown` measures the content bottom each frame and the next
  `BeginDropdown` clamps `size.Y` to it, so menus never reserve dead
  space below their cards nor clip them. For side-by-side columns do
  **not** use nested `BeginChild`s — a fixed-height child scrolls its
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
  above the host window — while open/close stays manually animated. The
  pin yields while an inner popup (combo) or flyout (color picker) is
  open, so the overlay can come to front.
  For scrollable menu content use `Layout.BeginScrollArea` inside: it
  provides wheel scrolling with edge fades instead of a native bar.
- Close paths: clicking the trigger again, outside click (popups and
  flyouts opened from inside don't count), Escape while focused, or
  `Popups.CloseDropdown(id)`. `Popups.ResetDropdowns()` drops all
  dropdown and flyout state on window/page resets.

Prefer this over `BeginStyled` for button-anchored menus that should feel
connected to their trigger — and over `Layout.BeginExpander` only when the
menu genuinely has to float *above* other content instead of living in the
page flow.

## Inline expanders (`Layout.BeginExpander`)

An expanding panel rendered **inside** the host window's layout flow —
unlike `BeginDropdown` it is not a separate overlay window, so it needs
no focus pinning, z-order or outside-click handling at all and simply
vanishes with the host window. `Layout.ToggleExpander(id)` flips the
open state (typically from a button click), `BeginExpander(id)` returns
true while visible — render content after it and pair with
`EndExpander()`. The region grows from zero to the measured content
height with a smoothstep height + alpha animation and clips content
while animating; once fully open it is exactly content-sized with no
inner scrolling (the host page scrolls instead). External close paths:
`Layout.SetExpanderOpen(id, false)`; `Layout.ResetExpanders()` drops all
state on window/page resets. The panel paints its own background on the
parent draw list (a barely-lifted copy of the host window's `WindowBg`)
while pushing `ChildBg` transparent — child windows paint `ChildBg` as
their own background, so an opaque push would let nested children (the
hero's text block) overlay the accent-tinted card surfaces beneath them.
The tone is `WindowBg` + 4% — semi-transparent card fills composite
identically to the page.

Used by the hub hero's "Style" panel: the preview switches between the
live profile card and the friends-list row demo via `Layout.TabStrip`,
and the settings cards stack at full width below it, filtered to the
controls that apply to the active preview tab.

### Flyouts (`Popups.BeginFlyout`)

Small anchored panels for controls inside a dropdown that need their own
floating surface (the theme color picker). `Popups.ToggleFlyout(id)`
toggles and captures the trigger rect; `BeginFlyout(id)` opens an
`AlwaysAutoResize` window at the trigger's right edge (below it,
flipping above near the viewport bottom), focus-pinned and alpha-faded
like a dropdown — outside clicks and Escape close it, other flyouts
close when a new one toggles. Pair with `Popups.EndFlyout()`.
The flyout window is deliberately **not** `ImGuiWindowFlags.Popup`:
`ImGui.Begin` resolves a Popup-flagged window's PopupId from the live
popup stack, which is empty for a manually begun window — the bogus id
makes open/close/focus erratic (opens only after several clicks). A
plain pinned window plus the manual close paths open reliably on the
first click. The dropdown's focus pin and outside-click handler both
yield while `AnyFlyoutOpen` holds, so flyouts stay interactive above
their parent.

## Micro-animations (`SpheneUI.Fx`)

Reusable, framerate-independent (DeltaTime-driven) hover animation
primitives:

- `Fx.HoverT(id, hovered, speed)` — eased 0..1 hover progress per widget id
  (`ImGui.GetID` with the widget label), smoothstepped ramp in/out.
- `Fx.HoverRow(drawList, min, max, accent, t, radius)` — standard hover
  treatment: tinted fill + accent border fading in plus an accent edge bar
  growing out of the left edge.
- `Fx.Sheen(drawList, min, max, phase, bandWidth, tint)` — clipped
  traveling light band; cycle `phase = (time * speed) % 1.5` for a sweep
  with a rest interval (phases outside 0..1 draw nothing).
- `Fx.Reset()` — clears cached animation state (wired into hub resets).

## Owner-selected row effects (`Profile.RowFx`)

Friend-list rows (and any future row/card surface) render the **row
owner's** chosen effects — not the viewer's settings:

- `Profile.RowFx(drawList, min, max, accent, hovered, hoverT, theme,
  radius)` — paints the owner's theme layers behind row content: the
  `RowIdle` flags (always visible) then the `RowHover` flags driven by
  the eased `hoverT`. Any animated row image must be drawn *beneath* this
  call. A flags-taking overload (`ProfileRowFx hover, ProfileRowFx idle`)
  serves ad-hoc surfaces like the style preview strip.
- `ProfileRowFx` (Services, `[Flags]`) — every set bit draws one layer,
  so owners stack effects freely: hover mask `Glow` (edge bar + fill),
  `Sheen` (traveling light band), `Pulse` (fast breathing accent border),
  `Sweep` (accent comet orbiting the border), `Lift` (drop shadow + top
  edge highlight); idle mask `EdgeBar` (static accent edge), `Tint`
  (subtle accent gradient), `Pulse` (slow breathing border), `Sparkle`
  (twinkling accent sparks), `Wave` (continuous slow luminance drift).
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
  the card chrome via `Profile.RowFx` — drawn on the card's chrome
  channel so they sit above the card background but beneath the content.
  `Profile.Hero` passes the theme's `CardHover`/`CardIdle`, so the real
  card, the style preview and every other hero render identical effects
  from the same code path. The card's built-in ambient animation (sheen
  band + border pulse, `Chrome.AccentCardBackdrop` + the border alpha
  pulse in `AccentCard`) is gated by the theme's `cardShimmer` flag —
  Hero forwards it as `animatedBackdrop`, so a card with shimmer off and
  no picked effects is fully still. Defaults to on; only the profile
  card honors it, other AccentCards keep their chrome.
- `Profile.FxChips(groupLabel, ref value, allowed, accent)` renders the
  effect picker as wrapping toggle pills. The style preview renders the
  REAL `Profile.FriendRow` — the identical renderer the friends list
  uses (`FriendRowContent` carries avatar/name/presence/status/NSFW;
  the preview feeds it the owner's own live profile data) — so what you
  see in settings is pixel-for-pixel what friends see. Same for the
  card tab: the preview IS `Profile.Hero` bound to the unsaved buffers.
  Owners pick effects on the hub's Style page (`SpheneHubPage.ProfileStyle`);
  its two
  preview tabs gate which settings show: the "Profile card" tab carries
  accent + banner controls + card effects, the "List row" tab carries
  accent + row effects + the supporter row-image upload — controls only
  appear where they apply. The tab strip + preview form a sticky
  header: their flow space is reserved via `Dummy` while the block is
  drawn last, clamped between its natural slot and the child's visible
  clip top — settings scroll underneath, the preview stays put.

Used by hub friend rows (per-friend accent + theme drive idle/hover,
chevron slides in) and `Profile.StatTile` (eased fill/border).

## Animated images (`Media.Animated`)

- `Media.Animated(key, byte[])` — cache-owned `AnimatedImage` decoding
  off the UI thread: GIF/WebP frames are composited into a horizontal
  atlas in a single texture wrap with per-frame durations (PNG decodes to
  a single static frame). Capped at 48 frames and 512px per side.
- `AnimatedImage.FrameIndexAt(time)` / `IsAnimated` — looping frame
  lookup; `AnimatedInRect(drawList, anim, min, max, fit, tint, rounding)`
  draws the current frame with the standard fit/tint/rounding pipeline.
- `Media.Evict(key)` drops both the static and animated cache entries —
  call it whenever the underlying bytes change.

Used for the supporter-only animated row background: owners upload a PNG
or GIF via the Style page — `ImageCropUi` (`CropKind.RowImage`) locks
the selection to `Profile.RowImageAspect` (4:1, the exact strip aspect)
and outputs ≤512×128/≤400KiB; multi-frame sources stay animated
(crop+resize applies to every frame, GIF-encoded on save). The server
enforces the supporter gate + limits in `UserSetProfile`, and
`UserProfileDto.RowImageBase64` carries it to viewers.
`Profile.RowImageStrip` renders the strip left-anchored at row height and
fades to TRUE transparency toward the right edge — it tessellates the
rounded strip as a textured perimeter fan (`PushTextureID` + per-vertex
atlas UVs and alpha), so the image's own opacity dissolves and the row
background shows through instead of blending into a solid color;
readability dimming is baked into the vertex color. `Profile.RowBackdrop`
is the shared composition — row image beneath the owner's idle/hover
`RowFx` layers — used identically by the friends list and the style
preview's `FxPreviewStrip`, so previews can never drift from real rows.
Drawn only when the row owner `IsSupporter` (non-supporters can't smuggle
one in via stale data). `""` clears it server-side; `null` preserves the
stored image on partial updates.

`ImageCropUi` (UI/Components) is the interactive crop window used for all
profile images: `Open(CropKind, bytes, w, h, onConfirm)` shows the source
with an aspect-locked selection (1:1 avatar, 3:1 banner, 4:1 row strip —
move by dragging inside, resize via corners, click outside recenters).
"Apply" crops in source pixels, downscales to the server limits and
PNG-encodes on a task (GIF-encodes when the source was animated) — only
the cropped bytes are handed to `onConfirm` on the UI thread.
- `SpheneProfileTheme` (Services) — the owner's stored theme
  (`accent`/`bannerTop`/`bannerBottom`/`rowHover`/`rowIdle`/
  `rowHoverFx`/`rowIdleFx`/`cardHover`/`cardIdle`/`cardHoverFx`/
  `cardIdleFx`/`cardShimmer`/`bannerTopOver`/`bannerBottomOver`, JSON,
  forward-tolerant). Null fields mean "use design defaults"; parse
  failures fall back to defaults. `bannerTopOver`/`bannerBottomOver`
  blend that half's color (55% alpha) over the banner image instead of
  the image replacing the gradient — `Profile.Banner` honors all of them,
  so standalone and popout profiles show the same look. Rounded gradient
  fills render through
  `Media.FillRoundedGradient`, which tessellates a vertex-colored fan
  with bilinear corner colors plus a 1px alpha fringe (MultiColor rects
  can't round; the earlier 2×2-texture + `AddImageRounded` path visibly
  offset the blend under rounding).
- `ProfileVisibility` flags (SpheneAPI `Data/Enum`) — owner-controlled
  section hiding, enforced server-side in `UserGetProfile`. Hidden sections
  arrive empty; render them as absent rather than "hidden" placeholders,
  except on the owner's own view.

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
