# Side panel (web view + React)

A dockable Rhino panel (`FrepPanel`) that hosts a React app in Eto's WebView (WebView2 on Windows, WKWebView on macOS). It has a browser tree of F-rep objects at the top and a selection-driven inspector below it.

```
src/Freppuccino/Ui/
  FrepPanel.cs       Eto panel, registered in FreppuccinoPlugin.OnLoad; command FrepPanel opens it
  WebViewHost.cs     shared: start URL, origin check, JSON both ways, blocks foreign navigation
  IWebTransport.cs   per-OS part: WebView2Transport.cs (Windows), EtoWebTransport.cs (macOS)
  UiController.cs    Rhino events → snapshots for the page; page requests → Rhino (whitelisted)
  UiProtocol.cs      message types  ◄── keep in sync ──►  src/ui/src/bridge/protocol.ts
  PrimitiveProxy.cs  setParam: validates + writes SDF.<name>, rebuilds the proxy brep
  UserFnView.cs      FFunction (sdUser) user text → compiled userFn payload
src/ui/              Vite + React 19 + TypeScript + Tailwind 4 + shadcn/ui (radix), styled with the FRepuccino brand kit
  src/brand/         fonts, command icons, wordmark copied from brand-kit/ (icons recoloured: ink → currentColor)
  src/index.css      brand tokens (brand-kit/tokens.json) mapped onto shadcn's CSS variables; Latte / Espresso themes
  src/bridge/        host.ts (transport + store + useHost), mock-host.ts (fake Rhino for the browser)
  src/components/    scene-tree.tsx (browser), inspector.tsx (create / primitive / combine), param-slider.tsx, code-view.tsx (FFunction source),
                     script-panel.tsx + code-editor.tsx (Script tab: FFunction editor)
  src/lib/examples.ts FFunction examples (examples/ffunction/*.glsl + clip boxes) for the Script tab and the AI
  src/lib/scene.ts   display names, icons, labels per SDF type / operation
  src/ai/            AI assistant (AI tab): agent loop, tools, skills; see docs/ai.md
```

## Why Eto's WebView and not a raw WebView2 control

On Windows, Rhino 9's Eto `WebView` *is* WebView2 (`Eto.Wpf` `WebView2Handler`, runtime 1.0.1938.49 ships in `Rhino 9\System`). `WebViewHost` gets the `CoreWebView2` through the native control by reflection and compiles only against `Microsoft.Web.WebView2.Core` (compile-only package reference). So:

- the project stays `net10.0` with no WPF dependency, and still builds on macOS
- the `postMessage` / `SetVirtualHostNameToFolderMapping` APIs are available, so no URL-scheme hacks and no `file://` module/CORS issues

macOS: Eto's WebView is WKWebView, and Eto (2.13) already bridges it: a script message handler that injects `window.eto.postMessage` into every frame and raises `WebView.MessageReceived`. So the Mac side is plain Eto (`EtoWebTransport.cs`), no Microsoft.macOS / Eto.macOS reference, and builds anywhere. Page → host: `window.eto.postMessage(JSON string)`; host → page: `ExecuteScriptAsync` calling `window.__freppuccino.receive(msg)`. Eto gives no sender URL, so every frame navigation is checked in `DocumentLoading` and the main frame's URL is the origin check. The UI loads from `file://<plugin dir>/ui/index.html`; WKWebView blocks CORS-mode loads from `file://` (module scripts, `crossorigin`), so `vite.config.ts` builds one classic IIFE script loaded with `defer` (CSS inlined into it). Dev tools: Debug builds set `WKWebView.isInspectable` (macOS 13.3+); right-click → Inspect Element, or Safari → Develop.

Layout: `WebViewHost.cs` is shared (start URL, missing-UI placeholder, origin check, parse/dispatch, serialization, external links); `IWebTransport` is the per-OS part: `WebView2Transport.cs` (Windows), `EtoWebTransport.cs` (macOS).

## Dev workflow

| Goal | Do |
|---|---|
| Work on the UI without Rhino | `cd src/ui && npm install && npm run dev`, then open http://localhost:5173. A **mock host** stands in for Rhino; its sample scene includes nested operations. |
| Hot reload inside Rhino | Run `npm run dev`, then start the **"Rhino 9 + Freppuccino (UI dev server)"** launch config, which sets `FREPPUCCINO_UI_DEV_URL=http://localhost:5173`. |
| Normal plugin build | `dotnet build` copies `src/ui/dist` to `bin/.../ui/`. It runs `npm run build` itself only for Release builds or when `dist` is missing; otherwise run `npm run build` after UI changes. Pass `-p:SkipUiBuild=true` to skip. |
| Debug the page in Rhino | Debug builds: Windows F12 / right-click → Inspect (WebView2 DevTools); macOS right-click → Inspect Element (Web Inspector, 13.3+). |
| Blank panel, console "Invalid hook call" (Windows) | React got bundled twice because the build ran from a lowercase drive path (`c:\...`, e.g. Git Bash or some VS Code terminals): Vite registers the same file under both casings. Build / `npm run dev` from a `C:\...` working directory (PowerShell) and check that `dist/assets/*.js` contains `__CLIENT_INTERNALS_DO_NOT_USE` 3 times, not 5. |

In Rhino, run `FrepPanel`, or open the Freppuccino panel from the panels menu.

## Brand

The UI follows `brand-kit/` (tokens.json, fonts, command icons). To refresh after the kit changes: re-copy the
woff2 fonts and the `light` command icons into `src/ui/src/brand/`, replacing `#3B2620` with `currentColor` in the
icons (the dark set differs only in that colour), and update the colour values in `src/ui/src/index.css`.
The kit has no cone / pyramid / ellipsoid / function icons yet; `src/brand/icons/{cone,pyramid,ellipsoid,function}.svg` are stand-ins
drawn in its style, so replace them when the kit gets official ones. Primitive names, icons, parameter labels and
creating commands live in one table, `PRIMITIVES` in `src/ui/src/lib/scene.ts`.

## Protocol

JSON messages, each with a `type`. Host → page messages are snapshots (simple, and fine at this scale). Page → host messages are requests. Selection order is tracked on the page, because Rhino doesn't report pick order; it matters for subtract (target vs tool).

Ids are strings: a primitive's Rhino object id (GUID), or a CSG node's name (`N3`, same as typed at the F-CSG command prompts).

| Host → page | When |
|---|---|
| `state {scene, selection, settings: {units}, theme, frepMode}` | reply to `ready`; active document changed. `frepMode` = the active view shows the Freppuccino display mode |
| `view {frepMode}` | the active view switched into or out of the Freppuccino display mode, or another view became active (checked on Idle) |
| `scene {scene}` | object added/deleted/replaced/attributes changed, CSG tree changed (coalesced on Idle) |
| `selection {ids}` | Rhino selection or panel node selection changed |
| `notice {level, text}` | feedback, e.g. a rejected `setParam` value |
| `aiToolResult {callId, ok, result?, error?}` | reply to `aiTool` ([ai.md](ai.md)) |

| Page → host | Status |
|---|---|
| `ready` | ✅ |
| `select {ids, mode: replace\|add\|toggle}` | ✅ primitives → Rhino selection; nodes → panel node selection. While FUnion/FIntersect/FDifference wait for operands, a node click is sent to the command line instead |
| `setVisible {id, visible}` | ✅ primitive: Rhino show/hide; node: `FrepNode.Hidden`, dropped from the shader like a hidden primitive (undoable) |
| `runCommand {command}` | ✅ whitelist only (`UiController.AllowedCommands`): `FBox`, `FSphere`, `FCylinder`, `FCone`, `FPyramid`, `FEllipsoid`, `FTorus`, `FFunction`, `FTwist`, `FBend`, `FRepeat`, `Freppuccino` |
| `showFrepMode` | ✅ switch the active view to the Freppuccino display mode. Sent by the "F-rep display mode" button, shown under the header while `frepMode` is false |
| `applyOperation {operands, op, k}` | ✅ new CSG node over top-level operands; selects it. Union and intersect take ≥ 2; subtract takes exactly two |
| `setOperation {id, op, k, preview?}` | ✅ edit a node live. `preview: true` = drag tick, applied without undo; the next non-preview one ends the gesture as one undo step |
| `setNodeParam {id, name, value, preview?}` | ✅ edit a twist/bend/repeat param; same preview/commit gesture as `setOperation` |
| `deleteNodes {ids}` | ✅ nodes still used by other nodes are kept |
| `moveNode {id, from, to, index?}` | ✅ tree drag: detach `id` from node `from` (null: it is a root) and insert it into `to` at `index` (null `to`: make it a root). `to` must be a union, subtract, or intersect; cycles and emptied nodes are refused with a notice. One "Move in tree" undo step |
| `setColor {id, color, preview?}` | ✅ write `SDF.Color` (`"#RRGGBB"`) on a primitive; same preview/commit gesture as `setOperation` (one "F-rep color" undo step per picker drag) |
| `setParam {id, name, value, preview?}` | ✅ write `SDF.<name>` on a primitive and rebuild its proxy brep (`PrimitiveProxy`); same preview/commit gesture as `setOperation` (one "Edit F-rep parameter" undo step per drag). Sizes must be > 0 (cone radii ≥ 0). sdUser also takes `L` (> 0) and `u.<param>` (declared in the code, any finite value); both written round-trip exact, always stored |
| `aiTool {callId, tool, args, source?}` | ✅ AI agent tool call (`Ai/AiTools.cs`), see [ai.md](ai.md). The Script tab sends the same calls with `source: "script"` (undo label "Script: …" instead of "AI: …") |
| `aiSaveRun {title, transcript, images}` | ✅ writes an AI run log (`Ai/AiRunLog.cs`) |

Live edits: the inspector has no Update button. An op click sends one `setOperation`; a Blend drag sends `preview` ticks (≤ ~30 Hz, trailing) then a final one on release. The host (`FrepTrees.Preview`/`Commit`) remembers the tree from before the gesture and records one undo step back to it. A gesture whose final message never arrives is committed by any other page message, any Rhino command (incl. Undo), a page reload / doc switch, or panel close. The page ignores host snapshots for the node while dragging and until the host echoes its last sent value, so stale snapshots can't yank the slider.

`op` ∈ `union | subtract | intersect` (`FrepOp.Difference` = subtract). Every op takes a blend `k` (0 = sharp, > 0 = smooth). For subtract, operands[0] / children[0] is the target and the rest are tools.

## Browser tree

`SceneNode` is recursive:

```
SceneNode { id, kind: primitive|operation, name?, visible, locked, missing?,
            sdfType?, params?, color?,  // primitive; color = SDF.Color "#RRGGBB" (white if missing)
            userFn?,                    // sdUser only, see below; params then = just b.x/b.y/b.z
            op?, k?, children?,         // operation: children are operands, in order
            ref?, refMissing? }         // twist|bend|repeat: params = {angle} / {angle, direction} / {x, y, z}; ref = line/box id
```

`UiController.BuildScene` walks the doc's `FrepTree` (Csg/FrepTree.cs, stored as plugin document data): roots are nodes nothing references, then primitives no node uses. The tree is a DAG, so a shared operand is listed under each parent. Deleted operands show as `missing`.

Operation nodes aren't Rhino objects, so their selection is kept by `UiController` (per document). FUnion/FIntersect (and FTwist/FBend/FRepeat, single node) take it as preselection (`UiController.TakeSelectedNodes`); Rhino deselect-all clears it.

### FFunction (sdUser)

A box (frame + half-extents `SDF.b.*`, proxy brep = the box) whose SDF is user code. `Ui/UserFnView.cs` reads it straight from user text (not via `FrepScene`) and compiles it with `UserFnCompiler.Compile` (cached by source) on every scene snapshot:

```
userFn { code,                          // SDF.Code
         ok, hash?,                     // compiled; hash = UserFnProgram.Hash
         diagnostics: [{line, column, message}],   // 1-based
         params: [{name, value, default}],         // declared order; value = SDF.u.<name> or default; [] if !ok
         lipschitz,                     // SDF.L; 1 if missing / invalid
         error? }                       // set when SDF.L is invalid (not drawn; FrepScene rejects it), shown under the code
```

Any user-text edit (panel, `SetUserText`, an AI agent) fires `ModifyObjectAttributes` → the next Idle sends a new `scene`, so code changes show up without reselecting.

Inspector: read-only code view (`components/code-view.tsx`: line numbers, diagnostic lines tinted red with the message on hover, 240px cap with a resize handle + expand, copy), diagnostics list (click → scroll to line) or "Compiles OK · <hash>", then a slider per declared param, `Lipschitz L`, and the bounding box half-extents. Param slider range from default d: d > 0 → [0, 4d], d < 0 → [4d, 0], d = 0 → [−1, 1]; step ≈ span/100; typed values may go past either end. The code view has an **Edit** button that opens the function in the Script tab (`components/script-panel.tsx`, editor: `components/code-editor.tsx`; see [ai.md](ai.md#coverage-app-features-vs-ai-tools)), where the code is editable, checked as you type, and applied with Ctrl+Enter.

Inspector: one primitive → its color (`SDF.Color`: current-color swatch / picker + hex field; new primitives start white `#FCFCFC`) and parameters; one node → edit op / k live (no Update button), delete; ≥ 2 selected top-level nodes → combine them (union and intersect take any number; subtract takes exactly two; pick order; first = subtract target).
