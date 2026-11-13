# Pixel Render — Legacy

The previous approach (per-object grids, layer 31, namespace `EliotByte.PixelRender.Legacy`). Kept for reference.

Stable pixel-art rendering for 3D scenes with a **perspective** camera in Unity URP.

The scene is drawn with chunky "pixels" (cells of ~7 screen pixels at 720p), but at **full screen resolution**:
cells move smoothly with the world instead of jumping between the cells of a low-resolution grid, and they don't
shimmer or crawl when the camera moves.

> Work in progress. The API, settings and shaders will change.

## How it works

Every object with `PixelRenderObject` gets its own pixel grid. There are two kinds:

- **Object grid (compact objects: props, cars, poles).** The grid is anchored to the object's centre on screen.
  The pixelated look of the object is *baked* (`Surface Bake`): it is captured once and carried as a sprite
  (moved by whole pixels after the object's centre and scaled by depth) until the view changes noticeably —
  by angle, scale or the error of the sprite against the real geometry (`Bake Angle`, `Bake Scale`,
  `Bake Max Error`). The edge can follow the live geometry with hysteresis (`Live Silhouette`).
- **Surface grid (large surfaces: floor, terrain, walls).** One anchor can't serve a surface that spans 1–80 m in
  depth, so the cells live on the surface itself. The grid axes are the camera's right/up projected onto the face,
  re-baked every `Surface Grid Angle` degrees; between updates the cells are glued to the surface. Cell size
  goes in ×2 steps **separately per axis** (like anisotropic filtering), so a cell stays about
  `Screen Cell Pixels` on screen at any distance.

Colour, light, shadows and fog are always evaluated at the **centre of a cell**, so every screen pixel of a cell
gets the same answer.

Pipeline (`PixelRenderManager`, on the main camera):

1. A hidden source camera renders all pixel objects (layer 31) into a full-resolution float texture:
   colour, object id and depth (`EliotByte/PixelRender/Source`).
2. Each object draws a quad over its screen rectangle that assembles it from the source by its own grid
   (`EliotByte/PixelRender/Resolve`).

## Requirements

- Unity 6 (tested on 6000.6), URP 17.
- Layer **31** is reserved for pixel objects.

## Installation

Via Package Manager → *Add package from git URL*:

```
https://github.com/eliotbyte/PixelRender.git
```

## Usage

1. Add `PixelRenderManager` to the main camera. Shared settings live in its `Settings`.
2. Add `PixelRenderObject` to every object that should be pixelated, with a material using
   `EliotByte/PixelRender/Source`. Turn on `Surface Grid` for big surfaces.

The source shader draws a procedural checker pattern (`_Checks`) or, with `Texture Layers` on, up to 4 textures by
UV0 blended by weights in UV1 (x, y, z, w) — e.g. grass / rock / sand / dirt on a terrain. Texture layers work on
both `Surface Grid` and baked object-grid objects.

Cell sizes are given for a 720p frame and scale with the frame height. Turn off `Scale With Screen` to get an exact
size instead (`Screen Cell Pixels` = 6 → every pixel is 6×6 screen pixels at any resolution).

## Agent guide for this repository

When changing a game scene or adding pixel-rendered content, use this package as the rendering system rather than
implementing a second pixelation effect.

### Add pixel rendering to a scene

1. Find the scene's main **perspective** camera and add `EliotByte.PixelRender.PixelRenderManager` to it. Keep one
   manager on the camera that renders the scene. Set shared pixel size and stability options on its `Settings`.
2. For each 3D mesh that should use the effect, ensure it has a `MeshFilter` and `MeshRenderer`, assign a material
   whose shader is `EliotByte/PixelRender/Source`, and add `PixelRenderObject` to the same GameObject.
3. Use the shared manager settings by default. Enable `Own Settings` on an object only when that object needs a
   deliberate exception.
4. For large terrain, floors, or walls that span a wide range of camera depths, enable `Surface Grid` on
   `PixelRenderObject`. Use the ordinary object grid for compact props, vehicles, and similar objects.
5. Enter Play mode and inspect the result while moving the camera. Adjust cell size and the manager's bake thresholds
   only after checking the scene at its intended camera distance and resolution.

### Important integration rules

- Layer **31** is reserved. `PixelRenderObject` assigns its GameObject to this layer at runtime; do not assign layer
  31 to unrelated objects. The manager excludes that layer from the main camera and renders it through its hidden
  source camera.
- Keep both shaders available in builds: `EliotByte/PixelRender/Source` for source materials and
  `EliotByte/PixelRender/Resolve` for the package's resolve pass. Do not rename shader names or move shader files
  without updating their references.
- The source material supplies the object's color and shading. For ordinary materials, use the source shader's
  checker option; when using texture layers, provide UV0 and layer weights in UV1. Texture layers are currently
  supported on `Surface Grid` objects.
- The runtime component requires a `MeshRenderer` and expects a `MeshFilter` with a mesh. It is intended for 3D
  geometry rendered by a perspective URP camera, not Canvas UI, sprites, or orthographic cameras.
- Shared defaults belong on `PixelRenderManager.Settings`. Avoid setting `Own Settings` on every object, since this
  makes scene-wide tuning harder and creates inconsistent pixel sizes.
- Prefer editing game-owned scene setup and materials over modifying package runtime code. If package code or shader
  behavior must change, review the demo scenes and run the motion test described below to catch flicker or resolve
  regressions.

### Repository paths and examples

- Runtime API: `Assets/External/PixelRender/Runtime/`
- Shaders: `Assets/External/PixelRender/Shaders/`
- Working example: `Assets/External/PixelRender/Demo/Scenes/PixelRender.unity`
- Terrain example with `Surface Grid`: `Assets/Game/Sandbox/Editor/SandboxSceneCreator.cs`
- Sandbox terrain material setup: `Assets/Game/Sandbox/Scripts/SandboxTerrain.cs`

Paths above are relative to the Unity project directory (`RetroRiding/`). The repository root contains that project
as a subdirectory.

## Demo

`Demo/` contains the test scene (`Demo/Scenes/PixelRender.unity`), its materials and meshes, a rain of capsules
and a camera fly-through. Menu *Tools → Pixel Render*:

- **Create Pixel Render Scene** — (re)creates the demo scene; existing materials are kept.
- **Pixel Render Motion Test** — renders a camera fly-through and writes flicker/grain/mismatch statistics and
  heat maps to `SandboxCaptures/PixelRender/` in the project root. Can also be triggered by a
  `PixelRenderMotion.request` file in the project root (its content is the test label).

`Tools~/PixelRenderSim/` — Python models of parts of the pipeline used for debugging outside Unity
(numpy, scipy, pillow).

Code comments are in Russian.

## License

MIT, see `LICENSE`.
