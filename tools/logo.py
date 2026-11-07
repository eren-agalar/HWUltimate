# Renders the HWUltimate logo to PNG / multi-size ICO. The same geometry is drawn as vectors by HwuLogo (UI/Controls).
# Geometry is in a 64 x 64 design grid. Run: python3 tools/logo.py
import io, os, struct
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "..", "src", "HWUltimate.App", "Assets")

TOP = (124, 92, 255)      # violet
BOTTOM = (34, 211, 238)   # cyan
INK = (255, 255, 255)

PULSE = [(19, 34), (25, 34), (28, 27), (32, 41), (36, 23), (39, 34), (45, 34)]

def render(size, ss=8):
    s = size * ss
    k = s / 64.0
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    # diagonal gradient badge
    grad = Image.new("RGBA", (s, s))
    gp = grad.load()
    for y in range(s):
        for x in range(s):
            t = (x + y) / (2 * s - 2)
            gp[x, y] = tuple(int(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3)) + (255,)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([2 * k, 2 * k, 62 * k, 62 * k], radius=15 * k, fill=255)
    img.paste(grad, (0, 0), mask)
    # soft top highlight
    hl = Image.new("L", (s, s), 0)
    ImageDraw.Draw(hl).rounded_rectangle([2 * k, 2 * k, 62 * k, 34 * k], radius=15 * k, fill=38)
    img.paste(Image.new("RGBA", (s, s), INK + (255,)), (0, 0), Image.composite(hl, Image.new("L", (s, s), 0), mask))

    d = ImageDraw.Draw(img)
    small = size <= 24
    w = (3.6 if small else 2.6) * k
    # chip body
    d.rounded_rectangle([15 * k, 15 * k, 49 * k, 49 * k], radius=6 * k, outline=INK, width=round(w))
    # pins (fewer at tiny sizes so they stay distinct)
    pins = [32] if small else [24, 32, 40]
    for p in pins:
        for a, b in [((p, 8.5), (p, 15)), ((p, 49), (p, 55.5)), ((8.5, p), (15, p)), ((49, p), (55.5, p))]:
            d.line([a[0] * k, a[1] * k, b[0] * k, b[1] * k], fill=INK, width=round(w))
    # sensor pulse
    pts = [(x * k, y * k) for x, y in (PULSE if not small else [(20, 33), (27, 33), (32, 24), (37, 40), (44, 33)])]
    d.line(pts, fill=INK, width=round((4.0 if small else 3.0) * k), joint="curve")
    r = w / 2
    for x, y in (pts[0], pts[-1]):
        d.ellipse([x - r, y - r, x + r, y + r], fill=INK)
    return img.resize((size, size), Image.LANCZOS)

def write_ico(path, images):
    entries, blobs = [], []
    for im in images:
        buf = io.BytesIO(); im.save(buf, "PNG"); blobs.append(buf.getvalue())
    offset = 6 + 16 * len(images)
    header = struct.pack("<HHH", 0, 1, len(images))
    for im, blob in zip(images, blobs):
        w = 0 if im.width >= 256 else im.width
        entries.append(struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    with open(path, "wb") as f:
        f.write(header + b"".join(entries) + b"".join(blobs))

if __name__ == "__main__":
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    imgs = [render(sz) for sz in sizes]
    write_ico(os.path.join(ASSETS, "hwultimate.ico"), imgs)
    imgs[-1].save(os.path.join(ASSETS, "hwultimate.png"))
    print("written", sizes)
