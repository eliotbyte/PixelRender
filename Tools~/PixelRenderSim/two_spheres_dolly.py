"""
Two static spheres; the near one covers about half of the far one.
Camera moves straight back and forth (dolly) towards the near sphere, looking at the far sphere centre.
"""
import math, os, sys
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import twospheres as T
from pixelsim import Cam

FPS, SECONDS = 60, 6.0
Z_MID, Z_AMP, PERIOD = -13.0, 6.5, 3.0   # camera z: -6.5 (2.5 m to near sphere) .. -19.5
NEAR_C = np.array([1.6, 0.0, -4.0])   # covers about half of the far sphere


def camera(t):
    z = Z_MID - Z_AMP * math.cos(2 * math.pi * t / PERIOD)    # starts far, comes close (2.5 m to the near sphere), pulls back
    return Cam(np.array([0.0, 0.8, z]), T.FAR_C.copy())


def main(out):
    far = T.PixelSphere(1, T.FAR_C, T.FAR_R)
    near = T.PixelSphere(2, NEAR_C, T.NEAR_R)
    objs = [far, near]
    fdir = os.path.join(out, 'frames'); os.makedirs(fdir, exist_ok=True)
    stats = []
    for f in range(int(SECONDS * FPS)):
        T.CAM = camera(f / FPS)
        for o in objs:
            o.update_grid(T.CAM)
        src = T.render_source(objs)
        for o in objs:
            o.after_source(src)
        ours = np.zeros((T.H, T.W), int); best = np.full((T.H, T.W), np.inf)
        for o in objs:
            dr, dp = o.resolve(src)
            m = dr & (dp <= best)
            best = np.where(m, dp, best); ours = np.where(m, o.id, ours)
        ref = T.reference(objs)
        stats.append(((ours != ref).sum(), ((ours == 1) & (ref != 1)).sum(), ((ours == 2) & (ref != 2)).sum()))
        a, b = T.to_rgb(ours)[:, 160:1120], T.to_rgb(ref)[:, 160:1120]
        sep = np.full((T.H, 4, 3), (255, 0, 255), np.uint8)
        img = np.concatenate([T.label(a, 'PixelRender (наш метод)'), sep,
                              T.label(b, 'эталон: свежая сетка, каждый объект отдельно')], 1)
        Image.fromarray(img).save(os.path.join(fdir, f'{f:04d}.png'))
    s = np.array(stats)
    print(f'bakes far {far.bakes} near {near.bakes}')
    print(f'pixels differing: mean {s[:,0].mean():.0f} max {s[:,0].max()}; far extra mean {s[:,1].mean():.0f} max {s[:,1].max()}; near extra mean {s[:,2].mean():.0f} max {s[:,2].max()}')
    np.save(os.path.join(out, 'stats.npy'), s)


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '.')
