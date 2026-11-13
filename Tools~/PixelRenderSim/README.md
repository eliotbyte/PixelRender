# PixelRender — отладочная сцена вне Unity

Python-порт силуэтной части PixelRender: один шар одним цветом, камера как в `PixelRenderCameraMotion`,
1280×720, FOV 50. Построчно перенесены `UpdateGrid / UpdateBake / SpriteError / PrepareMask`
(PixelRenderObject.cs) и `CaptureCell / LiveCell / Frag (ветка _BakedMask)` (PixelRenderResolve.shader).

Сравнение каждый кадр:
- **TRUE** — точный силуэт сферы (луч–сфера в каждом пикселе);
- **GT** — свежая сетка с якорем в текущем центре, клетка занята, если её центр внутри сферы.

Запуск (нужны numpy, scipy, pillow):
    python run.py                    # шар один: bake / live / hysteresis / без запекания
    python ball_floor_occluder.py    # шар + пол + шарик, пролетающий перед ним; ORIGINAL и FIX
    python floor_lod.py [race|orbit] # пол: экранная сетка против сетки на поверхности → out/FloorLod/

Флаги исправления: `pixelsim.FIX = {'near_limit': ..., 'path3_depth': ...}`; сцена: `pixelsim.SCENE`.
