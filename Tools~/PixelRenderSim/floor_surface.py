"""
Пол с сеткой на поверхности (Surface Grid) — порт PixelRenderManager.UpdateSurfaceBake и ветки _SurfaceGrid
из PixelRenderResolve. Сцены (камера как в демо, PixelRenderCameraMotion):
  floor — белый пол (коробка 20×0.4×20, как в Unity), чёрный фон;
  cube  — тот же пол и оранжевый куб, стоящий на нём (куб — обычный объект с запечённым спрайтом, scene3).
Метрики за кадр:
  мигания силуэта пола — пиксели, где «пол / не пол» сменилось A→B→A за три кадра;
  дырки — чёрные пиксели, у которых ≥3 из 4 соседей нарисованы (как в scene3).
    python floor_surface.py floor|cube [frames] [mode]      mode: v1 (как сейчас в Unity) | fix
Кадры — out/floor_surface/<сцена>_<mode>/.
"""
import math, os, sys
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixelsim import W, H, FOCAL, NEAR, Cam, XX, YY, euler
import scene3
from scene3 import Box, PObj, camera_at, capture, live, resolve_baked, holes

CELL = 7.0
DISTORTION, ANGLE = 0.15, 6.0
FLOOR_ID, CUBE_ID = 1, 2
MODE = 'v1'
PX = XX + 0.5; PY = YY + 0.5


def project(cam, X):
    d = X - cam.pos
    vx, vy, vz = d @ cam.r, d @ cam.u, d @ cam.f
    w = vz
    vz = np.where(np.abs(vz) < 1e-9, 1e-9, vz)
    return W * 0.5 + FOCAL * vx / vz, H * 0.5 + FOCAL * vy / vz, w


def ray_dir(cam, sx, sy):
    d = ((sx - W * 0.5) / FOCAL)[..., None] * cam.r + ((sy - H * 0.5) / FOCAL)[..., None] * cam.u + cam.f
    return d / np.linalg.norm(d, axis=-1, keepdims=True)


def world_at(cam, sx, sy, depth):
    r = ray_dir(cam, sx, sy)
    return cam.pos + r * (depth / np.maximum(r @ cam.f, 1e-4))[..., None]


def ray_plane(origin, direction, pp, pn):
    facing = np.sum(direction * pn, -1)
    ok = np.abs(facing) > 1e-6
    t = np.sum((pp - origin) * pn, -1) / np.where(ok, facing, 1.0)
    return origin + direction * t[..., None], ok & (t > 0)


# ---------------------------------------------------------------- source: id + depth (цвет однотонный)
def render_source(cam, objs):
    ids = np.zeros((H, W), int); dep = np.zeros((H, W))
    for o in objs:
        d, _ = o.s.hit(cam, PX, PY)
        m = (d > 0) & ((dep <= 0) | (d < dep))
        ids = np.where(m, o.id, ids); dep = np.where(m, d, dep)
    return ids, dep, ids * 2


def fetch(src, x, y):
    xi = np.clip(np.floor(x).astype(int), 0, W - 1); yi = np.clip(np.floor(y).astype(int), 0, H - 1)
    return src[0][yi, xi], src[1][yi, xi]


class Bake:
    def __init__(self):
        self.cam = None; self.count = 0

    def update(self, cam, surfaces):
        if self.cam is not None:
            ang = lambda a, b: math.degrees(math.acos(max(-1, min(1, a @ b))))
            near = min(np.linalg.norm(np.clip(cam.pos, s.c - s.half, s.c + s.half) - cam.pos) for s in surfaces)
            near = max(near, NEAR * 2)
            if ang(cam.r, self.cam.r) <= ANGLE and ang(cam.u, self.cam.u) <= ANGLE \
                    and np.linalg.norm(cam.pos - self.cam.pos) <= DISTORTION * near:
                return False
        self.cam = Cam(cam.pos.copy(), cam.pos + cam.f); self.count += 1
        return True


# ---------------------------------------------------------------- маска (fix): решение «клетка занята» в момент запекания
MARGIN = 0.25
MW, MH = int(math.ceil(W / CELL * (1 + 2 * MARGIN))), int(math.ceil(H / CELL * (1 + 2 * MARGIN)))
MOX, MOY = int(math.ceil(W / CELL * MARGIN)), int(math.ceil(H / CELL * MARGIN))


def capture_mask(src):
    jj, ii = np.mgrid[0:MH, 0:MW]
    cx = (ii - MOX + 0.5) * CELL; cy = (jj - MOY + 0.5) * CELL
    on = (cx >= 0) & (cy >= 0) & (cx < W) & (cy < H)
    i, a = fetch(src, cx, cy)
    return np.where(on, np.where(a > 0, i, 0), -1).astype(float)


def own_plane(cam, src, sx, sy):
    i0, a0 = fetch(src, sx, sy)
    p = world_at(cam, sx, sy, a0)
    n = np.zeros(p.shape); n[..., 1] = 1
    def nb(dx, dy):
        i, a = fetch(src, sx + dx, sy + dy)
        return (i == FLOOR_ID) & (a > 0), world_at(cam, sx + dx, sy + dy, a)
    hx, px_ = nb(2, 0); hx2, px2 = nb(-2, 0)
    px_ = np.where(hx[..., None], px_, px2); hx |= hx2
    hy, py_ = nb(0, 2); hy2, py2 = nb(0, -2)
    py_ = np.where(hy[..., None], py_, py2); hy |= hy2
    c = np.cross(px_ - p, py_ - p); l = np.linalg.norm(c, axis=-1)
    good = hx & hy & (l > 1e-7)
    n = np.where(good[..., None], c / np.maximum(l, 1e-12)[..., None], n)
    return p, n


def resolve_floor(cam, bake, src, mask):
    """Возвращает (рисовать?, глубина, путь) на весь экран."""
    sid, sa = src[0], src[1]
    own = (sid == FLOOR_ID) & (sa > 0); occ = (sid > 0) & (sa > 0)
    srcx = PX.copy(); srcy = PY.copy(); found = own.copy()
    dirs = [(1, 0), (-1, 0), (0, 1), (0, -1), (.7071, .7071), (-.7071, .7071), (.7071, -.7071), (-.7071, -.7071)]
    for ring in range(1, 5):
        for dx, dy in dirs:
            cx = np.floor(PX + dx * ring * 0.5 * CELL) + 0.5; cy = np.floor(PY + dy * ring * 0.5 * CELL) + 0.5
            i, a = fetch(src, cx, cy)
            hit = ~found & (i == FLOOR_ID) & (a > 0)
            srcx = np.where(hit, cx, srcx); srcy = np.where(hit, cy, srcy); found |= hit
    path = np.where(own, 4, 0); depth = np.where(own, sa, np.inf)
    pp, pn = own_plane(cam, src, srcx, srcy)
    rd = ray_dir(cam, PX, PY)
    Xr, okr = ray_plane(cam.pos, rd, pp, pn)
    X = np.where(own[..., None], world_at(cam, PX, PY, sa), Xr)
    hit = found & (own | okr)
    bx, by, bw = project(bake.cam, X)
    cix, ciy = np.floor(bx / CELL), np.floor(by / CELL)
    cbx, cby = (cix + 0.5) * CELL, (ciy + 0.5) * CELL
    C, okc = ray_plane(bake.cam.pos, ray_dir(bake.cam, cbx, cby), pp, pn)
    sx, sy, sw = project(cam, C)
    onscr = (sw > 0) & (sx >= 0) & (sy >= 0) & (sx < W) & (sy < H)
    ci, ca = fetch(src, sx, sy)
    c_own = (ci == FLOOR_ID) & (ca > 0)
    tol = 0.98 if MODE == 'v1' else 1.02
    c_hidden = (ci > 0) & (ca > 0) & ~c_own & (ca < sw * tol)
    valid = hit & (bw > 0) & okc & onscr
    occupied = c_own; empty = ~c_own & ~c_hidden; hidden = c_hidden
    if MODE == 'fix':
        mx = (cix + MOX).astype(int); my = (ciy + MOY).astype(int)
        inm = (mx >= 0) & (my >= 0) & (mx < MW) & (my < MH)
        m = np.full(PX.shape, -1.0); m[inm] = mask[my[inm], mx[inm]]
        known = inm & ((m == FLOOR_ID) | (m == 0))
        occupied = np.where(known, m == FLOOR_ID, occupied)
        empty = np.where(known, m == 0, empty)
        hidden = np.where(known, False, hidden)
        valid = valid | (known & hit & (bw > 0))
    world_depth = np.sum((X - cam.pos) * cam.f, -1)
    # свой пиксель
    cut = own & valid & empty
    path = np.where(cut, 0, path)
    # чужой пиксель
    out = ~own & valid & occupied
    path = np.where(out, 8, path)
    depth = np.where(out, np.where(occ & (sa < world_depth), 2e6, world_depth), depth)
    patch = ~own & valid & ~occupied & hidden & occ
    path = np.where(patch, 8, path); depth = np.where(patch, 2e6, depth)
    return path > 0, depth, path


def run(scene, frames, out):
    floor = PObj(Box(FLOOR_ID, [0, -0.2, 0], (20, 0.4, 20), np.eye(3), [(240, 240, 240)] * 2), screen_grid=True)
    objs = [floor]
    if scene == 'cube':
        objs.append(PObj(Box(CUBE_ID, [0, 0.75, 0], (1.5, 1.5, 1.5), euler(0, 30, 0), [(255, 140, 20)] * 2)))
    os.makedirs(out, exist_ok=True)
    bake = Bake(); mask = None; hist = []; stats = []
    for f in range(frames):
        cam = camera_at(1.6 + f / 60)
        for o in objs:
            o.update(cam)
        rebaked = bake.update(cam, [floor.s])
        src = render_source(cam, objs)
        if rebaked:
            mask = capture_mask(src)
        for o in objs[1:]:
            if o.visible:
                if not o.pending and hasattr(o, 'maskD'):
                    live(o, src)
                if o.pending:
                    capture(o, src); o.pending = False
        img = np.zeros((H, W), int); best = np.full((H, W), np.inf); fl = np.zeros((H, W), bool)
        draw, d, p = resolve_floor(cam, bake, src, mask)
        img[draw] = 1; best[draw] = d[draw]; fl = draw.copy()
        for o in objs[1:]:
            if not o.visible:
                continue
            xs, ys, C, D, P = resolve_baked(o, src)
            sub = np.ix_(ys, xs)
            m = (C > 0) & (D <= best[sub])
            img[sub] = np.where(m, 2, img[sub]); best[sub] = np.where(m, D, best[sub])
        hist.append(img.copy())
        if len(hist) >= 3:
            a, b, c = hist[-3], hist[-2], hist[-1]
            aba = (c != b) & (c == a)
            floor_bg = aba & ((c == 1) | (b == 1)) & (a != 2) & (b != 2) & (c != 2)      # пол ↔ фон
            obj_edge = aba & ~floor_bg                                                      # край куба
            blink, eblink = int(floor_bg.sum()), int(obj_edge.sum())
        else:
            blink = eblink = 0
            floor_bg = obj_edge = np.zeros((H, W), bool)
        hl = int(holes(img, src).sum())
        black_floor = int(((img == 0) & (src[0] > 0) & np.roll(img > 0, 1, 0) & np.roll(img > 0, -1, 0)).sum())
        stats.append((blink, hl, rebaked, eblink, black_floor))
        rgb = np.zeros((H, W, 3), np.uint8); rgb[img == 1] = (240, 240, 240); rgb[img == 2] = (255, 140, 20)
        marked = rgb.copy()
        marked[floor_bg] = (255, 0, 60)                   # пол ↔ фон мигнул A→B→A
        marked[obj_edge] = (190, 0, 255)                  # край куба мигнул
        hm = holes(img, src)
        grow = hm | np.roll(hm, 1, 0) | np.roll(hm, -1, 0) | np.roll(hm, 1, 1) | np.roll(hm, -1, 1)
        marked[grow & ~hm] = (0, 255, 255); marked[hm] = (0, 120, 255)   # чёрные дырки (обведены голубым)
        Image.fromarray(np.concatenate([rgb[::-1], marked[::-1]], 0)).save(f'{out}/{f:04d}.png')
    s = np.array(stats)
    print(f'{scene} {MODE}: мигания пол↔фон {s[2:,0].mean():.1f} пикс/кадр, мигания края куба {s[2:,3].mean():.1f}, '
          f'дырки {s[:,1].mean():.1f}, чёрные полоски внутри {s[:,4].mean():.1f} пикс/кадр, запеканий {int(s[:,2].sum())} за {frames} кадров')
    return s


if __name__ == '__main__':
    scene = sys.argv[1] if len(sys.argv) > 1 else 'floor'
    frames = int(sys.argv[2]) if len(sys.argv) > 2 else 45
    MODE = sys.argv[3] if len(sys.argv) > 3 else 'v1'
    run(scene, frames, os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out', 'floor_surface', f'{scene}_{MODE}'))
