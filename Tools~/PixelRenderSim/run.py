import sys, json, math
import numpy as np
from scipy import ndimage
from PIL import Image
import os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixelsim import *


def err_px(ours, true):
    """max outward (ours outside true) and inward (true not covered) error, pixels."""
    d_out = ndimage.distance_transform_edt(~true)
    d_in = ndimage.distance_transform_edt(~ours) if ours.any() else np.full(true.shape, 1e9)
    return float(d_out[ours].max(initial=0)), float(d_in[true].max(initial=0))


def run(label, t0=1.6, seconds=8.0, fps=60, cam_kw=None, **ball_kw):
    ball = PixelBall(**ball_kw)
    rows = []
    worst = None
    n = int(seconds * fps)
    for f in range(n):
        t = t0 + f / fps
        cam = camera_at(t, **(cam_kw or {}))
        ball.update_grid(cam)
        SCENE_T[0] = t
        src = render_scene(cam)
        ball.after_source(src)
        ours = ball.resolve(src)
        true = src[0] == 1
        occ = src[0] >= 3
        ours = ours & ~occ
        gt = gt_cells(cam, ball.anchor, ball.cell) & ~occ
        o_out, o_in = err_px(ours, true)
        g_out, g_in = err_px(gt, true)
        diff = int((ours != gt).sum())
        rows.append((t, o_out, o_in, g_out, g_in, diff, ball.bakes, getattr(ball,'path3',0), getattr(ball,'stats',{}).get('stuck',0)))
        score = max(o_out, o_in)
        if worst is None or score > worst[0]:
            worst = (score, f, t, ours.copy(), gt.copy(), true.copy(), ball.bakes)
    a = np.array(rows)
    print(f"[{label}] frames={n} bakes={ball.bakes} reasons={dict((r, ball.bake_reasons.count(r)) for r in set(ball.bake_reasons))}")
    print(f"   OURS  outward err: mean {a[:,1].mean():.1f}px max {a[:,1].max():.1f}px | inward mean {a[:,2].mean():.1f} max {a[:,2].max():.1f}")
    print(f"   GT    outward err: mean {a[:,3].mean():.1f}px max {a[:,3].max():.1f}px | inward mean {a[:,4].mean():.1f} max {a[:,4].max():.1f}")
    print(f"   unknown-cell 'neighbour colour' pixels (Resolve path 3): mean {a[:,7].mean():.0f}/frame; filled cells frozen by visible<8: mean {a[:,8].mean():.1f}")
    print(f"   pixels differing ours vs GT: mean {a[:,5].mean():.0f}/frame, max {a[:,5].max():.0f}; worst frame t={worst[2]:.3f}")
    return a, worst, ball


def save_compare(worst, path, crop=None):
    _, f, t, ours, gt, true, _ = worst
    def img(m, col):
        im = np.zeros(m.shape + (3,), np.uint8)
        im[m] = col
        edge = true & ~ndimage.binary_erosion(true)
        im[edge] = (255, 40, 40)
        return im[::-1]  # bottom-left origin -> image
    ys, xs = np.where(true | ours | gt)
    y0, y1, x0, x1 = ys.min() - 20, ys.max() + 20, xs.min() - 20, xs.max() + 20
    y0, x0 = max(y0, 0), max(x0, 0)
    a = img(ours, (235, 235, 230))[H - 1 - y1:H - y0, x0:x1 + 1]
    b = img(gt, (235, 235, 230))[H - 1 - y1:H - y0, x0:x1 + 1]
    sep = np.full((a.shape[0], 8, 3), (255, 0, 255), np.uint8)
    Image.fromarray(np.concatenate([a, sep, b], 1)).save(path)


if __name__ == '__main__':
    out = {}
    a, w, _ = run('default (bake + live silhouette, hyst 0.2)')
    save_compare(w, 'default_worst.png')
    a, w, _ = run('bake, live hyst 0', hyst=0.0)
    a, w, _ = run('bake, live OFF', live=False)
    save_compare(w, 'nolive_worst.png')
    a, w, _ = run('no bake (fresh grid every frame)', bake=False)
