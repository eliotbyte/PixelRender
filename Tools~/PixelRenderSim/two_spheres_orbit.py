"""
Two static spheres, camera orbits the far sphere's centre and always looks at it.
Near sphere sits 4.5 m from the far one: once per orbit it passes in front of it, once it hides behind it.
"""
import math, os, sys
import numpy as np
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import twospheres as T
from pixelsim import Cam

FPS, SECONDS = 60, 6.0
ORBIT_R, ORBIT_H, DEG_PER_S = 11.0, 1.5, 60.0      # full turn in 6 s
NEAR_C = np.array([0.0, 0.4, -4.5])


def camera(t):
    yaw = math.radians(DEG_PER_S * t)
    pos = T.FAR_C + np.array([math.sin(yaw) * ORBIT_R, ORBIT_H, -math.cos(yaw) * ORBIT_R])
    return Cam(pos, T.FAR_C.copy())


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
