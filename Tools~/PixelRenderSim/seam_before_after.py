import sys, os, numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy import ndimage
import os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import two_spheres as T, two_spheres_dolly as D
out = sys.argv[1]; os.makedirs(out, exist_ok=True)
font = ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf', 20)
def run(fix):
    T.FIX_SEAM[0] = fix; T.RULES.clear(); T.RULES.update('ab')
    far = T.PixelSphere(1, T.FAR_C, T.FAR_R); near = T.PixelSphere(2, D.NEAR_C, T.NEAR_R); objs=[far, near]
    res=[]
    for f in range(360):
        T.CAM = D.camera(f / 60)
        for o in objs: o.update_grid(T.CAM)
        src = T.render_source(objs)
        for o in objs: o.after_source(src)
        ours = np.zeros((T.H,T.W),np.uint8); best=np.full((T.H,T.W),np.inf)
        for o in objs:
            dr, dp = o.resolve(src); m = dr & (dp <= best); best=np.where(m,dp,best); ours=np.where(m,np.where(o.borrow_img>0,o.borrow_img,o.id),ours)
        res.append(ours)
    return res
A = run(False); B = run(True)
def panel(a, title):
    rgb = T.to_rgb(a)
    blk = a[::-1] == 0
    nb = sum(np.roll(~blk,(dy,dx),(0,1)) for dy,dx in ((1,0),(-1,0),(0,1),(0,-1)))
    sp = blk & (nb >= 3); sp[:40] = False
    rgb[sp] = (0,255,0)
    im = Image.fromarray(rgb[:, 160:1120]); d = ImageDraw.Draw(im)
    lab, n = ndimage.label(ndimage.binary_dilation(sp[:, 160:1120], iterations=4))
    for sl in ndimage.find_objects(lab):
        sy, sx = sl; cy, cx = (sy.start+sy.stop)/2, (sx.start+sx.stop)/2; r = max(sy.stop-sy.start, sx.stop-sx.start)/2 + 10
        d.ellipse([cx-r, cy-r, cx+r, cy+r], outline=(255,0,60), width=2)
    d.text((14,12), title, fill=(190,190,190), font=font)
    return np.array(im), int(sp.sum())
cnt=[0,0]
for f in range(360):
    a, ca = panel(A[f], 'до правки'); b, cb = panel(B[f], 'после правки')
    cnt[0]+=ca; cnt[1]+=cb
    Image.fromarray(np.concatenate([a, np.full((T.H,4,3),(255,0,255),np.uint8), b],1)).save(f'{out}/{f:04d}.png')
print('specks total before/after:', cnt)
