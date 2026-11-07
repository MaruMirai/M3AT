"""How much of each painted view's outline overlaps the render's: python3 iou.py <view> <png> ..."""
import sys
import numpy as np
from PIL import Image
m = np.array(Image.open('0_render_%s.png' % sys.argv[1]).convert('RGBA').resize((512, 512)))[..., 3] > 128
for f in sys.argv[2:]:
    a = np.array(Image.open(f).convert('RGB').resize((512, 512))).astype(int)
    g = np.abs(a - a[5, 5]).sum(2) > 30
    print(f, 'IoU %.3f' % ((m & g).sum() / (m | g).sum()))
