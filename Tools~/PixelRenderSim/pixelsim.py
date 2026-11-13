"""
Off-Unity debug scene for the PixelRender pipeline: ONE sphere, ONE colour, black background.

Ports (line-for-line where it matters):
  PixelRenderCameraMotion.Apply
  PixelRenderObject.UpdateGrid / UpdateBake / SpriteError / PrepareMask
  PixelRenderResolve: CaptureCell, LiveCell, Frag (BakedMask branch), OwnCellColor (found-only)
and compares our silhouette with
  * TRUE  - exact sphere silhouette per pixel (analytic ray-sphere)
  * GT    - fresh grid at the current anchor, cell filled iff its centre is inside the sphere
"""
import math
import numpy as np

W, H = 1280, 720
FOV = 50.0
NEAR = 0.3
FOCAL = 0.5 * H / math.tan(math.radians(FOV) * 0.5)

BALL_C = np.array([0.0, 2.2, 0.0])
BALL_R = 2.2                       # Unity sphere r=0.5 * scale 4.4
BALL_YAW = 20.0
BALL_SCALE = 4.4


# ---------------------------------------------------------------- Unity helpers
def euler(px, py, pz):
    """Unity Quaternion.Euler as a 3x3 matrix (Z, then X, then Y)."""
    x, y, z = map(math.radians, (px, py, pz))
    Rx = np.array([[1, 0, 0], [0, math.cos(x), -math.sin(x)], [0, math.sin(x), math.cos(x)]])
    Ry = np.array([[math.cos(y), 0, math.sin(y)], [0, 1, 0], [-math.sin(y), 0, math.cos(y)]])
    Rz = np.array([[math.cos(z), -math.sin(z), 0], [math.sin(z), math.cos(z), 0], [0, 0, 1]])
    return Ry @ Rx @ Rz


class Cam:
    def __init__(self, pos, target):
        self.pos = np.asarray(pos, float)
        f = target - self.pos
        f /= np.linalg.norm(f)
        r = np.cross([0, 1, 0], f)          # Unity: right = Cross(up, forward)
        r /= np.linalg.norm(r)
        u = np.cross(f, r)
        self.f, self.r, self.u = f, r, u

    def to_view(self, p):
        d = np.asarray(p, float) - self.pos
        return np.array([d @ self.r, d @ self.u, d @ self.f])

    def w2s(self, p):
        """Camera.WorldToScreenPoint: pixels from bottom-left, z = view depth."""
        v = self.to_view(p)
        return np.array([W * 0.5 + FOCAL * v[0] / v[2], H * 0.5 + FOCAL * v[1] / v[2], v[2]])


def camera_at(t, orbit=3.0, start_yaw=25.0, dist=9.75, amp=1.85, period=5.2, bob_amp=0.3, bob_period=7.0, pitch=35.0):
    yaw = start_yaw + orbit * t
    d = dist + amp * math.sin(t * 2 * math.pi / period)
    bob = bob_amp * math.sin(t * 2 * math.pi / bob_period)
    pos = BALL_C + euler(pitch, yaw, 0) @ np.array([0, 0, -1.0]) * d + np.array([0, bob, 0])
    return Cam(pos, BALL_C)


# ---------------------------------------------------------------- "source" render
YY, XX = np.mgrid[0:H, 0:W]          # row index = screen y from the bottom (LOAD coord)


def render_source(cam, centre=BALL_C, radius=BALL_R):
    """Per pixel centre: is the sphere hit, and its view depth (a > 0) -> like the source texture."""
    px = (XX + 0.5 - W * 0.5) / FOCAL
    py = (YY + 0.5 - H * 0.5) / FOCAL
    # ray dir in view space (px, py, 1); sphere centre in view space
    c = cam.to_view(centre)
    dd = px * px + py * py + 1.0
    b = px * c[0] + py * c[1] + c[2]
    cc = c @ c - radius * radius
    disc = b * b - dd * cc
    hit = disc >= 0
    t = np.where(hit, (b - np.sqrt(np.maximum(disc, 0))) / dd, 0.0)
    depth = np.where(hit, t, 0.0)    # view z of the hit (ray z component is 1)
    return depth                      # a: >0 own, 0 = background


SCENE = {'floor': False, 'occluders': []}
NEAR_DEPTH = [0.0]
FIX = {'near_limit': False, 'path3_depth': False}   # occluders: list of callables t->(centre, radius)
SCENE_T = [0.0]

def ray_dirs(cam):
    px = (XX + 0.5 - W * 0.5) / FOCAL
    py = (YY + 0.5 - H * 0.5) / FOCAL
    return px, py

def sphere_depth(cam, centre, radius):
    px, py = ray_dirs(cam)
    c = cam.to_view(centre)
    dd = px * px + py * py + 1.0
    b = px * c[0] + py * c[1] + c[2]
    disc = b * b - dd * (c @ c - radius * radius)
    hit = disc >= 0
    return np.where(hit, (b - np.sqrt(np.maximum(disc, 0))) / dd, 0.0)

def render_scene(cam):
    """returns (id, depth); id 0 = nothing."""
    depth = sphere_depth(cam, BALL_C, BALL_R)
    ids = np.where(depth > 0, 1, 0)
    def merge(d, i):
        nonlocal depth, ids
        m = (d > 0) & ((depth <= 0) | (d < depth))
        depth = np.where(m, d, depth); ids = np.where(m, i, ids)
    if SCENE['floor']:
        px, py = ray_dirs(cam)
        dy = px * cam.r[1] + py * cam.u[1] + cam.f[1]
        t = np.where(dy < 0, -cam.pos[1] / np.where(dy < 0, dy, -1), 0.0)
        wx = cam.pos[0] + t * (px * cam.r[0] + py * cam.u[0] + cam.f[0])
        wz = cam.pos[2] + t * (px * cam.r[2] + py * cam.u[2] + cam.f[2])
        t = np.where((np.abs(wx) < 30) & (np.abs(wz) < 30), t, 0.0)
        merge(t, 2)
    for k, occ in enumerate(SCENE['occluders']):
        c, r = occ(SCENE_T[0])
        merge(sphere_depth(cam, c, r), 3 + k)
    return ids, depth

def inside_true(cam, sx, sy, centre=BALL_C, radius=BALL_R):
    """Analytic: is the screen point (float pixels) inside the sphere's silhouette."""
    px = (sx - W * 0.5) / FOCAL
    py = (sy - H * 0.5) / FOCAL
    c = cam.to_view(centre)
    dd = px * px + py * py + 1.0
    b = px * c[0] + py * c[1] + c[2]
    return b * b - dd * (c @ c - radius * radius) >= 0


def fetch(src, x, y):
    """LOAD_TEXTURE2D(int2(floor(p))) with clamp -> (id, depth)."""
    ids, dep = src
    xi = np.clip(np.floor(x).astype(int), 0, W - 1)
    yi = np.clip(np.floor(y).astype(int), 0, H - 1)
    return ids[yi, xi], dep[yi, xi]


OWN = 1
OFFS16 = np.array([((i & 3) + 0.5) / 4 - 0.5 for i in range(16)]), np.array([((i >> 2) + 0.5) / 4 - 0.5 for i in range(16)])


def capture_cell(src, cx, cy, cell, far_depth):
    """CaptureCell port. a: >0 filled(depth), 0 empty, -1 unknown."""
    off = (cx < 0) | (cy < 0) | (cx >= W) | (cy >= H)
    corner_x, corner_y = cx - 0.5, cy - 0.5
    x0, y0 = np.floor(corner_x), np.floor(corner_y)
    fx, fy = corner_x - x0, corner_y - y0
    own = np.zeros_like(cx); best_own_w = np.full_like(cx, -1.0); own_a = np.zeros_like(cx)
    best_oth_w = np.full_like(cx, -1.0); oth_id = np.zeros_like(cx); oth_d = np.zeros_like(cx)
    for k in range(4):
        ox, oy = k & 1, k >> 1
        w = (fx if ox else 1 - fx) * (fy if oy else 1 - fy)
        i, a = fetch(src, x0 + ox + 0.5, y0 + oy + 0.5)
        m = (i == OWN) & (a > 0)
        own += np.where(m, w, 0)
        u = m & (w > best_own_w); best_own_w = np.where(u, w, best_own_w); own_a = np.where(u, a, own_a)
        u = ~m & (w > best_oth_w); best_oth_w = np.where(u, w, best_oth_w)
        oth_id = np.where(u, np.where(a > 0, i, 0), oth_id); oth_d = np.where(u, a, oth_d)
    nearest = np.full_like(cx, 1e9)
    for j in range(16):
        i, a = fetch(src, cx + OFFS16[0][j] * cell, cy + OFFS16[1][j] * cell)
        nearest = np.where((i == OWN) & (a > 0), np.minimum(nearest, a), nearest)
    limit = np.where(nearest < 1e8, nearest, far_depth if not FIX['near_limit'] else NEAR_DEPTH[0])
    res = np.where(own >= 0.5, np.maximum(own_a, 1e-3),
                   np.where((oth_id > 0) & (oth_d < limit * 0.99), -1.0, 0.0))
    return np.where(off, -1.0, res)


def live_cell(src, prev, bake_cx, bake_cy, grid, cur, far_depth, hyst, stats=None):
    """LiveCell port."""
    scale = max(cur[2], 1e-4)
    cx = cur[0] + (bake_cx - grid[0]) / scale
    cy = cur[1] + (bake_cy - grid[1]) / scale
    cellp = grid[2] / scale
    off = (cx < 0) | (cy < 0) | (cx >= W) | (cy >= H)
    ids, deps, owns = [], [], []
    nearest = np.full_like(cx, 1e9)
    for j in range(16):
        i, a = fetch(src, cx + OFFS16[0][j] * cellp, cy + OFFS16[1][j] * cellp)
        o = (i == OWN) & (a > 0)
        ids.append(i); deps.append(np.where(a > 0, a, 0.0)); owns.append(o)
        nearest = np.where(o, np.minimum(nearest, a), nearest)
    limit = np.where(nearest < 1e8, nearest, far_depth if not FIX['near_limit'] else NEAR_DEPTH[0])
    own_n = np.zeros_like(cx); free_n = np.zeros_like(cx)
    for j in range(16):
        o = owns[j]
        other_id = np.where(~o & (deps[j] > 0), ids[j], 0)          # own[] = -id for other objects
        free = ~o & ((other_id == 0) | (deps[j] >= limit * 0.99))     # own[j] > -0.5  (background)  || behind
        own_n += o; free_n += free
    visible = own_n + free_n
    coverage = own_n / np.maximum(visible, 1)
    was = prev > 0
    filled = np.where(prev < 0, coverage >= 0.5, np.where(was, coverage > 0.5 - hyst, coverage > 0.5 + hyst))
    res = np.where(filled, np.where(was, prev, np.maximum(nearest + cur[3], 1e-3)), 0.0)
    keep = off | (visible < 8)
    if stats is not None:
        stats['stuck'] = int(((visible < 8) & ~off & was).sum())
    return np.where(keep, prev, res)


def own_cell_found(src, cx, cy, cellp):
    found = np.zeros(cx.shape, bool)
    for j in range(16):
        i, a = fetch(src, cx + OFFS16[0][j] * cellp, cy + OFFS16[1][j] * cellp)
        found |= (i == OWN) & (a > 0)
    return found


# ---------------------------------------------------------------- object (CPU side)
class PixelBall:
    def __init__(self, cell_pixels=7.0, bake=True, live=True, hyst=0.2, max_err=1.0, bake_angle=6.0, bake_scale=0.15,
                 mesh_corners=True):
        self.cell = max(1.0, round(cell_pixels * H / 720.0))
        self.bake_on, self.live, self.hyst = bake, live, hyst
        self.max_err, self.bake_angle, self.bake_scale = max_err, bake_angle, bake_scale
        self.baked = False
        self.mask = None
        self.capture_pending = False
        self.bakes = 0
        self.bake_reasons = []
        R = euler(0, BALL_YAW, 0)
        # mesh bounds of Unity sphere: cube +-0.5 local -> world (8 corners)
        self.corners = [BALL_C + R @ (np.array([sx, sy, sz]) * 0.5 * BALL_SCALE)
                        for sz in (-1, 1) for sy in (-1, 1) for sx in (-1, 1)]
        self.bounds_extent = np.linalg.norm(np.abs(R) @ np.full(3, 0.5 * BALL_SCALE))  # renderer.bounds (AABB of rotated box)

    # ---- UpdateGrid (non-ScreenGrid branch)
    def update_grid(self, cam):
        a = cam.w2s(BALL_C)
        anchor = a[:2]
        cell = self.cell
        radius = self.bounds_extent
        sr = radius * FOCAL / max(a[2] - radius, NEAR) + cell * 2
        rect = (max(0, anchor[0] - sr), max(0, anchor[1] - sr), min(W, anchor[0] + sr), min(H, anchor[1] + sr))
        self.rect = rect
        self.anchor = anchor
        depth = (BALL_C - cam.pos) @ cam.f
        self.depth = depth
        NEAR_DEPTH[0] = depth - BALL_R  # nearest possible point of the object (centre depth - radius)
        if self.bake_on:
            self.update_bake(cam, anchor, cell, depth)
            self.sprite_scale = depth / max(self.bake_depth, 1e-4)
            self.mask_update = (round(anchor[0]), round(anchor[1]), self.sprite_scale, self.bake_depth - depth)
            self.far_depth = depth + self.bounds_extent

    def sprite_error(self, cam, anchor, depth):
        scale = depth / max(self.bake_depth, 1e-4)
        err = 0
        for i, c in enumerate(self.corners):
            act = cam.w2s(c)
            if act[2] <= NEAR:
                return float('inf')
            pred = anchor + (self.bake_corners[i] - self.bake_anchor) / max(scale, 1e-4)
            err = max(err, np.linalg.norm(pred - act[:2]))
        return err

    def update_bake(self, cam, anchor, cell, depth):
        to_cam = cam.pos - BALL_C
        world_cell = cell * max(depth, NEAR) / FOCAL
        direction = to_cam / np.linalg.norm(to_cam)
        up = cam.u
        reason = None
        if self.baked:
            if self.max_err > 0 and self.sprite_error(cam, anchor, depth) > self.max_err * cell:
                reason = 'error'
            elif (ang(direction, self.bake_dir) > self.bake_angle or ang(up, self.bake_up) > self.bake_angle
                  or abs(math.log(world_cell / max(self.bake_world_cell, 1e-6))) > math.log(1 + self.bake_scale)):
                reason = 'threshold'
        if not self.baked or reason:
            self.grid = (anchor[0], anchor[1], cell)
            self.bake_dir, self.bake_up, self.bake_world_cell = direction, up, world_cell
            self.bake_depth = depth
            self.bake_anchor = anchor.copy()
            self.bake_corners = [cam.w2s(c)[:2] for c in self.corners]
            self.baked = True
            self.bakes += 1
            self.bake_reasons.append(reason or 'first')
            self.prepare_mask(anchor, cell)

    def prepare_mask(self, anchor, cell):
        x0, y0, x1, y1 = self.rect
        fx = math.floor((x0 - anchor[0]) / cell) - 2
        fy = math.floor((y0 - anchor[1]) / cell) - 2
        tx = math.ceil((x1 - anchor[0]) / cell) + 2
        ty = math.ceil((y1 - anchor[1]) / cell) + 2
        self.mask_rect = (fx, fy, int(min(max(tx - fx, 1), 1024)), int(min(max(ty - fy, 1), 1024)))
        self.capture_pending = True

    def mask_cell_centres(self):
        fx, fy, mw, mh = self.mask_rect
        jj, ii = np.mgrid[0:mh, 0:mw]
        cx = self.grid[0] + (fx + ii + 0.5) * self.grid[2]
        cy = self.grid[1] + (fy + jj + 0.5) * self.grid[2]
        return cx, cy

    # ---- after source render: RecordMaskUpdate, then RecordCapture (manager order)
    def after_source(self, src):
        if not self.bake_on:
            return
        if self.mask is not None and not self.capture_pending and self.live is not None:
            cx, cy = self.mask_cell_centres()
            if self.live:
                self.stats = {}
                self.mask = live_cell(src, self.mask, cx, cy, self.grid, self.mask_update, self.far_depth, self.hyst, self.stats)
            else:
                pass  # non-live update only refreshes unknown cells; none for a lone object on screen
        if self.capture_pending:
            cx, cy = self.mask_cell_centres()
            self.mask = capture_cell(src, cx, cy, self.grid[2], self.bake_depth + self.bounds_extent)
            self.capture_pending = False

    # ---- Resolve (BakedMask branch) -> boolean image of drawn pixels
    def resolve(self, src):
        out = np.zeros((H, W), bool)
        x0, y0, x1, y1 = self.rect
        # quad covers rect; rasterised pixels = centres inside rect
        xs = np.arange(int(math.floor(x0)), int(math.ceil(x1)))
        ys = np.arange(int(math.floor(y0)), int(math.ceil(y1)))
        xs = xs[(xs + 0.5 >= x0) & (xs + 0.5 < x1)]
        ys = ys[(ys + 0.5 >= y0) & (ys + 0.5 < y1)]
        PX, PY = np.meshgrid(xs + 0.5, ys + 0.5)
        if not self.bake_on:
            return self.resolve_fresh(src, PX, PY, out)
        gx, gy, cell = self.grid
        s = self.sprite_scale
        ra = (round(self.anchor[0]), round(self.anchor[1]))
        bx = gx + (PX - ra[0]) * s
        by = gy + (PY - ra[1]) * s
        cix = np.floor((bx - gx) / cell)
        ciy = np.floor((by - gy) / cell)
        mx = (cix - self.mask_rect[0]).astype(int)
        my = (ciy - self.mask_rect[1]).astype(int)
        inr = (mx >= 0) & (my >= 0) & (mx < self.mask_rect[2]) & (my < self.mask_rect[3])
        m = np.full(PX.shape, -1.0)
        m[inr] = self.mask[my[inr], mx[inr]]
        drawn = m > 0
        self.path3 = self.path2 = 0
        unk = m < 0
        if unk.any():
            ccx = ra[0] + (gx + (cix + 0.5) * cell - gx) / max(s, 1e-4)
            ccy = ra[1] + (gy + (ciy + 0.5) * cell - gy) / max(s, 1e-4)
            f = own_cell_found(src, ccx[unk], ccy[unk], cell / max(s, 1e-4))
            # not found: pixel shows ANY object -> colour of a filled 4-neighbour mask cell (path 3)
            pid, pa = fetch(src, PX[unk], PY[unk])
            edge = ~f & (pid > 0) & (pa > 0)
            nb = np.zeros(edge.shape, bool)
            ux, uy = mx[unk], my[unk]
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = ux + dx, uy + dy
                ok = (nx >= 0) & (ny >= 0) & (nx < self.mask_rect[2]) & (ny < self.mask_rect[3])
                v = np.zeros(nx.shape); v[ok] = self.mask[ny[ok], nx[ok]]
                if FIX['path3_depth']:
                    nb |= (v > 0) & (pa < self.depth + (v - self.bake_depth))
                else:
                    nb |= v > 0
            self.path3 = int((edge & nb).sum()); self.path2 = int(f.sum())
            drawn[unk] = f | (edge & nb)
        out[np.ix_(ys, xs)] = drawn
        return out

    def resolve_fresh(self, src, PX, PY, out):
        """Non-baked branch of Resolve (own >= 0.5 at exact cell centre, bilinear)."""
        cell = self.cell
        ax, ay = self.anchor
        cx = ax + (np.floor((PX - ax) / cell) + 0.5) * cell
        cy = ay + (np.floor((PY - ay) / cell) + 0.5) * cell
        a = capture_cell(src, cx, cy, cell, 1e9)
        xs = (PX[0] - 0.5).astype(int)
        ys = (PY[:, 0] - 0.5).astype(int)
        out[np.ix_(ys, xs)] = a > 0
        return out


def ang(a, b):
    return math.degrees(math.acos(max(-1.0, min(1.0, a @ b / (np.linalg.norm(a) * np.linalg.norm(b))))))


def gt_cells(cam, anchor, cell):
    """Reference: fresh grid at the current anchor; cell filled iff its exact centre is inside the sphere."""
    cx = anchor[0] + (np.floor((XX + 0.5 - anchor[0]) / cell) + 0.5) * cell
    cy = anchor[1] + (np.floor((YY + 0.5 - anchor[1]) / cell) + 0.5) * cell
    return inside_true(cam, cx, cy)
