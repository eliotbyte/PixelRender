import sys, math, numpy as np
import os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pixelsim as P, run as R
P.SCENE['floor'] = True
def occ(t):
    c = P.camera_at(t)
    to_cam = c.pos - P.BALL_C; to_cam /= np.linalg.norm(to_cam)
    sweep = -4.0 + 8.0 * ((t - 1.6) % 4.0) / 4.0
    return P.BALL_C + to_cam * 3.2 + c.r * sweep + c.u * (-0.8), 0.7
P.SCENE['occluders'] = [occ]
for name, fx in [('ORIGINAL', {'path3_depth':False,'near_limit':False}),
                 ('FIX both', {'path3_depth':True,'near_limit':True})]:
    P.FIX.update(fx)
    a, w, ball = R.run('ball + floor + passing occluder, '+name, seconds=4.0, fps=30)
    R.save_compare(w, f'occ_{name.split()[0].lower()}_worst.png')
