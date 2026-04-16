from PIL import Image
import numpy as np

for fn in ['sts2.png', 'sv.png', 'mc.png']:
    img = Image.open(fn).convert('RGBA')
    data = np.array(img, dtype=np.float32)
    r, g, b, a = data[...,0], data[...,1], data[...,2], data[...,3]
    darkness = (255 - r + 255 - g + 255 - b) / 3.0
    new_a = np.where(darkness < 30, 0,
            np.where(darkness < 80, (darkness - 30) / 50.0 * a, a))
    data[...,3] = np.clip(new_a, 0, 255).astype(np.uint8)
    Image.fromarray(data.astype(np.uint8), 'RGBA').save(fn)
    print(fn, 'done')
