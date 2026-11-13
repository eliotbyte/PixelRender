"""
Two-sphere test: black background, far sphere + near sphere that moves sideways fast and occludes it.
Both objects go through the full PixelRender pipeline (port of PixelRenderObject + PixelRenderResolve,
current code incl. the 2026-11-09 fix). Reference: every object pixelised alone on its own fresh grid
(cell filled iff its centre is inside), composited by cell depth.
"""
import math, os, subprocess, sys
import numpy as np
from PIL import Image, ImageDraw
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixelsim import W, H, FOCAL, NEAR, OFFS16, Cam, XX, YY

CELL = 7.0
FPS = 60
SECONDS = 4.0

CAM = Cam(np.array([0.0, 1.2, -10.0]), np.array([0.0, 0.0, 0.0]))
FAR_C, FAR_R = np.array([0.0, 0.0, 0.0]), 2.0
NEAR_R = 1.3


def near_centre(t):
    # 5 m in front of the camera, swings sideways: +-4.5 m, period 2 s (peak ~14 m/s)
    return np.array([4.5 * math.sin(2 * math.pi * t / 2.0), 0.6, -5.0])


COLOURS = {1: (235, 235, 228), 2: (255, 140, 30)}


# ---------------------------------------------------------------- rendering helpers
def sphere_hit(cam, centre, radius, sx, sy):
    """depth (view z) of the sphere at screen points (float px), 0 = miss."""
    px = (sx - W * 0.5) / FOCAL
    py = (sy - H * 0.5) / FOCAL
    c = cam.to_view(centre)
    dd = px * px + py * py + 1.0
    b = px * c[0] + py * c[1] + c[2]
    disc = b * b - dd * (c @ c - radius * radius)
    return np.where(disc >= 0, (b - np.sqrt(np.maximum(disc, 0))) / dd, 0.0)


def render_source(objs):
    ids = np.zeros((H, W), int)
    dep = np.zeros((H, W))
    for o in objs:
        d = sphere_hit(CAM, o.c, o.R, XX + 0.5, YY + 0.5)
        m = (d > 0) & ((dep <= 0) | (d < dep))
        dep = np.where(m, d, dep)
        ids = np.where(m, o.id, ids)
    return ids, dep


def fetch(src, x, y):
    ids, dep = src
    xi = np.clip(np.floor(x).astype(int), 0, W - 1)
    yi = np.clip(np.floor(y).astype(int), 0, H - 1)
    return ids[yi, xi], dep[yi, xi]


# ---------------------------------------------------------------- shader ports (generic own id)
def capture_cell(src, own_id, cx, cy, cell, limit_fallback):
    off = (cx < 0) | (cy < 0) | (cx >= W) | (cy >= H)
    x0, y0 = np.floor(cx - 0.5), np.floor(cy - 0.5)
    fx, fy = cx - 0.5 - x0, cy - 0.5 - y0
    own = np.zeros_like(cx); bo = np.full_like(cx, -1.0); oa = np.zeros_like(cx)
    bt = np.full_like(cx, -1.0); tid = np.zeros_like(cx); td = np.zeros_like(cx)
    for k in range(4):
        ox, oy = k & 1, k >> 1
        w = (fx if ox else 1 - fx) * (fy if oy else 1 - fy)
        i, a = fetch(src, x0 + ox + 0.5, y0 + oy + 0.5)
        m = (i == own_id) & (a > 0)
        own += np.where(m, w, 0)
        u = m & (w > bo); bo = np.where(u, w, bo); oa = np.where(u, a, oa)
        u = ~m & (w > bt); bt = np.where(u, w, bt); tid = np.where(u, np.where(a > 0, i, 0), tid); td = np.where(u, a, td)
    nearest = np.full_like(cx, 1e9)
    for j in range(16):
        i, a = fetch(src, cx + OFFS16[0][j] * cell, cy + OFFS16[1][j] * cell)
        nearest = np.where((i == own_id) & (a > 0), np.minimum(nearest, a), nearest)
    limit = np.where(nearest < 1e8, nearest, limit_fallback)
    res = np.where(own >= 0.5, np.maximum(oa, 1e-3), np.where((tid > 0) & (td < limit * 0.99), -1.0, 0.0))
    return np.where(off, -1.0, res)


def live_cell(src, own_id, prev, bcx, bcy, grid, cur, limit_fallback, hyst):
    scale = max(cur[2], 1e-4)
    cx = cur[0] + (bcx - grid[0]) / scale
    cy = cur[1] + (bcy - grid[1]) / scale
    cellp = grid[2] / scale
    off = (cx < 0) | (cy < 0) | (cx >= W) | (cy >= H)
    S = [fetch(src, cx + OFFS16[0][j] * cellp, cy + OFFS16[1][j] * cellp) for j in range(16)]
    nearest = np.full_like(cx, 1e9)
    for i, a in S:
        nearest = np.where((i == own_id) & (a > 0), np.minimum(nearest, a), nearest)
    limit = np.where(nearest < 1e8, nearest, limit_fallback)
    own_n = np.zeros_like(cx); free_n = np.zeros_like(cx)
    for i, a in S:
        o = (i == own_id) & (a > 0)
        other = ~o & (a > 0)
        own_n += o
        free_n += ~o & (~other | (a >= limit * 0.99))
    vis = own_n + free_n
    cov = own_n / np.maximum(vis, 1)
    was = prev > 0
    filled = np.where(prev < 0, cov >= 0.5, np.where(was, cov > 0.5 - hyst, cov > 0.5 + hyst))
    res = np.where(filled, np.where(was, prev, np.maximum(nearest + cur[3], 1e-3)), 0.0)
    return np.where(off | (vis < 8), prev, res)


def own_cell(src, own_id, cx, cy, cellp):
    """OwnCellColor: found + nearest own depth."""
    nearest = np.full(cx.shape, 1e9)
    for j in range(16):
        i, a = fetch(src, cx + OFFS16[0][j] * cellp, cy + OFFS16[1][j] * cellp)
        nearest = np.where((i == own_id) & (a > 0), np.minimum(nearest, a), nearest)
    return nearest < 1e8, nearest


# ---------------------------------------------------------------- object
class PixelSphere:
    def __init__(self, oid, centre, radius, hyst=0.2, max_err=1.0, bake_angle=6.0, bake_scale=0.15):
        self.id, self.c, self.R = oid, np.asarray(centre, float), radius
        self.cell = CELL
        self.hyst, self.max_err, self.bake_angle, self.bake_scale = hyst, max_err, bake_angle, bake_scale
        self.baked = False
        self.mask = None
        self.pending = False
        self.bakes = 0
        self.extent = math.sqrt(3) * radius   # renderer.bounds.extents.magnitude (unit sphere mesh, unrotated)

    def corners(self):
        return [self.c + np.array([sx, sy, sz]) * self.R for sz in (-1, 1) for sy in (-1, 1) for sx in (-1, 1)]

    def update_grid(self, cam):
        a = cam.w2s(self.c)
        self.anchor = a[:2]
        cell = self.cell
        sr = self.extent * FOCAL / max(a[2] - self.extent, NEAR) + cell * 2
        self.rect = (max(0, a[0] - sr), max(0, a[1] - sr), min(W, a[0] + sr), min(H, a[1] + sr))
        self.visible = self.rect[2] > self.rect[0] and self.rect[3] > self.rect[1]
        if not self.visible:
            return
        depth = (self.c - cam.pos) @ cam.f
        self.depth = depth
        self.update_bake(cam, depth)
        self.scale = depth / max(self.bake_depth, 1e-4)
        self.cur = (round(self.anchor[0]), round(self.anchor[1]), self.scale, self.bake_depth - depth)
        self.near_limit = depth - self.extent      # fixed: nearest possible depth

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

    def update_bake(self, cam, depth):
        to_cam = cam.pos - self.c
        world_cell = self.cell * max(depth, NEAR) / FOCAL
        d = to_cam / np.linalg.norm(to_cam)
        rebake = not self.baked
        if self.baked:
            ang = math.degrees(math.acos(max(-1, min(1, d @ self.bake_dir))))
            rebake = (self.max_err > 0 and self.sprite_error(cam, depth) > self.max_err * self.cell) \
                or ang > self.bake_angle \
                or abs(math.log(world_cell / self.bake_world_cell)) > math.log(1 + self.bake_scale)
        if rebake:
            self.grid = (self.anchor[0], self.anchor[1], self.cell)
            self.bake_dir, self.bake_world_cell, self.bake_depth = d, world_cell, depth
            self.bake_anchor = self.anchor.copy()
            self.bake_corners = [cam.w2s(c)[:2] for c in self.corners()]
            self.baked = True
            self.bakes += 1
            x0, y0, x1, y1 = self.rect
            fx = math.floor((x0 - self.anchor[0]) / self.cell) - 2
            fy = math.floor((y0 - self.anchor[1]) / self.cell) - 2
            tx = math.ceil((x1 - self.anchor[0]) / self.cell) + 2
            ty = math.ceil((y1 - self.anchor[1]) / self.cell) + 2
            self.mrect = (fx, fy, int(min(max(tx - fx, 1), 1024)), int(min(max(ty - fy, 1), 1024)))
            self.pending = True

    def centres(self):
        fx, fy, mw, mh = self.mrect
        jj, ii = np.mgrid[0:mh, 0:mw]
        return self.grid[0] + (fx + ii + 0.5) * self.grid[2], self.grid[1] + (fy + jj + 0.5) * self.grid[2]

    def after_source(self, src):
        if not self.visible:
            return
        if self.mask is not None and not self.pending:
            cx, cy = self.centres()
            self.mask = live_cell(src, self.id, self.mask, cx, cy, self.grid, self.cur, self.near_limit, self.hyst)
        if self.pending:
            cx, cy = self.centres()
            self.mask = capture_cell(src, self.id, cx, cy, self.grid[2], self.bake_depth - self.extent)
            self.pending = False

    def resolve(self, src):
        """-> (drawn bool HxW, depth HxW)"""
        drawn_img = np.zeros((H, W), bool)
        depth_img = np.full((H, W), np.inf)
        if not self.visible:
            return drawn_img, depth_img
        x0, y0, x1, y1 = self.rect
        xs = np.arange(int(math.floor(x0)), int(math.ceil(x1)))
        ys = np.arange(int(math.floor(y0)), int(math.ceil(y1)))
        xs = xs[(xs + 0.5 >= x0) & (xs + 0.5 < x1)]
        ys = ys[(ys + 0.5 >= y0) & (ys + 0.5 < y1)]
        PX, PY = np.meshgrid(xs + 0.5, ys + 0.5)
        gx, gy, cell = self.grid
        s = self.scale
        ra = self.cur[:2]
        bx = gx + (PX - ra[0]) * s
        by = gy + (PY - ra[1]) * s
        cix = np.floor((bx - gx) / cell); ciy = np.floor((by - gy) / cell)
        mx = (cix - self.mrect[0]).astype(int); my = (ciy - self.mrect[1]).astype(int)
        inr = (mx >= 0) & (my >= 0) & (mx < self.mrect[2]) & (my < self.mrect[3])
        m = np.full(PX.shape, -1.0)
        m[inr] = self.mask[my[inr], mx[inr]]
        ccx = ra[0] + ((cix + 0.5) * cell) / max(s, 1e-4)      # cell centre on screen
        ccy = ra[1] + ((ciy + 0.5) * cell) / max(s, 1e-4)
        cellp = cell / max(s, 1e-4)
        drawn = np.zeros(PX.shape, bool)
        depth = np.full(PX.shape, np.inf)

        # filled cells
        f = m > 0
        if f.any():
            i, a = fetch(src, ccx[f], ccy[f])
            ok = (i == self.id) & (a > 0)
            found, near = own_cell(src, self.id, ccx[f], ccy[f], cellp)
            stored = np.maximum(self.depth + (m[f] - self.bake_depth), 1e-3)
            depth[f] = np.where(ok, a, np.where(found, near, stored))
            drawn[f] = True
        # unknown cells
        u = m < 0
        if u.any():
            found, near = own_cell(src, self.id, ccx[u], ccy[u], cellp)
            d_u = np.where(found, near, np.inf)
            dr_u = found.copy()
            pid, pa = fetch(src, PX[u], PY[u])
            edge = ~found & (pid > 0) & (pa > 0)
            ux, uy = mx[u], my[u]
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = ux + dx, uy + dy
                ok = (nx >= 0) & (ny >= 0) & (nx < self.mrect[2]) & (ny < self.mrect[3])
                v = np.zeros(nx.shape); v[ok] = self.mask[ny[ok], nx[ok]]
                nd = np.maximum(self.depth + (v - self.bake_depth), 1e-3)
                hit = edge & ~dr_u & (v > 0) & (pa < nd)        # fixed: only if that object is closer
                dr_u |= hit
                d_u = np.where(hit, nd, d_u)
            drawn[u] = dr_u
            depth[u] = d_u
        sub = np.ix_(ys, xs)
        drawn_img[sub] = drawn
        depth_img[sub] = np.where(drawn, depth, np.inf)
        return drawn_img, depth_img


# ---------------------------------------------------------------- reference
def reference(objs):
    img = np.zeros((H, W), int)
    best = np.full((H, W), np.inf)
    for o in objs:
        a = CAM.w2s(o.c)
        cx = a[0] + (np.floor((XX + 0.5 - a[0]) / CELL) + 0.5) * CELL
        cy = a[1] + (np.floor((YY + 0.5 - a[1]) / CELL) + 0.5) * CELL
        d = sphere_hit(CAM, o.c, o.R, cx, cy)
        m = (d > 0) & (d < best)
        best = np.where(m, d, best)
        img = np.where(m, o.id, img)
    return img


def to_rgb(idimg):
    rgb = np.zeros((H, W, 3), np.uint8)
    for k, c in COLOURS.items():
        rgb[idimg == k] = c
    return rgb[::-1]          # screen y-up -> image rows


def main(out_dir):
    far = PixelSphere(1, FAR_C, FAR_R)
    near = PixelSphere(2, near_centre(0), NEAR_R)
    objs = [far, near]
    frames = []
    stats = []
    n = int(SECONDS * FPS)
    for f in range(n):
        t = f / FPS
        near.c = near_centre(t)
        for o in objs:
            o.update_grid(CAM)
        src = render_source(objs)
        for o in objs:
            o.after_source(src)
        ours = np.zeros((H, W), int)
        best = np.full((H, W), np.inf)
        for o in objs:
            dr, dp = o.resolve(src)
            m = dr & (dp <= best)
            best = np.where(m, dp, best)
            ours = np.where(m, o.id, ours)
        ref = reference(objs)
        true = src[0]
        # far-sphere pixels drawn by us outside the far sphere's real (visible) region
        stats.append(((ours != ref).sum(), ((ours == 1) & (ref != 1)).sum(), ((ours == 2) & (ref != 2)).sum()))
        frames.append((to_rgb(ours), to_rgb(ref)))
    s = np.array(stats)
    print(f'frames {n}, bakes far {far.bakes} near {near.bakes}')
    print(f'pixels differing from reference: mean {s[:,0].mean():.0f}, max {s[:,0].max()}')
    print(f'  far-sphere colour where reference has none/other: mean {s[:,1].mean():.0f}, max {s[:,1].max()}')
    print(f'  near-sphere colour where reference has none/other: mean {s[:,2].mean():.0f}, max {s[:,2].max()}')
    return frames, s


def label(img, text):
    from PIL import ImageFont
    im = Image.fromarray(img)
    font = ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf', 20)
    ImageDraw.Draw(im).text((14, 12), text, fill=(190, 190, 190), font=font)
    return np.array(im)


if __name__ == '__main__':
    out = sys.argv[1] if len(sys.argv) > 1 else '.'
    frames, s = main(out)
    x0, x1 = 160, 1120                                    # 960 px wide central crop
    fdir = os.path.join(out, 'frames'); os.makedirs(fdir, exist_ok=True)
    for i, (a, b) in enumerate(frames):
        sep = np.full((H, 4, 3), (255, 0, 255), np.uint8)
        img = np.concatenate([label(a[:, x0:x1], 'PixelRender (наш метод)'), sep,
                              label(b[:, x0:x1], 'эталон: свежая сетка, каждый объект отдельно')], 1)
        Image.fromarray(img).save(os.path.join(fdir, f'{i:04d}.png'))
    np.save(os.path.join(out, 'stats.npy'), s)
