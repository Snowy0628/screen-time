"""16px 图标参数对照试验：哪种组合在极小尺寸下最清晰。"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from PIL import Image, ImageDraw

ACCENT_A = (0x25, 0x63, 0xEB)
WHITE = (255, 255, 255, 255)


def variant(stroke=0.075, hand_w=0.105, two_hands=True, face_r=0.315, ss=8):
    size = 16
    n = size * ss
    cx = cy = n / 2
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d0 = ImageDraw.Draw(img)
    d0.rounded_rectangle([0, 0, n - 1, n - 1], radius=round(n * 0.225), fill=ACCENT_A)
    fr = n * face_r
    d0.ellipse([cx - fr, cy - fr, cx + fr, cy + fr], outline=WHITE, width=max(1, round(n * stroke)))
    hw = max(2, round(n * hand_w))
    d0.line([cx, cy, cx, cy - fr * 0.46], fill=WHITE, width=hw)
    if two_hands:
        d0.line([cx, cy, cx + fr * 0.40, cy + fr * 0.32], fill=WHITE, width=hw)
    return img.resize((size, size), Image.LANCZOS)


def white_ratio(img):
    """白色像素占比——用来衡量"内部细节是否被填满"。太高说明糊了。"""
    px = img.convert("RGBA").load()
    w = sum(1 for y in range(img.height) for x in range(img.width)
            if px[x, y][3] > 128 and px[x, y][0] > 200 and px[x, y][1] > 200 and px[x, y][2] > 200)
    return w / (img.width * img.height)


combos = [
    ("当前：双指针 粗描边", dict(stroke=0.075, hand_w=0.105, two_hands=True)),
    ("双指针 细描边", dict(stroke=0.055, hand_w=0.09, two_hands=True)),
    ("双指针 更细指针", dict(stroke=0.06, hand_w=0.07, two_hands=True)),
    ("单指针 细描边", dict(stroke=0.055, hand_w=0.095, two_hands=False)),
    ("双指针 小表盘", dict(stroke=0.06, hand_w=0.085, two_hands=True, face_r=0.28)),
]

print(f"{'方案':<22} {'白色占比':>8}  说明")
print("-" * 60)
for name, kw in combos:
    im = variant(**kw)
    r = white_ratio(im)
    note = "偏糊" if r > 0.20 else ("偏空" if r < 0.10 else "适中")
    print(f"{name:<22} {r*100:>7.1f}%  {note}")

# 输出对照图：每个方案放大 8 倍（最近邻），便于肉眼比较
SCALE = 8
out = Image.new("RGBA", (16 * SCALE * len(combos), 16 * SCALE + 20), (255, 255, 255, 255))
for i, (name, kw) in enumerate(combos):
    im = variant(**kw).resize((16 * SCALE, 16 * SCALE), Image.NEAREST)
    out.paste(im, (i * 16 * SCALE, 0), im)

here = os.path.dirname(os.path.abspath(__file__))
p = os.path.normpath(os.path.join(here, "..", "assets", "_compare-16.png"))
out.save(p)
print(f"\n对照图（左起依次为上述 5 个方案，各放大 8 倍）: {p}")

# 同时导出真实尺寸的横向对照
row = Image.new("RGBA", (16 * len(combos) + 4 * (len(combos) - 1), 16), (255, 255, 255, 255))
x = 0
for name, kw in combos:
    im = variant(**kw)
    row.paste(im, (x, 0), im)
    x += 20
p2 = os.path.normpath(os.path.join(here, "..", "assets", "_compare-16-actual.png"))
row.save(p2)
print(f"真实尺寸对照: {p2}")
