# Sphene - Global UI Audit & Responsive Density

> **Historical snapshot (2026-11-01)** - this is the point-in-time density
> audit. `DESIGN.md` is the live specification; where rows below disagree
> with it (e.g. `Text.PageHeader` attributions, `TopTabMenu` being
> CompactUI-private), the current doc wins. See
> `Docs/Runs/UI-Design-Docs-Audit-001/` for the reconciliation.

Audit performed 2026-11-01. Scope: every `WindowMediatorSubscriberBase` window,
every Hub page (incl. embedded hosts), every shared component and the
`SpheneUI` design system itself. CompactUI is excluded by policy (own styling
system); it is listed for completeness only.

Status legend: ✅ done · 🔶 partial · ⬜ pending · ➖ n/a (exempt/accepted)

---

## 1. Architecture facts that drive the whole audit

- **Single funnel**: every window's `Draw()` wraps content in
  `SpheneUI.Theme.WindowScope()` inside `WindowMediatorSubscriberBase`.
  → A density scope pushed here covers ~100% of the UI automatically.
- **Spacing grid**: `Spacing` tokens (1/2/4/8/10/12/16/20/24/32) with semantic
  aliases (`Row`, `Section`, `CardGap`, `CardPadding`, `PagePadding`,
  `ScrollInset`, `TitlePadX/Y`). Aliases are the correct density lever -
  raw steps stay fixed so the grid is never violated.
- **Metrics**: `RowHeight` (≥28·scale), `GridMinCell` (200·scale),
  `NavRailItemHeight` (30·scale const), `NavRailDividerHeight` (11.5·scale),
  `BadgeHeight` (textline + 4·scale).
- **Typography**: 4 font handles - default Dalamud body font, `DisplayFont`
  (Lato 26px), `UidFont` (Lato 35px → `Text.Big`), Axis12 (`GameFont`),
  plus `UiBuilder.MonoFont` for `Text.Header`. `SetWindowFontScale` is banned.
- **Theme style vars** (per theme): `WindowPadding` 16×12, `FramePadding` 6×3,
  `ItemSpacing` 6×4, `IndentSpacing` 20, `ScrollbarSize` 16.

## 2. Responsive gaps found globally

| # | Problem | Scope | Priority |
|---|---------|-------|----------|
| G1 | No density concept: same paddings/row heights at 1024×560 and 2000×1600 | all windows | **High** |
| G2 | Window min sizes ≥1000-1100px (Hub 1024, CharaDataHub 1000, SyncshellAdmin 1024, StatusDebug 1050, DataAnalysis 1050, PhotoStudio 1100×620) overflow 1366×768 once UI scale >1.0 or the OS claims work area | standalone windows | **High** |
| G3 | `Size`/`SizeConstraints` never consult the viewport - only `SelectPairForTagUi` checks work height | all windows | **High** |
| G4 | `Text.Big` = Lato 35px used as the generic "page title" (`Text.PageHeader`) - eats ~2 body rows of vertical space on short windows | every page | Medium |
| G5 | Fixed cell/card heights in pages (hub featured 190px, community cells 140/190px, preset buttons 96×30, studio buttons 22px) ignore density | hub pages, studios | Medium |
| G6 | `IntroUI` fixed 720×640 (min = max) - no resize, close to 768 viewport height | IntroUI | Medium |
| G7 | Nav rail item height 30px + dividers fixed - no compaction on short windows | hub + embedded rails | Medium |
| G8 | `ImGui.Table*`/legacy rows in some panels rely on raw ItemSpacing - they inherit density automatically via style vars | misc | Low |

## 3. Window inventory (top-level `WindowMediatorSubscriberBase`)

| Window | Default size | Min | Max | Notes |
|---|---|---|---|---|
| SpheneHubUi | 1150×750 | 1024×560 | 2000×1600 | hosts 13 pages + embedded settings/modsharing/analysis/charadata/housing |
| CompactUi | 370×400 | 425×400 | - | ➖ exempt (own styling) |
| SettingsUi (standalone) | - | 800×400 | 800×2000 | fixed width |
| CharaDataHubUi | - | 1000×500 (400 gpose) | 1000×2000 | fixed width |
| ModSharingUi | 1050×750 | - | - | embedded in hub too |
| DataAnalysisUi | 1050×700 | ~min | max | embedded in hub too |
| SyncshellAdminUI | - | 1024×500 | 1024×2000 | fixed width |
| SyncshellCommunityUI | - | MinContentWidth×520 | - | standalone shell community |
| PhotoStudioUi | 1240×720 | 1100×620 | 1700×1200 | largest min in app |
| StatusDebugUi | - | 1050×540 | 1050×800 | fixed width |
| StatusDebugLogPopupUi | 900×600 | - | - | popup window |
| PenumbraReceiveModUi | 900×650 | 800×500 | 3000² | |
| PenumbraSendModUi | - | 500×0 | 500×1000 | fixed width |
| ModPackageHistoryUi | 900×600 | 700×400 | 3000² | |
| IntroUI | - | 720×640 | 720×640 | fully fixed; inner dialogs 450×250-700×500 |
| IdentitySetupUi | - | 460×300 | 460×340 | near-fixed |
| EventViewerUI | - | 600×500 | 1000×2000 | |
| DownloadUi | - | 500×90 | 500×90 (+300 wide variant) | fixed |
| PairRequestsUi | 400×320 | 320×200 | - | |
| PermissionWindowUI | - | 450×100 | 450×500 | fixed width |
| TextureConversionUi | 560×640 | 480×420 | - | |
| NewOptionsUi | - | 680×420 | 1100×1400 | |
| ReleaseChangelogUi | - | 600×507 | 600×507 | fully fixed |
| UpdateNotificationUi | - | 500×300 | 600×800 | |
| AcknowledgmentMonitorUI | - | 600×400 | 1200×800 | |
| SupporterNameplateStudioUi | - | 460·scale | viewport | live constraint per draw |
| SelectPairForTagUi | - | 300×min(400,work) | 300×1000 | already viewport-aware ✓ |
| SelectTagForPairUi | - | dynamic | - | |
| SyncshellImageViewerUI | - | 520×420 | - | |
| AreaBoundSyncshellConsentUI | 600×500 | - | - | |
| AreaBoundSyncshellSelectionUI | 460×240 | - | - | |
| CitySyncshellExplanationUI | 600×450 | - | - | |
| EditProfileUi | embedded | - | - | inside hub |
| SpheneIcon | overlay | - | - | quick panel + bubble |
| PopupHandler | overlay host | - | - | BanUser + Census popups |

## 4. Hub page inventory (`SpheneHubPage`)

Home · My Profile · ProfileStyle · Friends (+friend detail view) ·
News & Updates (+article view) · Syncshells (list/join/create + shell detail
tabs + community view) · Community · Housing · Character Data (embedded
`CharaDataHubUi`) · Data Analysis (embedded `DataAnalysisUi`) ·
Mod Sharing (embedded `ModSharingUi`) · Supporter · Settings (embedded
`SettingsUi`, 15 sub-pages: Home/Account/Connectivity/Display/Theme/Alerts/
Performance/Transfers/Storage/SyncBehavior/Acknowledgment/Supporter/
SupporterNameplate/ReleaseNotes/Debug).

Sub-surfaces: nav rail (segmented items + dividers + pinned bottom items),
header band with breadcrumb + bell/inbox, home dashboard (hero, "Needs
attention", feed, discover strip, release card), community feed cards,
syncshell directory cards, friend/person cards, event cards.

## 5. Shared components (single fix → multi-page benefit)

| Component | Used by | Finding |
|---|---|---|
| `Layout.NavRail*` | hub, settings, charadata, modsharing, analysis, community | fixed 30px items → density-aware |
| `Layout.CardGrid`/`Metrics.GridMinCell` | directory, discover, dashboard | 200px min cell → density-scaled |
| `Rows.RowGroup`/`Metrics.RowHeight` | settings rows everywhere | 28px → density-scaled |
| `Text.PageHeader`/`Layout.PageHeader` | ~25 call sites | 35px title → step down to 26px in Compact |
| `Cards`/`Surfaces` + `Spacing.CardPadding` | every card | 16px → 12/20 by density |
| `PersonCard`/`EventCard`/`FeedItemRow`/`NotificationInbox` | home, friends, community, news | fixed paddings → tokens |
| `SyncshellCard`/`SyncshellDirectoryCard`/`SyncshellRoster` | syncshells | tokens |
| `PairRequestsSection` | home + syncshells | tokens |
| `DrawUserPair`/`DrawFolder*` | CompactUI + tag UIs | ➖ mostly CompactUI-exempt |
| `TopTabMenu` | legacy tab strip | check spacing constants |
| Option blocks (`*OptionBlock.cs`) | settings pages | inherit tokens automatically |
| `HousingPageView` | hub Housing | embedded, inherits hub density |
| `SyncshellCommunityView`/`SyncshellCommunityUI` | hub + standalone | shared view - inherits density |

## 6. Density profiles (implemented)

Resolution order per window per frame:
`config override (SpheneConfig.LayoutDensity)` → else auto by **unscaled window size**:

| Profile | Rule (unscaled window units) | Intent |
|---|---|---|
| Compact | height ≤ 640 **or** width ≤ 760 | small windows / 1366×768 |
| Comfortable | height ≥ 880 **and** width ≥ 1400 | large windows |
| Balanced | otherwise | default |

Mechanisms (all live in `UI/Design/SpheneUI.Density.cs`):
- `SpheneUI.Density` - scope stack (like accent overrides).
  `WindowMediatorSubscriberBase` pushes a marker in `PreDraw` (previous
  frame's profile, for fold math) and `Density.AutoWindowScope()` inside
  `Draw()` resolves the real window size and pushes the profile plus the
  style-var pass. `Density.Current` / `Density.Factor` readable anywhere.
- Style-var pass: `ItemSpacing`, `ItemInnerSpacing`, `FramePadding`,
  `CellPadding`, `IndentSpacing` ×0.75 (Compact) / ×1.15 (Comfortable) -
  covers every legacy `ImGui.*` row and embedded page for free.
- Token remap (grid steps, never off-grid): `Spacing.Row` 4/8/10,
  `Section` 8/12/16, `CardGap` 4/8/12, `CardPadding` 10/16/20,
  `PagePadding` 10/16/24, `ScrollInset` 8/10/12, `TitlePadX` 10/12/16,
  `TitlePadY` 4/8/10; `Metrics.RowHeight` 24/28/32, `GridMinCell`
  180/200/230; `NavRailItemHeight` 26/30/34, `NavRailDividerHeight`
  9/11.5/13, `BadgeHeight` +2/+4/+6, `Chrome.TitleBarPaddingY` 5/7/8,
  `Profile.HeroBannerHeight` 74/92/104, `HeroAvatarSize` 56/68/76.
- Typography: `Text.Big` uses the 26px display font in Compact (35px Uid
  otherwise) - one change covers every page title, profile name and
  warning label that uses `Big`/`Text.PageHeader`.

  Typography role map (font sizes are bound to atlas handles - Dalamud's
  global font scale owns body size; the spec's px targets map to the
  nearest role):

  | Role | Rendering | Spec target |
  |---|---|---|
  | Sidebar/nav text | body font (user-scaled) | 12-13px |
  | Body text | `Text.Plain`/`Colored` body font | 12-13px |
  | Card headings | `Text.SectionHeader` accent body font | 13-14px |
  | Page headings | `Layout.PageHeader` accent body font | ~15px |
  | Secondary/meta | `Text.Muted`/`Timestamp` body font | 11-12px |
  | Large headings | `Text.Display` 26px / `Text.Big` 35→26px Compact | 19-20px+ |
  | Hero/banner name | `Text.Big` (Uid 35px, Display 26px Compact) | - |

- `Chrome.FitToWorkArea` - clamps `SizeConstraints.MinimumSize` and
  default `Size` to 94% of the viewport work area every PreDraw
  (`Chrome.WorkAreaMax` exposes the bound for raw `SetNextWindowSize*`
  call sites). Fixes G2/G3 globally without per-window edits.
- Config: `SpheneConfig.LayoutDensity` (`LayoutDensityMode`), drawn via
  `AppearanceOptionBlock.DrawLayoutDensityOption` in Settings ▸
  Appearance ▸ Basic Interface, registered in `UpdateOptionPanel` as
  `Link.LayoutDensity` under release tag `v.1.1.31`.

Page-level constants scaled by `Density.Factor`: hub featured/directory
card heights (190/140), `CollapsedStripHeight` header band (44/52/58),
`SyncshellCommunityView` staff rows, identity banner+avatar, events
rail cards, gallery tiles, `HousingPageView` thumbs/hero/segments,
`SyncshellDirectoryCard`, `PersonCard`, `FeedItemRow`, `EventCard`,
`LinkChips` card height, `StatusDebugUi` header table.

- `SettingsUi.DrawSettingsPageContent` - dropped the centered offset of
  the 1040px page cap (`SettingsPageContentMaxWidth`); pages now dock to
  the rail inside `Layout.ContentRegion(maxPageWidth)`, which enforces
  the cap on the right for every widget (wrapped text, docked rows,
  stretched tables, full-width children) and pushes the matching
  `ContentBounds`. Removes the dead left margin on all 15 settings
  sub-pages in both hosts (hub-embedded + standalone) - previously the
  centered start plus unbounded right-edge measuring produced the
  asymmetric gap seen on wide windows.

## 7. Per-area status

| Area | Audited | Issues | Implemented | Verified |
|---|---|---|---|---|
| Design tokens / Density | ✅ | G1,G4 | ✅ | 🔶 build-only |
| Window base + chrome | ✅ | G1,G3 | ✅ | 🔶 build-only |
| SpheneHubUi (all 13 pages) | ✅ | G1,G4,G5,G7 | ✅ | 🔶 build-only |
| SettingsUi (standalone+embedded, 15 pages) | ✅ | G1 | ✅ (tokens inherit) | 🔶 build-only |
| CharaDataHubUi (7 pages) | ✅ | G2,G3 | ✅ (clamp+scope) | 🔶 build-only |
| DataAnalysisUi (5 pages) | ✅ | G2 | ✅ (clamp+scope) | 🔶 build-only |
| ModSharingUi (3 pages) | ✅ | G2 | ✅ (clamp+scope) | 🔶 build-only |
| Syncshell windows (admin/community/join/create/consent/selection/explanation/imageviewer) | ✅ | G2,G3 | ✅ | 🔶 build-only |
| Panels (intro/identity/event/download/pairreq/permission/texture/newoptions/changelog/updatenotif/statusdebug/packagehistory/sendmod/recvmod) | ✅ | G3,G6 | ✅ | 🔶 build-only |
| Shared components (cards/feed/inbox/pairs/roster/sections) | ✅ | G5 | ✅ | 🔶 build-only |
| CompactUI | ✅ | - | ➖ exempt | ➖ |
| Config + UpdateOptionPanel | ✅ | new option | ✅ | 🔶 build-only |

## 8. Verification plan

- Resolutions: 1366×768, 1600×900, 1920×1080 (logical check - ImGui sizes
  are clamped to work area so overflow is impossible by construction).
- UI scales: 0.8×, 1.0×, 1.25×, 1.5× (`ImGuiHelpers.GlobalScale`).
- Per window: no clipped content, no overlap, no unreachable buttons, no
  unnecessary horizontal scroll, no broken navigation.
- Build: mandated client command; warning baseline unchanged. ✅
  (Release build passed, 0 errors, warnings at known baseline.)

## 9. Known limitations / remaining issues

- **Fixed-size windows** (`IntroUI` 720×640, `ReleaseChangelogUi` 600×507,
  `IdentitySetupUi` 460×340, `PenumbraSendModUi` 500 wide,
  `PermissionWindowUI` 450 wide, `DownloadUi` 500×90): `FitToWorkArea`
  shrinks their *minimum* but the fixed size can still exceed very small
  viewports at high UI scale - ImGui keeps them draggable so content stays
  reachable. Making their layouts scrollable/reflowable is page work, not
  a token change.
- **Raw `ImGui.SetNextWindowSizeConstraints`** inside `DrawInternal`
  (SettingsUi popups 2830/2881/5671/5752, `IntroUI` inner dialogs,
  `SupporterNameplateStudioUi`, `SelectPairForTagUi`): all <1000px - fit
  1366×768; `Chrome.WorkAreaMax` is available if they ever grow.
- **Density flips mid-resize**: profile thresholds are window-size driven,
  so dragging across a boundary remaps layout live - intended; the 1-frame
  PreDraw marker lag is invisible.
- **Page-level fixed heights** not scaled by `Factor` remain where the
  size is content-driven (banners sized by aspect ratio, `cardW * 0.46f`
  ratios) - those already adapt.
- **In-game verification** (clipping/overlap pass at the three target
  resolutions and UI scales) is not executable here - Dalamud plugins only
  render inside FFXIV. The construction guarantees no window can demand
  more than 94% of the work area; remaining checks are visual.
