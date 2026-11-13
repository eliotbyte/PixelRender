"""
Two checkered spheres, camera dollies back and forth fast (cycle 1.2 s), the near sphere spins
counter-clockwise (as seen from the camera) at SPIN deg/s. Full colour path of PixelRender:
 source  - colour of a pixel = majority of the checker over a cell-sized footprint around it (PixelRenderSource)
 capture - filled cell colour = majority of its own pixels (16 points), LiveCell keeps colour of filled cells
 resolve - filled: mask colour; unknown: OwnCellColor / seam rules (a)+(b) with neighbour-cell colour
Reference: each object pixelised alone on a fresh grid, cell filled iff centre inside, colour = footprint majority at the centre.
"""
import math, os, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixelsim import W, H, FOCAL, NEAR, OFFS16, Cam, XX, YY

CELL = 7.0
FPS, SECONDS = 60, 4.0
PERIOD = 1.2
SPIN = 180.0                      # deg/s, counter-clockwise on screen
Z_MID, Z_AMP = -13.0, 6.5
FAR_C, FAR_R = np.array([0.0, 0.0, 0.0]), 2.0
NEAR_C, NEAR_R = np.array([1.6, 0.0, -4.0]), 1.3
PALETTE = {2: (235, 235, 228), 3: (120, 120, 126),      # id 1 (far): light / dark
           4: (255, 140, 30), 5: (120, 45, 10)}         # id 2 (near): orange / dark
CHECKS = {1: (12, 6), 2: (8, 4)}


def camera(t):
    z = Z_MID - Z_AMP * math.cos(2 * math.pi * t / PERIOD)
    return Cam(np.array([0.0, 0.8, z]), FAR_C.copy())


def rot_z(deg):
    a = math.radians(deg)
    return np.array([[math.cos(a), -math.sin(a), 0], [math.sin(a), math.cos(a), 0], [0, 0, 1.0]])


def ray_sphere(cam, c, r, sx, sy):
    """depth (view z) and world hit point for screen points; depth 0 = miss."""
    px = (sx - W * 0.5) / FOCAL
    py = (sy - H * 0.5) / FOCAL
    cv = cam.to_view(c)
    dd = px * px + py * py + 1.0
    b = px * cv[0] + py * cv[1] + cv[2]
    disc = b * b - dd * (cv @ cv - r * r)
    hit = disc >= 0
    t = np.where(hit, (b - np.sqrt(np.maximum(disc, 0))) / dd, 0.0)
    pw = cam.pos + t[..., None] * (px[..., None] * cam.r + py[..., None] * cam.u + cam.f)
    return t, pw


class Obj:
    def __init__(self, oid, c, r):
        self.id, self.c, self.R = oid, np.asarray(c, float), r
        self.rot = np.eye(3)
        self.cell = CELL
        self.extent = math.sqrt(3) * r
        self.baked = False; self.pending = False; self.bakes = 0
        self.hyst, self.max_err, self.bake_angle, self.bake_scale = 0.2, 1.0, 6.0, 0.15

    def checker(self, pw):
        loc = (pw - self.c) @ self.rot           # world -> local (rot is local -> world)
        lon = np.arctan2(loc[..., 2], loc[..., 0])
        lat = np.arcsin(np.clip(loc[..., 1] / self.R, -1, 1))
        u = (lon / (2 * np.pi) + 0.5) * CHECKS[self.id][0]
        v = (lat / np.pi + 0.5) * CHECKS[self.id][1]
        return (np.floor(u) + np.floor(v)).astype(int) % 2

    def footprint_colour(self, cam, sx, sy):
        """majority of the checker over a cell-sized footprint (16 points) around each screen point -> colour code."""
        dark = np.zeros(sx.shape); hits = np.zeros(sx.shape)
        for j in range(16):
            t, pw = ray_sphere(cam, self.c, self.R, sx + OFFS16[0][j] * CELL, sy + OFFS16[1][j] * CELL)
            h = t > 0
            ch = self.checker(pw)
            dark += np.where(h, ch, 0); hits += h
        maj = np.where(hits > 0, (dark * 2 > hits).astype(int), 0)
        return self.id * 2 + maj

    def corners(self):
        return [self.c + self.rot @ (np.array([sx, sy, sz]) * self.R) for sz in (-1, 1) for sy in (-1, 1) for sx in (-1, 1)]

    # ---- CPU side (PixelRenderObject)
    def update_grid(self, cam):
        a = cam.w2s(self.c)
        self.anchor = a[:2]
        sr = self.extent * FOCAL / max(a[2] - self.extent, NEAR) + self.cell * 2
        self.rect = (max(0, a[0] - sr), max(0, a[1] - sr), min(W, a[0] + sr), min(H, a[1] + sr))
        self.visible = self.rect[2] > self.rect[0] and self.rect[3] > self.rect[1]
        if not self.visible:
            return
        depth = (self.c - cam.pos) @ cam.f
        self.depth = depth
        to_cam = cam.pos - self.c
        d_loc = self.rot.T @ (to_cam / np.linalg.norm(to_cam))
        up_loc = self.rot.T @ cam.u
        world_cell = self.cell * max(depth, NEAR) / FOCAL
        rebake = not self.baked
        if self.baked:
            ang = lambda p, q: math.degrees(math.acos(max(-1, min(1, p @ q))))
            rebake = (self.sprite_error(cam, depth) > self.max_err * self.cell
                      or ang(d_loc, self.bake_dir) > self.bake_angle or ang(up_loc, self.bake_up) > self.bake_angle
                      or abs(math.log(world_cell / self.bake_world_cell)) > math.log(1 + self.bake_scale))
        if rebake:
            self.grid = (self.anchor[0], self.anchor[1], self.cell)
            self.bake_dir, self.bake_up, self.bake_world_cell, self.bake_depth = d_loc, up_loc, world_cell, depth
            self.bake_anchor = self.anchor.copy()
            self.bake_corners = [cam.w2s(c)[:2] for c in self.corners()]
            self.baked = True; self.bakes += 1
            x0, y0, x1, y1 = self.rect
            fx = math.floor((x0 - self.anchor[0]) / self.cell) - 2; fy = math.floor((y0 - self.anchor[1]) / self.cell) - 2
            tx = math.ceil((x1 - self.anchor[0]) / self.cell) + 2; ty = math.ceil((y1 - self.anchor[1]) / self.cell) + 2
            self.mrect = (fx, fy, int(min(max(tx - fx, 1), 1024)), int(min(max(ty - fy, 1), 1024)))
            self.pending = True
        self.scale = depth / max(self.bake_depth, 1e-4)
        self.cur = (round(self.anchor[0]), round(self.anchor[1]), self.scale, self.bake_depth - depth)
        self.near_limit = depth - self.extent

    def sprite_error(self, cam, depth):
        scale = depth / max(self.bake_depth, 1e-4)
        err = 0
        for i, c in enumerate(self.corners()):
            act = cam.w2s(c)
            if act[2] <= NEAR:
                return float('inf')
            pred = self.anchor + (self.bake_corners[i] - self.bake_anchor) / max(scale, 1e-4)
            err = max(err, np.linalg.norm(pred - act[:2]))
        return err

    def centres(self):
        fx, fy, mw, mh = self.mrect
        jj, ii = np.mgrid[0:mh, 0:mw]
        return self.grid[0] + (fx + ii + 0.5) * self.grid[2], self.grid[1] + (fy + jj + 0.5) * self.grid[2]


# ---------------------------------------------------------------- source
def render_source(cam, objs):
    ids = np.zeros((H, W), int); dep = np.zeros((H, W)); col = np.zeros((H, W), int)
    for o in objs:
        if not o.visible:
            continue
        x0, y0, x1, y1 = [int(v) for v in (math.floor(o.rect[0]), math.floor(o.rect[1]), math.ceil(o.rect[2]), math.ceil(o.rect[3]))]
        sx, sy = XX[y0:y1, x0:x1] + 0.5, YY[y0:y1, x0:x1] + 0.5
        d, _ = ray_sphere(cam, o.c, o.R, sx, sy)
        sub = (slice(y0, y1), slice(x0, x1))
        m = (d > 0) & ((dep[sub] <= 0) | (d < dep[sub]))
        if m.any():
            c = np.zeros(d.shape, int)
            c[m] = o.footprint_colour(cam, sx[m], sy[m])
            dep[sub] = np.where(m, d, dep[sub]); ids[sub] = np.where(m, o.id, ids[sub]); col[sub] = np.where(m, c, col[sub])
    return ids, dep, col


def fetch(src, x, y):
    xi = np.clip(np.floor(x).astype(int), 0, W - 1); yi = np.clip(np.floor(y).astype(int), 0, H - 1)
    return src[0][yi, xi], src[1][yi, xi], src[2][yi, xi]


def majority(cols, valid):
    """most frequent colour code among valid samples (list of arrays)."""
    best = np.zeros(cols[0].shape, int); bestv = np.zeros(cols[0].shape)
    for a in range(16):
        votes = sum(valid[b] & (cols[b] == cols[a]) for b in range(16)) * valid[a]
        u = votes > bestv
        best = np.where(u, cols[a], best); bestv = np.where(u, votes, bestv)
    return best, bestv > 0


def samples(src, cx, cy, cellp):
    return [fetch(src, cx + OFFS16[0][j] * cellp, cy + OFFS16[1][j] * cellp) for j in range(16)]


# ---------------------------------------------------------------- mask passes
def capture(o, src):
    cx, cy = o.centres(); cell = o.grid[2]
    off = (cx < 0) | (cy < 0) | (cx >= W) | (cy >= H)
    x0, y0 = np.floor(cx - 0.5), np.floor(cy - 0.5); fx, fy = cx - 0.5 - x0, cy - 0.5 - y0
    own = np.zeros_like(cx); bt = np.full_like(cx, -1.0); tid = np.zeros_like(cx); td = np.zeros_like(cx); oa = np.zeros_like(cx)
    for k in range(4):
        ox, oy = k & 1, k >> 1
        w = (fx if ox else 1 - fx) * (fy if oy else 1 - fy)
        i, a, _ = fetch(src, x0 + ox + 0.5, y0 + oy + 0.5)
        m = (i == o.id) & (a > 0)
        own += np.where(m, w, 0); oa = np.where(m, a, oa)
        u = ~m & (w > bt); bt = np.where(u, w, bt); tid = np.where(u, np.where(a > 0, i, 0), tid); td = np.where(u, a, td)
    S = samples(src, cx, cy, cell)
    valid = [(i == o.id) & (a > 0) for i, a, _ in S]
    nearest = np.full_like(cx, 1e9)
    for (i, a, _), v in zip(S, valid):
        nearest = np.where(v, np.minimum(nearest, a), nearest)
    colr, found = majority([c for _, _, c in S], valid)
    limit = np.where(nearest < 1e8, nearest, o.bake_depth - o.extent)
    filled = own >= 0.5
    D = np.where(filled, np.maximum(np.where(found, nearest, oa), 1e-3), np.where((tid > 0) & (td < limit * 0.99), -1.0, 0.0))
    D = np.where(off, -1.0, D)
    o.maskD, o.maskC = D, np.where(filled, np.where(found, colr, o.id * 2), 0)


def live(o, src):
    cx0, cy0 = o.centres(); g = o.grid; cur = o.cur
    s = max(cur[2], 1e-4)
    cx = cur[0] + (cx0 - g[0]) / s; cy = cur[1] + (cy0 - g[1]) / s; cellp = g[2] / s
    off = (cx < 0) | (cy < 0) | (cx >= W) | (cy >= H)
    S = samples(src, cx, cy, cellp)
    valid = [(i == o.id) & (a > 0) for i, a, _ in S]
    nearest = np.full_like(cx, 1e9)
    for (i, a, _), v in zip(S, valid):
        nearest = np.where(v, np.minimum(nearest, a), nearest)
    limit = np.where(nearest < 1e8, nearest, o.near_limit)
    own_n = sum(v.astype(float) for v in valid)
    free_n = sum((~v & (~((a > 0)) | (a >= limit * 0.99))).astype(float) for (i, a, _), v in zip(S, valid))
    vis = own_n + free_n; cov = own_n / np.maximum(vis, 1)
    prev = o.maskD; was = prev > 0
    filled = np.where(prev < 0, cov >= 0.5, np.where(was, cov > 0.5 - o.hyst, cov > 0.5 + o.hyst))
    colr, _ = majority([c for _, _, c in S], valid)
    newD = np.where(filled, np.where(was, prev, np.maximum(nearest + cur[3], 1e-3)), 0.0)
    newC = np.where(filled, np.where(was, o.maskC, colr), 0)
    keep = off | (vis < 8)
    o.maskD = np.where(keep, prev, newD); o.maskC = np.where(keep, o.maskC, newC)


# ---------------------------------------------------------------- resolve (baked mask branch, current shader)
def resolve(o, src):
    outC = np.zeros((H, W), int); outD = np.full((H, W), np.inf)
    if not o.visible:
        return outC, outD
    x0, y0, x1, y1 = o.rect
    xs = np.arange(int(math.floor(x0)), int(math.ceil(x1))); ys = np.arange(int(math.floor(y0)), int(math.ceil(y1)))
    xs = xs[(xs + 0.5 >= x0) & (xs + 0.5 < x1)]; ys = ys[(ys + 0.5 >= y0) & (ys + 0.5 < y1)]
    PX, PY = np.meshgrid(xs + 0.5, ys + 0.5)
    gx, gy, cell = o.grid; s = o.scale; ra = o.cur[:2]
    cix = np.floor(((PX - ra[0]) * s) / cell); ciy = np.floor(((PY - ra[1]) * s) / cell)
    mx = (cix - o.mrect[0]).astype(int); my = (ciy - o.mrect[1]).astype(int)
    inr = (mx >= 0) & (my >= 0) & (mx < o.mrect[2]) & (my < o.mrect[3])
    mD = np.full(PX.shape, -1.0); mC = np.zeros(PX.shape, int)
    mD[inr] = o.maskD[my[inr], mx[inr]]; mC[inr] = o.maskC[my[inr], mx[inr]]
    ccx = ra[0] + (cix + 0.5) * cell / max(s, 1e-4); ccy = ra[1] + (ciy + 0.5) * cell / max(s, 1e-4); cellp = cell / max(s, 1e-4)
    C = np.zeros(PX.shape, int); Dp = np.full(PX.shape, np.inf)
    f = mD > 0
    if f.any():
        i, a, _ = fetch(src, ccx[f], ccy[f])
        S = samples(src, ccx[f], ccy[f], cellp)
        near = np.full(i.shape, 1e9)
        for (si, sa, _) in S:
            near = np.where((si == o.id) & (sa > 0), np.minimum(near, sa), near)
        stored = np.maximum(o.depth + (mD[f] - o.bake_depth), 1e-3)
        Dp[f] = np.where((i == o.id) & (a > 0), a, np.where(near < 1e8, near, stored)); C[f] = mC[f]
    u = mD < 0
    if u.any():
        S = samples(src, ccx[u], ccy[u], cellp)
        valid = [(i == o.id) & (a > 0) for i, a, _ in S]
        near = np.full(ccx[u].shape, 1e9)
        for (si, sa, _), v in zip(S, valid):
            near = np.where(v, np.minimum(near, sa), near)
        colr, found = majority([c for _, _, c in S], valid)
        cu = np.where(found, colr, 0); du = np.where(found, near, np.inf)
        pid, pa, pc = fetch(src, PX[u], PY[u])
        ux, uy = mx[u], my[u]
        cand_d = np.full(ux.shape, np.inf); cand_c = np.zeros(ux.shape, int)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                nx, ny = ux + dx, uy + dy
                ok = (nx >= 0) & (ny >= 0) & (nx < o.mrect[2]) & (ny < o.mrect[3])
                v = np.zeros(nx.shape); vc = np.zeros(nx.shape, int)
                v[ok] = o.maskD[ny[ok], nx[ok]]; vc[ok] = o.maskC[ny[ok], nx[ok]]
                nd = np.maximum(o.depth + (v - o.bake_depth), 1e-3)
                better = (v > 0) & (nd < cand_d)
                cand_d = np.where(better, nd, cand_d); cand_c = np.where(better, vc, cand_c)
        rest = ~found & (pa > 0)
        mine = rest & (pid == o.id)                                           # (a)
        cu = np.where(mine, np.where(np.isfinite(cand_d), cand_c, pc), cu); du = np.where(mine, pa, du)
        hit = rest & ~mine & (pid > 0) & np.isfinite(cand_d) & (pa < cand_d)  # (b)
        cu = np.where(hit, cand_c, cu); du = np.where(hit, cand_d, du)
        C[u] = cu; Dp[u] = du
    sub = np.ix_(ys, xs)
    outC[sub] = C; outD[sub] = np.where(C > 0, Dp, np.inf)
    return outC, outD


def reference(cam, objs):
    img = np.zeros((H, W), int); best = np.full((H, W), np.inf)
    for o in objs:
        a = cam.w2s(o.c)
        cx = a[0] + (np.floor((XX + 0.5 - a[0]) / CELL) + 0.5) * CELL
        cy = a[1] + (np.floor((YY + 0.5 - a[1]) / CELL) + 0.5) * CELL
        d, _ = ray_sphere(cam, o.c, o.R, cx, cy)
        m = (d > 0) & (d < best)
        if m.any():
            col = np.zeros((H, W), int)
            # colour per cell: evaluate once per unique cell centre
            ucx, ucy = cx[m], cy[m]
            col[m] = o.footprint_colour(cam, ucx, ucy)
            img = np.where(m, col, img); best = np.where(m, d, best)
    return img


def to_rgb(code):
    rgb = np.zeros((H, W, 3), np.uint8)
    for k, c in PALETTE.items():
        rgb[code == k] = c
    return rgb[::-1]


def main(out):
    os.makedirs(out, exist_ok=True)
    font = ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf', 20)
    far = Obj(1, FAR_C, FAR_R); near = Obj(2, NEAR_C, NEAR_R); objs = [far, near]
    n = int(SECONDS * FPS); specks = 0
    for f in range(n):
        t = f / FPS
        cam = camera(t)
        near.rot = rot_z(SPIN * t)
        for o in objs:
            o.update_grid(cam)
        src = render_source(cam, objs)
        for o in objs:
            if not o.visible:
                continue
            if not o.pending and hasattr(o, 'maskD'):
                live(o, src)
            if o.pending:
                capture(o, src); o.pending = False
        ours = np.zeros((H, W), int); best = np.full((H, W), np.inf)
        for o in objs:
            c, d = resolve(o, src)
            m = (c > 0) & (d <= best)
            ours = np.where(m, c, ours); best = np.where(m, d, best)
        ref = reference(cam, objs)
        blk = ours == 0
        nb = sum(np.roll(~blk, (dy, dx), (0, 1)) for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)))
        specks += int((blk & (nb >= 3)).sum())
        a = Image.fromarray(to_rgb(ours)[:, 160:1120]); b = Image.fromarray(to_rgb(ref)[:, 160:1120])
        ImageDraw.Draw(a).text((14, 12), f'PixelRender, вращение {SPIN:g}°/с против ч.с., цикл {PERIOD:g} с', fill=(190, 190, 190), font=font)
        ImageDraw.Draw(b).text((14, 12), 'эталон', fill=(190, 190, 190), font=font)
        Image.fromarray(np.concatenate([np.array(a), np.full((H, 4, 3), (255, 0, 255), np.uint8), np.array(b)], 1)).save(f'{out}/{f:04d}.png')
        if f % 30 == 0:
            print('frame', f, 'bakes far', far.bakes, 'near', near.bakes, flush=True)
    print(f'done: bakes far {far.bakes} near {near.bakes} / {n}; black specks total {specks}')


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else 'spin_out')
