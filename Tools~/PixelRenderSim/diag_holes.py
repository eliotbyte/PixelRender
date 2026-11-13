import sys, os, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scene3 as S
import os; sys_path_fix = None
from collections import Counter
for tok in sys.argv[1].split('+'): S.EXTRA.add(tok)
objs = S.build_scene(); byid = {o.id: o for o in objs}
C = Counter(); n = 0
def mask_state(o, x, y):
    """state of object o's mask cell covering screen pixel (x,y) (bottom-up coords)."""
    if o.screen: return 'screen'
    if not o.visible or not hasattr(o, 'maskD'): return 'novis'
    if not (o.rect[0] <= x + 0.5 < o.rect[2] and o.rect[1] <= y + 0.5 < o.rect[3]): return 'outside-rect'
    s = o.scale; ra = o.cur[:2]
    cix = int(np.floor(((x + 0.5 - ra[0]) * s) / S.CELL)); ciy = int(np.floor(((y + 0.5 - ra[1]) * s) / S.CELL))
    mx, my = cix - o.mrect[0], ciy - o.mrect[1]
    if not (0 <= mx < o.mrect[2] and 0 <= my < o.mrect[3]): return 'outside-mask'
    v = o.maskD[my, mx]
    return 'filled' if v > 0 else ('empty' if v == 0 else 'unknown')
for f in range(int(sys.argv[2])):
    cam = S.camera_at(1.6 + f / 60)
    for o in objs: o.update(cam)
    src = S.render_source(cam, objs)
    for o in objs:
        if o.screen or not o.visible: continue
        if not o.pending and hasattr(o, 'maskD'): S.live(o, src)
        if o.pending: S.capture(o, src); o.pending = False
    img, who, path = S.compose(cam, objs, src)
    h = S.holes(img, src)
    for y, x in zip(*np.nonzero(h)):
        sid = int(src[0][y, x]); n += 1
        if sid == 0:
            C[('src=background',)] += 1; continue
        o = byid[sid]
        kind = 'floor' if o.screen else ('ball' if sid == 2 else ('column' if sid < 7 else 'capsule'))
        C[(kind, mask_state(o, x, y))] += 1
print('holes', n)
for k, v in C.most_common(): print(f'  {v:5d}  {k}')
