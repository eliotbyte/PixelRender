import sys, os, math, numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy import ndimage
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import two_spheres as T, two_spheres_dolly as D
from pixelsim import Cam
PERIOD = float(sys.argv[2]); SECONDS = 4.0; FPS = 60
out = sys.argv[1]; os.makedirs(out, exist_ok=True)
T.FIX_SEAM[0] = True; T.RULES.clear(); T.RULES.update('ab')
def camera(t):
    z = D.Z_MID - D.Z_AMP * math.cos(2 * math.pi * t / PERIOD)
    return Cam(np.array([0.0, 0.8, z]), T.FAR_C.copy())
font = ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf', 20)
far = T.PixelSphere(1, T.FAR_C, T.FAR_R); near = T.PixelSphere(2, D.NEAR_C, T.NEAR_R); objs=[far, near]
st=[]
for f in range(int(SECONDS*FPS)):
    T.CAM = camera(f / FPS)
    for o in objs: o.update_grid(T.CAM)
    src = T.render_source(objs)
    for o in objs: o.after_source(src)
    ours = np.zeros((T.H,T.W),np.uint8); best=np.full((T.H,T.W),np.inf)
    for o in objs:
        dr, dp = o.resolve(src); m = dr & (dp <= best); best=np.where(m,dp,best); ours=np.where(m,np.where(o.borrow_img>0,o.borrow_img,o.id),ours)
    ref = T.reference(objs)
    rgb = T.to_rgb(ours); blk = ours[::-1] == 0
    nb = sum(np.roll(~blk,(dy,dx),(0,1)) for dy,dx in ((1,0),(-1,0),(0,1),(0,-1)))
    sp = blk & (nb >= 3); sp[:40] = False; rgb[sp] = (0,255,0)
    reff = ref[::-1]
    st.append((int(sp.sum()), int((sp & (reff > 0)).sum()), int((ours != ref).sum())))
    im = Image.fromarray(rgb[:, 160:1120]); d = ImageDraw.Draw(im)
    lab, n = ndimage.label(ndimage.binary_dilation(sp[:, 160:1120], iterations=4))
    for sy, sx in ndimage.find_objects(lab):
        cy, cx = (sy.start+sy.stop)/2, (sx.start+sx.stop)/2; r = max(sy.stop-sy.start, sx.stop-sx.start)/2 + 10
        d.ellipse([cx-r, cy-r, cx+r, cy+r], outline=(255,0,60), width=2)
    d.text((14,12), f'PixelRender (с правкой шва), цикл {PERIOD:g} с', fill=(190,190,190), font=font)
    r = Image.fromarray(T.to_rgb(ref)[:, 160:1120]); ImageDraw.Draw(r).text((14,12), 'эталон', fill=(190,190,190), font=font)
    Image.fromarray(np.concatenate([np.array(im), np.full((T.H,4,3),(255,0,255),np.uint8), np.array(r)],1)).save(f'{out}/{f:04d}.png')
s=np.array(st)
print(f'period {PERIOD}s: bakes far {far.bakes} near {near.bakes} / {len(s)} frames; black specks total {s[:,0].sum()} (where ref is painted: {s[:,1].sum()}), frames with specks {(s[:,0]>0).sum()}, max {s[:,0].max()}; differs from ref mean {s[:,2].mean():.0f} px')
