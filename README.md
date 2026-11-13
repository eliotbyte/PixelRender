# Pixel Render

Stable pixel-art rendering for 3D scenes with a **perspective** camera in Unity URP (6000.6+).

The scene is drawn with chunky square "pixels" (cells of `Cell Pixels` screen pixels at 720p, integer size) at
**full screen resolution**. Cells are glued to the world, so they don't shimmer or crawl when the camera moves,
and only whole square cells are ever drawn — no cut pixels along polygon seams or silhouettes.

> Work in progress. The API, settings and shaders will change.

## Usage

1. Add `PixelRenderManager` to the main camera.
2. Put objects on layer **30** (`PixelRenderManager.Layer`) and give them a material with the
   `EliotByte/PixelRender/Source` shader (checker mode or up to 4 texture layers blended by UV1 weights).

## How it works

- **Source pass.** A hidden camera renders layer 30 into a float buffer: colour evaluated at the **centre of the
  cell** (own triangle plane), normal, depth and tile id.
- **Tiles (stable regions).** World is split into `Tile Size` × `Tile Size` m tiles (XZ). Each tile has its own
  baked grid (the "world → screen" matrix of the frame it was baked). A tile is re-baked only when its cells got
  distorted on screen by more than `Bake Max Error` pixels — near tiles often, far tiles rarely. Neighbouring tiles
  close to the limit are re-baked together (`Bake Group`) so they share one grid.
- **Edge quads.** A compute pass walks every triangle edge in the tile's bake grid and finds the cells the edge
  cuts. Each such cell is drawn as a whole quad by its owner triangle, before the resolve pass — this glues polygon
  seams and gives the stepped silhouette.
- **Resolve.** A full-screen quad picks, for every screen pixel, the cell it belongs to and outputs its colour.

Static objects take part in tiles and edge quads (their triangles are gathered on the CPU once). Moving objects
(with a `Rigidbody`) are drawn by the source and resolve passes only.

## Demo

`Demo/Scenes/PixelRender.unity` — ball, columns and 1000 capsules falling from the sky (instanced, one draw call
per material). `Demo/Scenes/PixelRenderTerrain.unity` — low-poly textured terrain with a capsule rain over it.
Scenes are (re)created from Tools → Pixel Render.

## Legacy

The previous implementation is in `Legacy/` (namespace `EliotByte.PixelRender.Legacy`, layer 31, shaders
`EliotByte/PixelRender/Legacy/*`) with its own demo scenes.
