"""
Пол (одна огромная плоскость) — сравнение способов пикселизации в движении.

  screen  — как сейчас (ScreenGrid): клетка фиксирована на экране, цвет — точка пола в центре клетки.
  world   — сетка на поверхности фиксированного размера (тексель), без LOD.
  iso     — сетка на поверхности, размер клетки ступеньками x2, один уровень на обе оси.
  aniso   — сетка на поверхности, уровень x2 ОТДЕЛЬНО по каждой оси поверхности
            (как анизотропная фильтрация); уровень решается в центре крупной клетки и
            уточняется вниз (kd-дерево) — клетка всегда целая, решение одно на всю клетку.

Метрика «смена»: точка поверхности меняет цвет между кадрами (функция цвета метода
вычисляется для той же мировой точки камерой прошлого кадра). «Мигание» — A->B->A за 3 кадра.
Размер клетки на экране: медиана и доля клеток уже 2 px по какой-либо оси.

    python floor_lod.py [race|orbit]
"""
import math, sys, os
import numpy as np
from PIL import Image

W, H = 640, 360
FOV = 50.0
FOCAL = 0.5 * H / math.tan(math.radians(FOV) * 0.5)
CELL = 3.5            # целевой размер клетки, px (= 7 при 720p)
FAR = 80.0
S0 = 8.0              # самая крупная клетка поверхности, м
MAXL = 10
TEX = 0.125           # мелкий тексель текстуры пола, м

PAL = np.array([[70, 74, 80], [92, 96, 102], [58, 62, 66], [120, 112, 96], [84, 100, 72]], np.uint8)
SKY = np.array([150, 180, 210], np.uint8)


def hash2(i, j):
    h = (i * 73856093) ^ (j * 19349663)
    h = (h ^ (h >> 13)) * 1274126177
    return (h ^ (h >> 16)) & 0x7fffffff


def tex(x, z):
    """Индекс палитры: шахматка 1 м + шум по 0.125 м + полоски 'дороги'."""
    i, j = np.floor(x / TEX).astype(np.int64), np.floor(z / TEX).astype(np.int64)
    checker = ((np.floor(x) + np.floor(z)) % 2).astype(np.int64)
    n = hash2(i, j) % 7
    idx = np.where(n == 0, 2, checker)
    idx = np.where(n == 1, 3, idx)
    road = (np.abs(np.mod(x, 6.0) - 3.0) < 0.15)
    return np.where(road, 4, idx)


class Cam:
    def __init__(self, pos, yaw, pitch):
        y, p = math.radians(yaw), math.radians(pitch)
        f = np.array([math.sin(y) * math.cos(p), -math.sin(p), math.cos(y) * math.cos(p)])
        r = np.cross([0, 1, 0], f); r /= np.linalg.norm(r)
        self.pos, self.f, self.r, self.u = np.asarray(pos, float), f, r, np.cross(f, r)

    def project(self, X, Z):
        """Точки пола (y=0) -> экран (px от левого-нижнего), глубина."""
        d = np.stack([X - self.pos[0], -self.pos[1] + 0 * X, Z - self.pos[2]], -1)
        vx, vy, vz = d @ self.r, d @ self.u, d @ self.f
        vz = np.maximum(vz, 1e-4)
        return W * 0.5 + FOCAL * vx / vz, H * 0.5 + FOCAL * vy / vz, vz

    def ray_floor(self, sx, sy):
        dx = (sx - W * 0.5) / FOCAL; dy = (sy - H * 0.5) / FOCAL
        d = self.r[None, None] * dx[..., None] + self.u[None, None] * dy[..., None] + self.f
        t = np.where(d[..., 1] < -1e-6, -self.pos[1] / np.minimum(d[..., 1], -1e-6), np.inf)
        X, Z = self.pos[0] + t * d[..., 0], self.pos[2] + t * d[..., 2]
        depth = t * (d @ self.f)
        hit = np.isfinite(t) & (depth < FAR)
        return hit, X, Z


def camera(t, scene):
    if scene == 'race':      # гонка: низко, быстро вперёд, покачивание
        yaw = 8 * math.sin(t * 0.9)
        z = 7.0 * t
        x = 1.2 * math.sin(t * 0.7)
        return Cam([x, 1.4 + 0.05 * math.sin(t * 5), z], yaw, 9 + 1.5 * math.sin(t * 1.3))
    yaw = 25 + 3 * t * 10    # orbit: как PixelRender — облёт на ~10 м, взгляд на 35° вниз
    d = 9.75 + 1.85 * math.sin(t * 2 * math.pi / 5.2)
    y, p = math.radians(yaw), math.radians(35)
    pos = np.array([-math.sin(y) * math.cos(p) * d, 2.2 + math.sin(p) * d, -math.cos(y) * math.cos(p) * d])
    return Cam(pos, yaw, 35)


# ---------------------------------------------------------------- методы: мировая точка -> (цвет, id клетки)
def screen_grid(cam, X, Z):
    sx, sy, _ = cam.project(X, Z)
    cell = round(CELL)
    cx, cy = (np.floor(sx / cell) + 0.5) * cell, (np.floor(sy / cell) + 0.5) * cell
    hit, Xc, Zc = cam.ray_floor(cx, cy)
    return np.where(hit, tex(Xc, Zc), -1), np.floor(sx / cell) * 10000 + np.floor(sy / cell)


def world_grid(cam, X, Z):
    i, j = np.floor(X / TEX), np.floor(Z / TEX)
    return tex((i + .5) * TEX, (j + .5) * TEX), i * 100000 + j


def edge_len(cam, X, Z, du, dv):
    """Длина на экране ребра клетки du (вдоль x) и dv (вдоль z) в её центре."""
    sx, sy, _ = cam.project(X, Z)
    ax, ay, _ = cam.project(X + du * .5, Z); bx, by, _ = cam.project(X - du * .5, Z)
    cx, cy, _ = cam.project(X, Z + dv * .5); ex, ey, _ = cam.project(X, Z - dv * .5)
    return np.hypot(ax - bx, ay - by), np.hypot(cx - ex, cy - ey)


SPLIT = CELL * math.sqrt(2)   # делим, пока ребро длиннее CELL*√2 -> итог в (CELL/√2, CELL*√2]


def lod_grid(cam, X, Z, aniso):
    lu = np.zeros(X.shape, int); lv = np.zeros(X.shape, int)
    for _ in range(2 * MAXL):
        su, sv = S0 / 2.0 ** lu, S0 / 2.0 ** lv
        cu, cv = (np.floor(X / su) + .5) * su, (np.floor(Z / sv) + .5) * sv
        Lu, Lv = edge_len(cam, cu, cv, su, sv)
        if aniso:
            wu = (Lu > SPLIT) & (lu < MAXL) & (Lu >= Lv)
            wv = (Lv > SPLIT) & (lv < MAXL) & ~wu
            wu |= (Lu > SPLIT) & (lu < MAXL) & ~wv & ~(Lv > SPLIT)
        else:
            wu = (np.maximum(Lu, Lv) > SPLIT) & (lu < MAXL); wv = wu
        if not (wu.any() or wv.any()):
            break
        lu += wu; lv += wv
    su, sv = S0 / 2.0 ** lu, S0 / 2.0 ** lv
    iu, iv = np.floor(X / su), np.floor(Z / sv)
    Lu, Lv = edge_len(cam, (iu + .5) * su, (iv + .5) * sv, su, sv)
    cid = ((lu * 16 + lv) * 1e6 + iu) * 1e6 + iv
    return tex((iu + .5) * su, (iv + .5) * sv), cid, Lu, Lv


METHODS = {
    'screen': lambda c, X, Z: screen_grid(c, X, Z),
    'world': lambda c, X, Z: world_grid(c, X, Z),
    'iso': lambda c, X, Z: lod_grid(c, X, Z, False)[:2],
    'aniso': lambda c, X, Z: lod_grid(c, X, Z, True)[:2],
}


def main():
    scene = sys.argv[1] if len(sys.argv) > 1 else 'race'
    frames, dt = int(os.environ.get("FRAMES", 40)), 1 / 60
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out', 'FloorLod')
    os.makedirs(out, exist_ok=True)
    yy, xx = np.mgrid[0:H, 0:W] + 0.5
    cams = [camera(i * dt, scene) for i in range(frames)]
    lines = [f'сцена {scene}, {W}x{H}, клетка {CELL}px, {frames} кадров']
    for name, fn in METHODS.items():
        changes = blinks = total = 0
        vis = vtotal = 0
        prev_img = None
        gif = []
        for k in range(2, frames):
            cam = cams[k]
            hit, X, Z = cam.ray_floor(xx, yy)
            col, _ = fn(cam, X, Z)
            col = np.where(hit, col, -1)
            img = np.where((col >= 0)[..., None], PAL[np.maximum(col, 0)], SKY)
            if k % 2 == 0:
                gif.append(Image.fromarray(img[::-1]).resize((W * 2, H * 2), Image.NEAREST))
            if prev_img is not None:
                # видимая смена: пиксель кадра против пикселя прошлого КАДРА, куда та же точка пола проецировалась
                px, py, _ = cams[k - 1].project(X, Z)
                ix, iy = np.floor(px).astype(int), np.floor(py).astype(int)
                ok = hit & (ix >= 0) & (ix < W) & (iy >= 0) & (iy < H)
                ix, iy = np.clip(ix, 0, W - 1), np.clip(iy, 0, H - 1)
                vtotal += ok.sum()
                vis += (ok & (prev_img[iy, ix] != col)).sum()
            prev_img = col
            p1, _ = fn(cams[k - 1], X, Z)
            p2, _ = fn(cams[k - 2], X, Z)
            m = hit & (p1 >= 0) & (p2 >= 0)
            total += m.sum()
            changes += (m & (col != p1)).sum()
            blinks += (m & (col != p1) & (col == p2)).sum()
        gif[0].save(os.path.join(out, f'{scene}_{name}.gif'), save_all=True, append_images=gif[1:], duration=33, loop=0)
        extra = ''
        if name in ('iso', 'aniso'):
            hit, X, Z = cams[frames // 2].ray_floor(xx, yy)
            _, cid, Lu, Lv = lod_grid(cams[frames // 2], X, Z, name == 'aniso')
            mn = np.minimum(Lu, Lv)[hit]
            extra = f'  клетка: медиана {np.median(np.maximum(Lu, Lv)[hit]):.1f}x{np.median(mn):.1f}px, уже 2px: {100 * (mn < 2).mean():.1f}%'
        lines.append(f'{name:7s} ВИДИМАЯ смена {100 * vis / max(vtotal, 1):6.2f}% | {100 * changes / total:6.2f}%  мигание {100 * blinks / total:6.2f}% пикс/кадр{extra}')
    print('\n'.join(lines))
    with open(os.path.join(out, f'{scene}.txt'), 'w') as f:
        f.write('\n'.join(lines) + '\n')


if __name__ == '__main__':
    main()
