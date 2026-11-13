"""
PixelRender test scene close to the Unity one: ball, 4 columns standing on the floor, capsules lying on the floor,
floor drawn with a screen grid (fresh path, not baked). Camera = PixelRenderCameraMotion.
Ports both Resolve branches: baked-mask (objects) and fresh grid (floor), with colours.
"""
import math, os, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixelsim import W, H, FOCAL, NEAR, OFFS16, Cam, XX, YY, euler

CELL = 7.0
SEAM_FIX = [True]          # rules (a)+(b) currently in the shader
EXTRA = set()              # experimental fixes


def camera_at(t, center=np.array([0, 2.2, 0.0]), dist=9.75, pitch=35, yaw0=25, orbit=3, amp=1.85, period=5.2, bob=0.3, bobp=7):
    yaw = yaw0 + orbit * t
    d = dist + amp * math.sin(t * 2 * math.pi / period)
    b = bob * math.sin(t * 2 * math.pi / bobp)
    pos = center + euler(pitch, yaw, 0) @ np.array([0, 0, -1.0]) * d + np.array([0, b, 0])
    return Cam(pos, center)


def rays(cam, sx, sy):
    px = (sx - W * 0.5) / FOCAL; py = (sy - H * 0.5) / FOCAL
    d = px[..., None] * cam.r + py[..., None] * cam.u + cam.f
    n = np.linalg.norm(d, axis=-1, keepdims=True)
    return d / n, 1.0 / n[..., 0]      # unit dir, view-depth per unit length


# ---------------------------------------------------------------- shapes
class Shape:
    def __init__(self, oid, c, rot, palette):
        self.id, self.c, self.rot, self.pal = oid, np.asarray(c, float), rot, palette

    def hit(self, cam, sx, sy):
        """view depth (0 = miss) and world point."""
        rd, k = rays(cam, sx, sy)
        t = self._t(cam.pos, rd)
        ok = t > 0
        pw = cam.pos + np.where(ok, t, 0)[..., None] * rd
        return np.where(ok, t * k, 0.0), pw

    def local_corners(self):
        e = self.half
        return [np.array([sx, sy, sz]) * e for sz in (-1, 1) for sy in (-1, 1) for sx in (-1, 1)]


class Sphere(Shape):
    def __init__(self, oid, c, r, rot, palette, checks=(16, 8)):
        super().__init__(oid, c, rot, palette); self.R = r; self.half = np.full(3, r); self.checks = checks

    def _t(self, o, rd):
        oc = o - self.c
        b = rd @ oc; cc = oc @ oc - self.R ** 2
        h = b * b - cc
        return np.where(h >= 0, -b - np.sqrt(np.maximum(h, 0)), -1.0)

    def checker(self, pw):
        loc = (pw - self.c) @ self.rot
        lon = np.arctan2(loc[..., 2], loc[..., 0]); lat = np.arcsin(np.clip(loc[..., 1] / self.R, -1, 1))
        return (np.floor((lon / (2 * np.pi) + 0.5) * self.checks[0]) + np.floor((lat / np.pi + 0.5) * self.checks[1])).astype(int) % 2


class Box(Shape):
    def __init__(self, oid, c, size, rot, palette, k=3.0):
        super().__init__(oid, c, rot, palette); self.half = np.asarray(size, float) / 2; self.k = k

    def _t(self, o, rd):
        ol = (o - self.c) @ self.rot; dl = rd @ self.rot
        inv = 1.0 / np.where(np.abs(dl) < 1e-9, 1e-9, dl)
        t1 = (-self.half - ol) * inv; t2 = (self.half - ol) * inv
        tn = np.max(np.minimum(t1, t2), axis=-1); tf = np.min(np.maximum(t1, t2), axis=-1)
        return np.where((tn <= tf) & (tn > 0), tn, -1.0)

    def checker(self, pw):
        loc = (pw - self.c) @ self.rot + self.half
        q = np.floor(loc * self.k + 1e-4)
        return (q[..., 0] + q[..., 1] + q[..., 2]).astype(int) % 2


class Capsule(Shape):
    def __init__(self, oid, c, r, length, rot, palette, k=2.2):
        super().__init__(oid, c, rot, palette); self.R = r; self.L = length
        self.a = self.c + rot @ np.array([0, -length / 2 + r, 0]); self.b = self.c + rot @ np.array([0, length / 2 - r, 0])
        self.half = np.array([r, length / 2, r]); self.k = k

    def _t(self, o, rd):
        ba = self.b - self.a; oa = o - self.a
        baba = ba @ ba; bard = rd @ ba; baoa = oa @ ba; rdoa = rd @ oa; oaoa = oa @ oa
        A = baba - bard * bard; B = baba * rdoa - baoa * bard; C = baba * oaoa - baoa * baoa - self.R ** 2 * baba
        h = B * B - A * C
        t = np.full(rd.shape[:-1], -1.0)
        tc = (-B - np.sqrt(np.maximum(h, 0))) / np.where(A == 0, 1e-9, A)
        y = baoa + tc * bard
        body = (h >= 0) & (y > 0) & (y < baba)
        t = np.where(body, tc, t)
        oc = np.where((y <= 0)[..., None], oa, o - self.b)
        bb = np.sum(rd * oc, -1); cc = np.sum(oc * oc, -1) - self.R ** 2; hh = bb * bb - cc
        cap = ~body & (h >= 0) & (hh > 0)
        return np.where(cap, -bb - np.sqrt(np.maximum(hh, 0)), t)

    def checker(self, pw):
        loc = (pw - self.c) @ self.rot
        ang = np.arctan2(loc[..., 2], loc[..., 0])
        return (np.floor(loc[..., 1] * self.k) + np.floor((ang / (2 * np.pi) + 0.5) * 6)).astype(int) % 2


class Floor(Shape):
    def __init__(self, oid, palette, size=20.0):
        super().__init__(oid, np.array([0, -0.2, 0.0]), np.eye(3), palette); self.size = size; self.half = np.array([size / 2, 0.2, size / 2])

    def _t(self, o, rd):
        t = -o[1] / np.where(rd[..., 1] < -1e-9, rd[..., 1], -1e-9)
        p = o + t[..., None] * rd
        ok = (rd[..., 1] < -1e-9) & (np.abs(p[..., 0]) < self.size / 2) & (np.abs(p[..., 2]) < self.size / 2)
        return np.where(ok, t, -1.0)

    def checker(self, pw):
        return (np.floor(pw[..., 0] * 0.5) + np.floor(pw[..., 2] * 0.5)).astype(int) % 2


# ---------------------------------------------------------------- pixel object state (PixelRenderObject)
class PObj:
    def __init__(self, shape, screen_grid=False):
        self.s = shape; self.id = shape.id; self.screen = screen_grid
        self.baked = False; self.pending = False; self.bakes = 0
        self.hyst, self.max_err, self.bake_angle, self.bake_scale = 0.2, 1.0, 6.0, 0.15
        cw = [shape.c + shape.rot @ lc for lc in shape.local_corners()]
        self.extent = np.linalg.norm((np.max(cw, 0) - np.min(cw, 0)) / 2)
        self.centre = (np.max(cw, 0) + np.min(cw, 0)) / 2      # renderer.bounds.center

    def corners(self):
        return [self.s.c + self.s.rot @ lc for lc in self.s.local_corners()]

    def update(self, cam):
        if self.screen:
            self.anchor = np.zeros(2); self.rect = (0, 0, W, H); self.visible = True; return
        a = cam.w2s(self.centre)
        if a[2] <= NEAR:
            self.visible = False; return
        self.anchor = a[:2]
        sr = self.extent * FOCAL / max(a[2] - self.extent, NEAR) + CELL * 2
        self.rect = (max(0, a[0] - sr), max(0, a[1] - sr), min(W, a[0] + sr), min(H, a[1] + sr))
        self.visible = self.rect[2] > self.rect[0] and self.rect[3] > self.rect[1]
        if not self.visible:
            return
        depth = (self.centre - cam.pos) @ cam.f; self.depth = depth
        to_cam = cam.pos - self.centre
        d_loc = self.s.rot.T @ (to_cam / np.linalg.norm(to_cam)); up_loc = self.s.rot.T @ cam.u
        world_cell = CELL * max(depth, NEAR) / FOCAL
        rebake = not self.baked
        if self.baked:
            ang = lambda p, q: math.degrees(math.acos(max(-1, min(1, p @ q))))
            rebake = (self.sprite_error(cam, depth) > self.max_err * CELL or ang(d_loc, self.bake_dir) > self.bake_angle
                      or ang(up_loc, self.bake_up) > self.bake_angle
                      or abs(math.log(world_cell / self.bake_world_cell)) > math.log(1 + self.bake_scale))
        if rebake:
            self.grid = (self.anchor[0], self.anchor[1], CELL)
            self.bake_dir, self.bake_up, self.bake_world_cell, self.bake_depth = d_loc, up_loc, world_cell, depth
            self.bake_anchor = self.anchor.copy(); self.bake_corners = [cam.w2s(c)[:2] for c in self.corners()]
            self.baked = True; self.bakes += 1
            x0, y0, x1, y1 = self.rect
            fx = math.floor((x0 - self.anchor[0]) / CELL) - 2; fy = math.floor((y0 - self.anchor[1]) / CELL) - 2
            tx = math.ceil((x1 - self.anchor[0]) / CELL) + 2; ty = math.ceil((y1 - self.anchor[1]) / CELL) + 2
            self.mrect = (fx, fy, int(min(max(tx - fx, 1), 1024)), int(min(max(ty - fy, 1), 1024)))
            self.pending = True
        self.scale = depth / max(self.bake_depth, 1e-4)
        self.cur = (round(self.anchor[0]), round(self.anchor[1]), self.scale, self.bake_depth - depth)
        self.near_limit = depth - self.extent

    def sprite_error(self, cam, depth):
        scale = depth / max(self.bake_depth, 1e-4); err = 0
        for i, c in enumerate(self.corners()):
            act = cam.w2s(c)
            if act[2] <= NEAR:
                return float('inf')
            err = max(err, np.linalg.norm(self.anchor + (self.bake_corners[i] - self.bake_anchor) / max(scale, 1e-4) - act[:2]))
        return err

    def centres(self):
        fx, fy, mw, mh = self.mrect
        jj, ii = np.mgrid[0:mh, 0:mw]
        return self.grid[0] + (fx + ii + 0.5) * CELL, self.grid[1] + (fy + jj + 0.5) * CELL


# ---------------------------------------------------------------- source (id, depth, colour code = id*2 + dark)
def render_source(cam, objs):
    ids = np.zeros((H, W), int); dep = np.zeros((H, W)); col = np.zeros((H, W), int)
    for o in objs:
        if not o.visible:
            continue
        x0, y0 = int(math.floor(o.rect[0])), int(math.floor(o.rect[1])); x1, y1 = int(math.ceil(o.rect[2])), int(math.ceil(o.rect[3]))
        sx, sy = XX[y0:y1, x0:x1] + 0.5, YY[y0:y1, x0:x1] + 0.5
        d, _ = o.s.hit(cam, sx, sy)
        sub = (slice(y0, y1), slice(x0, x1))
        m = (d > 0) & ((dep[sub] <= 0) | (d < dep[sub]))
        if m.any():
            dark = np.zeros(m.sum()); hits = np.zeros(m.sum())
            for j in range(16):
                dd, pw = o.s.hit(cam, sx[m] + OFFS16[0][j] * CELL, sy[m] + OFFS16[1][j] * CELL)
                h = dd > 0; dark += np.where(h, o.s.checker(pw), 0); hits += h
            c = np.zeros(d.shape, int); c[m] = o.id * 2 + (dark * 2 > hits)
            dep[sub] = np.where(m, d, dep[sub]); ids[sub] = np.where(m, o.id, ids[sub]); col[sub] = np.where(m, c, col[sub])
    return ids, dep, col


def fetch(src, x, y):
    xi = np.clip(np.floor(x).astype(int), 0, W - 1); yi = np.clip(np.floor(y).astype(int), 0, H - 1)
    return src[0][yi, xi], src[1][yi, xi], src[2][yi, xi]


def samples(src, cx, cy, cellp):
    return [fetch(src, cx + OFFS16[0][j] * cellp, cy + OFFS16[1][j] * cellp) for j in range(16)]


def majority(cols, valid):
    best = np.zeros(cols[0].shape, int); bestv = np.zeros(cols[0].shape)
    for a in range(16):
        votes = sum(valid[b] & (cols[b] == cols[a]) for b in range(16)) * valid[a]
        u = votes > bestv; best = np.where(u, cols[a], best); bestv = np.where(u, votes, bestv)
    return best, bestv > 0


def own_cell(src, oid, cx, cy, cellp):
    S = samples(src, cx, cy, cellp)
    valid = [(i == oid) & (a > 0) for i, a, _ in S]
    near = np.full(cx.shape, 1e9)
    for (i, a, _), v in zip(S, valid):
        near = np.where(v, np.minimum(near, a), near)
    colr, found = majority([c for _, _, c in S], valid)
    return found, colr, near


def bilinear(src, oid, cx, cy):
    x0, y0 = np.floor(cx - 0.5), np.floor(cy - 0.5); fx, fy = cx - 0.5 - x0, cy - 0.5 - y0
    own = np.zeros_like(cx); bo = np.full_like(cx, -1.0); oa = np.zeros_like(cx); oc = np.zeros(cx.shape, int)
    bt = np.full_like(cx, -1.0); tid = np.zeros(cx.shape, int); td = np.zeros_like(cx)
    for k in range(4):
        ox, oy = k & 1, k >> 1
        w = (fx if ox else 1 - fx) * (fy if oy else 1 - fy)
        i, a, c = fetch(src, x0 + ox + 0.5, y0 + oy + 0.5)
        m = (i == oid) & (a > 0)
        own += np.where(m, w, 0)
        u = m & (w > bo); bo = np.where(u, w, bo); oa = np.where(u, a, oa); oc = np.where(u, c, oc)
        u = ~m & (w > bt); bt = np.where(u, w, bt); tid = np.where(u, np.where(a > 0, i, 0), tid); td = np.where(u, a, td)
    return own, oa, oc, tid, td


def capture(o, src):
    cx, cy = o.centres()
    off = (cx < 0) | (cy < 0) | (cx >= W) | (cy >= H)
    own, oa, oc, tid, td = bilinear(src, o.id, cx, cy)
    found, colr, near = own_cell(src, o.id, cx, cy, CELL)
    limit = np.where(near < 1e8, near, o.bake_depth - o.extent)
    filled = own >= 0.5
    D = np.where(filled, np.maximum(np.where(found, near, oa), 1e-3), np.where((tid > 0) & (td < limit * 0.99), -1.0, 0.0))
    o.maskD = np.where(off, -1.0, D); o.maskC = np.where(filled & ~off, np.where(found, colr, oc), 0)


def live(o, src):
    cx0, cy0 = o.centres(); g = o.grid; cur = o.cur; s = max(cur[2], 1e-4)
    cx = cur[0] + (cx0 - g[0]) / s; cy = cur[1] + (cy0 - g[1]) / s; cellp = CELL / s
    off = (cx < 0) | (cy < 0) | (cx >= W) | (cy >= H)
    S = samples(src, cx, cy, cellp)
    valid = [(i == o.id) & (a > 0) for i, a, _ in S]
    near = np.full(cx.shape, 1e9)
    for (i, a, _), v in zip(S, valid):
        near = np.where(v, np.minimum(near, a), near)
    limit = np.where(near < 1e8, near, o.near_limit)
    own_n = sum(v.astype(float) for v in valid)
    free_n = sum((~v & ((a <= 0) | (a >= limit * 0.99))).astype(float) for (i, a, _), v in zip(S, valid))
    vis = own_n + free_n; cov = own_n / np.maximum(vis, 1)
    prev = o.maskD; was = prev > 0
    filled = np.where(prev < 0, cov >= 0.5, np.where(was, cov > 0.5 - o.hyst, cov > 0.5 + o.hyst))
    colr, _ = majority([c for _, _, c in S], valid)
    newD = np.where(filled, np.where(was, prev, np.maximum(near + cur[3], 1e-3)), 0.0)
    newC = np.where(filled, np.where(was, o.maskC, colr), 0)
    keep = off | (vis < 8)
    o.maskD = np.where(keep, prev, newD); o.maskC = np.where(keep, o.maskC, newC)


# ---------------------------------------------------------------- resolve
def rect_pixels(o):
    x0, y0, x1, y1 = o.rect
    xs = np.arange(int(math.floor(x0)), int(math.ceil(x1))); ys = np.arange(int(math.floor(y0)), int(math.ceil(y1)))
    xs = xs[(xs + 0.5 >= x0) & (xs + 0.5 < x1)]; ys = ys[(ys + 0.5 >= y0) & (ys + 0.5 < y1)]
    PX, PY = np.meshgrid(xs + 0.5, ys + 0.5)
    return xs, ys, PX, PY


def resolve_fresh(o, src):
    """non-baked branch (floor, ScreenGrid). returns colour code, depth, path."""
    xs, ys, PX, PY = rect_pixels(o)
    ax, ay = o.anchor
    cx = ax + (np.floor((PX - ax) / CELL) + 0.5) * CELL; cy = ay + (np.floor((PY - ay) / CELL) + 0.5) * CELL
    own, oa, oc, tid, td = bilinear(src, o.id, cx, cy)
    C = np.zeros(PX.shape, int); D = np.full(PX.shape, np.inf); P = np.zeros(PX.shape, int)
    m5 = own >= 0.5
    C[m5] = oc[m5]; D[m5] = oa[m5]; P[m5] = 5
    rest = ~m5 & (tid > 0) & (td > 0)
    if rest.any():
        found, colr, near = own_cell(src, o.id, cx[rest], cy[rest], CELL)
        if 'Q' in EXTRA:
            far_own = np.full(near.shape, -1.0)
            for (si, sa, _) in samples(src, cx[rest], cy[rest], CELL):
                far_own = np.where((si == o.id) & (sa > 0), np.maximum(far_own, sa), far_own)
            ok7 = found & (td[rest] < far_own)
        else:
            ok7 = found & (td[rest] < near)
        c_r = np.where(ok7, colr, 0); d_r = np.where(ok7, near, np.inf); p_r = np.where(ok7, 7, 0)
        # ring search (5x5 cell centres, single fetch each)
        cand_ring = np.full(found.shape, 9.0); cand_d = np.full(found.shape, 1e9); cand_c = np.zeros(found.shape, int)
        for n in range(25):
            ox, oy = n % 5 - 2, n // 5 - 2; ring = max(abs(ox), abs(oy))
            if ring == 0:
                continue
            i, a, c = fetch(src, cx[rest] + ox * CELL, cy[rest] + oy * CELL)
            ok = (i == o.id) & (a > 0) & ((ring < cand_ring) | (a < cand_d))
            ok &= ring <= cand_ring
            cand_ring = np.where(ok, ring, cand_ring); cand_d = np.where(ok, a, cand_d); cand_c = np.where(ok, c, cand_c)
        i, a, c = fetch(src, PX[rest], PY[rest])
        selfp = (cand_d > 1e8) & (i == o.id) & (a > 0)
        cand_d = np.where(selfp, a, cand_d); cand_c = np.where(selfp, c, cand_c)
        ok8 = ~ok7 & (cand_d < 1e8) & ((td[rest] < cand_d) | ('Q' in EXTRA))
        c_r = np.where(ok8, cand_c, c_r); d_r = np.where(ok8, cand_d, d_r); p_r = np.where(ok8, 8, p_r)
        if 'F' in EXTRA:
            d_r = np.where((p_r == 7) | (p_r == 8), 1e6 + d_r * 0, d_r)
        if 'P' in EXTRA:
            pi, pa, _ = fetch(src, PX[rest], PY[rest])
            patch = (p_r == 7) | (p_r == 8)
            d_r = np.where(patch, np.where((pi == o.id) & (pa > 0), pa, np.maximum(d_r, pa * 1.002 + 0.01)), d_r)
        C[rest] = c_r; D[rest] = d_r; P[rest] = p_r
    return xs, ys, C, D, P


def resolve_baked(o, src):
    xs, ys, PX, PY = rect_pixels(o)
    gx, gy, cell = o.grid; s = o.scale; ra = o.cur[:2]
    cix = np.floor(((PX - ra[0]) * s) / cell); ciy = np.floor(((PY - ra[1]) * s) / cell)
    mx = (cix - o.mrect[0]).astype(int); my = (ciy - o.mrect[1]).astype(int)
    inr = (mx >= 0) & (my >= 0) & (mx < o.mrect[2]) & (my < o.mrect[3])
    mD = np.full(PX.shape, -1.0); mC = np.zeros(PX.shape, int)
    mD[inr] = o.maskD[my[inr], mx[inr]]; mC[inr] = o.maskC[my[inr], mx[inr]]
    ccx = ra[0] + (cix + 0.5) * cell / max(s, 1e-4); ccy = ra[1] + (ciy + 0.5) * cell / max(s, 1e-4); cellp = cell / max(s, 1e-4)
    C = np.zeros(PX.shape, int); D = np.full(PX.shape, np.inf); P = np.zeros(PX.shape, int)
    f = mD > 0
    if f.any():
        i, a, _ = fetch(src, ccx[f], ccy[f])
        found, _, near = own_cell(src, o.id, ccx[f], ccy[f], cellp)
        stored = np.maximum(o.depth + (mD[f] - o.bake_depth), 1e-3)
        D[f] = np.where((i == o.id) & (a > 0), a, np.where(found, near, stored)); C[f] = mC[f]; P[f] = 1
    e = (mD == 0) & ('G' in EXTRA)
    if e.any():
        # empty cell, but the pixel itself is ours and the cell shows no background: last-resort fill (behind everything)
        pid, pa, pc = fetch(src, PX[e], PY[e])
        S16 = samples(src, ccx[e], ccy[e], cellp)
        nobg = np.ones(pid.shape, bool)
        for (si, sa, _) in S16:
            nobg &= sa > 0
        ex, ey = mx[e], my[e]
        cc = pc.copy(); best = np.full(pid.shape, np.inf)
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1)):
            nx, ny = ex + dx, ey + dy
            ok = (nx >= 0) & (ny >= 0) & (nx < o.mrect[2]) & (ny < o.mrect[3])
            v = np.zeros(nx.shape); vc = np.zeros(nx.shape, int)
            v[ok] = o.maskD[ny[ok], nx[ok]]; vc[ok] = o.maskC[ny[ok], nx[ok]]
            b = (v > 0) & (v < best); best = np.where(b, v, best); cc = np.where(b, vc, cc)
        g = (pid == o.id) & (pa > 0) & nobg
        ce = np.zeros(pid.shape, int); de = np.full(pid.shape, np.inf); pe = np.zeros(pid.shape, int)
        ce[g] = cc[g]; de[g] = 2e6; pe[g] = 6
        C[e] = ce; D[e] = de; P[e] = pe
    u = mD < 0
    if u.any():
        found, colr, near = own_cell(src, o.id, ccx[u], ccy[u], cellp)
        cu = np.where(found, colr, 0); du = np.where(found, near, np.inf); pu = np.where(found, 2, 0)
        pid, pa, pc = fetch(src, PX[u], PY[u])
        ux, uy = mx[u], my[u]
        cand_d = np.full(ux.shape, np.inf); cand_c = np.zeros(ux.shape, int); first_d = np.full(ux.shape, np.inf); first_c = np.zeros(ux.shape, int)
        order = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1)]
        for k, (dx, dy) in enumerate(order):
            nx, ny = ux + dx, uy + dy
            ok = (nx >= 0) & (ny >= 0) & (nx < o.mrect[2]) & (ny < o.mrect[3])
            v = np.zeros(nx.shape); vc = np.zeros(nx.shape, int)
            v[ok] = o.maskD[ny[ok], nx[ok]]; vc[ok] = o.maskC[ny[ok], nx[ok]]
            nd = np.maximum(o.depth + (v - o.bake_depth), 1e-3)
            better = (v > 0) & (nd < cand_d); cand_d = np.where(better, nd, cand_d); cand_c = np.where(better, vc, cand_c)
            if k < 4:   # original: first of 4 neighbours that is filled AND farther than the pixel's object
                fb = (v > 0) & ~np.isfinite(first_d) & (pa < nd) & (pid > 0) & (pa > 0)
                first_d = np.where(fb, nd, first_d); first_c = np.where(fb, vc, first_c)
        rest = ~found & (pa > 0)
        if SEAM_FIX[0]:
            mine = rest & (pid == o.id)
            cu = np.where(mine, np.where(np.isfinite(cand_d), cand_c, pc), cu); du = np.where(mine, pa, du); pu = np.where(mine, 2, pu)
            hit = rest & ~mine & (pid > 0) & np.isfinite(cand_d) & ((pa < cand_d) | ('Q' in EXTRA))
            cu = np.where(hit, cand_c, cu); du = np.where(hit, cand_d, du); pu = np.where(hit, 3, pu)
        else:
            hit = rest & np.isfinite(first_d)
            cu = np.where(hit, first_c, cu); du = np.where(hit, first_d, du); pu = np.where(hit, 3, pu)
        if 'F' in EXTRA:
            ab = (~found) & ((pu == 2) | (pu == 3))
            du = np.where(ab, 1e6, du)
        if 'P' in EXTRA:
            patch = (pu == 2) | (pu == 3)
            du = np.where(patch, np.where((pid == o.id) & (pa > 0), pa, np.maximum(du, np.where(pa > 0, pa * 1.002 + 0.01, du))), du)
        C[u] = cu; D[u] = du; P[u] = pu
    return xs, ys, C, D, P


def compose(cam, objs, src):
    img = np.zeros((H, W), int); best = np.full((H, W), np.inf); who = np.zeros((H, W), int); path = np.zeros((H, W), int)
    for o in objs:
        if not o.visible:
            continue
        xs, ys, C, D, P = resolve_fresh(o, src) if o.screen else resolve_baked(o, src)
        sub = np.ix_(ys, xs)
        m = (C > 0) & (D <= best[sub])
        img[sub] = np.where(m, C, img[sub]); best[sub] = np.where(m, D, best[sub])
        who[sub] = np.where(m, o.id, who[sub]); path[sub] = np.where(m, P, path[sub])
    return img, who, path


def reference(cam, objs):
    """each object pixelised alone on a fresh grid (screen grid for the floor); composite by cell depth."""
    img = np.zeros((H, W), int); best = np.full((H, W), np.inf)
    for o in objs:
        if not o.visible:
            continue
        x0, y0 = int(math.floor(o.rect[0])), int(math.floor(o.rect[1])); x1, y1 = int(math.ceil(o.rect[2])), int(math.ceil(o.rect[3]))
        sx, sy = XX[y0:y1, x0:x1] + 0.5, YY[y0:y1, x0:x1] + 0.5
        ax, ay = o.anchor
        cx = ax + (np.floor((sx - ax) / CELL) + 0.5) * CELL; cy = ay + (np.floor((sy - ay) / CELL) + 0.5) * CELL
        d, _ = o.s.hit(cam, cx, cy)
        sub = (slice(y0, y1), slice(x0, x1))
        m = (d > 0) & (d < best[sub])
        if m.any():
            dark = np.zeros(m.sum()); hits = np.zeros(m.sum())
            for j in range(16):
                dd, pw = o.s.hit(cam, cx[m] + OFFS16[0][j] * CELL, cy[m] + OFFS16[1][j] * CELL)
                h = dd > 0; dark += np.where(h, o.s.checker(pw), 0); hits += h
            c = np.zeros(d.shape, int); c[m] = o.id * 2 + (dark * 2 > hits)
            img[sub] = np.where(m, c, img[sub]); best[sub] = np.where(m, d, best[sub])
    return img


def build_scene():
    rnd = np.random.default_rng(3)
    if 'SIMPLE' in EXTRA:
        return [PObj(Floor(1, [(76, 76, 82), (61, 61, 66)]), screen_grid=True),
                PObj(Box(2, [0, 0.55, 0], (2.4, 2.4, 2.4), euler(18, 30, 12), [(237, 227, 204), (26, 26, 28)], k=1.5))]
    objs = [PObj(Floor(1, [(76, 76, 82), (61, 61, 66)]), screen_grid=True),
            PObj(Sphere(2, [0, 2.2, 0], 2.2, euler(0, 20, 0), [(245, 245, 240), (10, 10, 13)]))]
    ch = 7 * 2 / 3
    for i, (p, yaw) in enumerate([((-4.5, ch / 2, 2.25), 15), ((4.125, ch / 2, 3.75), 40), ((5.25, ch / 2, -3), -10), ((-3.75, ch / 2, -4.5), 30)]):
        objs.append(PObj(Box(3 + i, p, (1, ch, 1), euler(0, yaw, 0), [(237, 227, 204), (26, 26, 28)])))
    pals = [[(245, 107, 219), (128, 51, 194)], [(76, 209, 56), (20, 107, 26)], [(255, 214, 46), (235, 41, 26)], [(115, 217, 255), (30, 110, 230)], [(255, 150, 30), (40, 25, 15)]]
    spots = [(-3.6, 1.3), (3.2, 2.9), (4.4, -2.0), (-2.6, -3.6), (1.6, -2.6), (-1.6, 2.6), (2.6, 0.8), (-3.0, -1.2)]
    if 'PILE' in EXTRA:
        spots = [(rnd.uniform(-4.5, 4.5), rnd.uniform(-4.5, 4.5)) for _ in range(30)]
        spots = [(x, z) for x, z in spots if math.hypot(x, z) > 2.6]
    for k, (x, z) in enumerate(spots):
        r = rnd.uniform(0.4, 0.65); L = r * 2 * rnd.uniform(1.75, 2.25)
        tilt = 90 if 'PILE' not in EXTRA else rnd.uniform(55, 90)
        y = r - 0.03 if 'PILE' not in EXTRA else r - 0.03 + rnd.uniform(0, 1.2)
        rot = euler(rnd.uniform(-20, 20) if 'PILE' in EXTRA else 0, rnd.uniform(0, 360), tilt)
        objs.append(PObj(Capsule(7 + k, [x, y, z], r, L, rot, pals[k % len(pals)])))
    return objs


def palette_of(objs):
    P = {}
    for o in objs:
        P[o.id * 2] = o.s.pal[0]; P[o.id * 2 + 1] = o.s.pal[1]
    return P


def to_rgb(code, P):
    rgb = np.zeros((H, W, 3), np.uint8)
    for k, c in P.items():
        rgb[code == k] = c
    return rgb[::-1]


def holes(img, src):
    """black pixels with >= 3 of 4 painted neighbours, where the source has an object."""
    blk = img == 0
    nb = sum(np.roll(~blk, (dy, dx), (0, 1)) for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)))
    return blk & (nb >= 3)


def run(out, t0=1.6, frames=120, fps=60, verbose=True, save=True):
    objs = build_scene(); P = palette_of(objs)
    if save:
        os.makedirs(out, exist_ok=True)
    stats = []
    for f in range(frames):
        cam = camera_at(t0 + f / fps)
        for o in objs:
            o.update(cam)
        src = render_source(cam, objs)
        for o in objs:
            if o.screen or not o.visible:
                continue
            if not o.pending and hasattr(o, 'maskD'):
                live(o, src)
            if o.pending:
                capture(o, src); o.pending = False
        img, who, path = compose(cam, objs, src)
        ref = reference(cam, objs)
        h = holes(img, src); hr = holes(ref, src)
        lone = lambda im: int(((sum((np.roll(im, (dy, dx), (0, 1)) != im) for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1))) >= 3) & (im > 0)).sum())
        stats.append((int(h.sum()), int(hr.sum()), int((img != ref).sum()), lone(img), lone(ref)))
        if save:
            np.savez_compressed(f'{out}/{f:04d}.npz', img=img.astype(np.int16), ref=ref.astype(np.int16), who=who.astype(np.int8),
                                path=path.astype(np.int8), sid=src[0].astype(np.int8), hole=h)
        if verbose and f % 20 == 0:
            print('frame', f, stats[-1], flush=True)
    s = np.array(stats)
    print(f'[{"+".join(sorted(EXTRA)) or "current"}] black holes/frame {s[:,0].mean():.1f} (ref {s[:,1].mean():.1f}) | lone px/frame {s[:,3].mean():.1f} (ref {s[:,4].mean():.1f}) | differs from ref {s[:,2].mean():.0f}')
    return objs, P, s


if __name__ == '__main__':
    for tok in (sys.argv[3].split('+') if len(sys.argv) > 3 and sys.argv[3] else []):
        EXTRA.add(tok)
    run(sys.argv[1], frames=int(sys.argv[2]) if len(sys.argv) > 2 else 120, verbose=False)
