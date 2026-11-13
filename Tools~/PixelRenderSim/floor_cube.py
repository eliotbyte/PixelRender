import sys, os, math, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scene3 as S
from PIL import Image, ImageDraw, ImageFont
from pixelsim import Cam, euler
font = ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf', 18)
def cam_simple(t):   # orbit around the cube + dolly, like PixelRenderCameraMotion but closer
    yaw = 25 + 20 * t; d = 7.0 + 2.5 * math.sin(t * 2 * math.pi / 2.6)
    pos = np.array([0, 0.6, 0]) + euler(30, yaw, 0) @ np.array([0, 0, -1.0]) * d
    return Cam(pos, np.array([0, 0.6, 0]))
def run(extra, n):
    S.EXTRA.clear(); S.EXTRA.update(extra)
    objs = S.build_scene(); P = S.palette_of(objs); out = []; st = []
    for f in range(n):
        cam = cam_simple(f / 60)
        for o in objs: o.update(cam)
        src = S.render_source(cam, objs)
        for o in objs:
            if o.screen or not o.visible: continue
            if not o.pending and hasattr(o, 'maskD'): S.live(o, src)
            if o.pending: S.capture(o, src); o.pending = False
        img, who, path = S.compose(cam, objs, src)
        ref = S.reference(cam, objs)
        h = S.holes(img, src)
        lone = int(((sum((np.roll(img, (dy, dx), (0, 1)) != img) for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1))) >= 3) & (img > 0)).sum())
        st.append((int(h.sum()), lone, int((img != ref).sum())))
        out.append((S.to_rgb(img, P), S.to_rgb(ref, P), h[::-1]))
    s = np.array(st)
    print(f'{"+".join(sorted(extra)) or "current"}: black holes/frame {s[:,0].mean():.1f}, lone px/frame {s[:,1].mean():.1f}, differs from ref {s[:,2].mean():.0f}', flush=True)
    return out
n = int(sys.argv[2])
A = run({'SIMPLE'}, n); B = run({'SIMPLE', 'F', 'Q', 'G'}, n)
os.makedirs(sys.argv[1], exist_ok=True)
x0, x1, y0, y1 = 240, 1040, 60, 660
for f in range(n):
    tiles = []
    for (img, h, title) in ((A[f][0], A[f][2], 'сейчас'), (B[f][0], B[f][2], 'с правкой'), (A[f][1], None, 'эталон')):
        im = img.copy()
        if h is not None: im[h] = (0, 255, 0)
        t = Image.fromarray(im[y0:y1, x0:x1]); ImageDraw.Draw(t).text((10, 8), title, fill=(220, 220, 220), font=font)
        tiles += [np.array(t), np.full((y1 - y0, 4, 3), (255, 0, 255), np.uint8)]
    Image.fromarray(np.concatenate(tiles[:-1], 1)).save(f'{sys.argv[1]}/{f:04d}.png')
