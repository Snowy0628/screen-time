"""
生成应用图标（多尺寸 ICO）。

设计：蓝→紫对角渐变圆角方块 + 白色表盘 + 右上进度弧 + 指针。
品牌色沿用 UI 原型的设计令牌：--accent #2563EB → #7C3AED
（原型 hero 卡片用的是同一组渐变）

按尺寸分档处理，这是关键：
  * ≥32px：从 2048px 超采样画布缩下来，细节完整（含弧、中心点、指针投影）；
  * ≤24px：**按目标尺寸原生绘制**，删除弧与中心点、加粗描边、缩短指针。
    如果小图标也从大图缩放，指针会糊成一个白块。
"""
from PIL import Image, ImageDraw, ImageFilter
import os

SS = 8                      # 超采样倍数（仅用于大尺寸）
BASE = 256
N = BASE * SS

ACCENT_A = (0x25, 0x63, 0xEB)   # #2563EB
ACCENT_B = (0x7C, 0x3A, 0xED)   # #7C3AED
WHITE = (255, 255, 255, 255)
SHADOW = (0x1B, 0x1D, 0x21, 70)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def diagonal_gradient(size, c0, c1):
    """对角（左下 → 右上）线性渐变。"""
    img = Image.new("RGB", (size, size))
    px = img.load()
    denom = 2 * (size - 1)
    for y in range(size):
        for x in range(size):
            px[x, y] = lerp(c0, c1, (x + (size - 1 - y)) / denom)
    return img


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def render(size: int, detailed: bool, ss: int = 1) -> Image.Image:
    """
    绘制一档图标。
      size     : 目标边长
      detailed : 是否画进度弧、中心点、指针投影
      ss       : 超采样倍数

    小尺寸（detailed=False）用简化几何 + 3~4 倍超采样：
    直接按像素画会丢抗锯齿、笔画互相挤压，导致指针糊成一坨。
    """
    n = size * ss
    cx = cy = n / 2

    # ---- 背景 ----
    bg = diagonal_gradient(n, ACCENT_A, ACCENT_B) if detailed else Image.new("RGB", (n, n), ACCENT_A)
    icon = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    icon.paste(bg, (0, 0), rounded_mask(n, max(2, round(n * 0.225))))

    d = ImageDraw.Draw(icon)

    # ---- 表盘 ----
    face_r = n * 0.315
    stroke = max(1, round(n * (0.055 if detailed else 0.060)))
    d.ellipse([cx - face_r, cy - face_r, cx + face_r, cy + face_r], outline=WHITE, width=stroke)

    # ---- 右上进度弧（Pillow 角度顺时针、0° 在 3 点方向）----
    # 从 12 点(270°)顺时针转到约 4 点(375°)，正好框住分针所在的右上区域
    if detailed:
        arc_r = n * 0.405
        d.arc(
            [cx - arc_r, cy - arc_r, cx + arc_r, cy + arc_r],
            start=272, end=378,
            fill=(255, 255, 255, 170),
            width=max(1, round(n * 0.032)),
        )

    # ---- 指针几何 ----
    # 简化版参数由 tools/icon-compare.py 的对照试验确定：
    # 细描边 + 更细指针在这一档白色占比 10.2%，指针仍有区分度；
    # 用更粗的指针会糊成一坨（11.7%），更细的描边则过空（5.9%）。
    if detailed:
        hour_end = (cx - n * 0.012, cy - face_r * 0.52)
        minute_end = (cx + face_r * 0.46, cy + face_r * 0.40)
        hand_w = max(1, round(n * 0.068))
    else:
        hour_end = (cx, cy - face_r * 0.46)
        minute_end = (cx + face_r * 0.40, cy + face_r * 0.32)
        hand_w = max(2, round(n * 0.070))

    # ---- 指针投影（仅大尺寸：加一层轻微暗色偏移，制造层次）----
    if detailed:
        off = max(1, round(n * 0.008))
        for (x0, y0, x1, y1) in [
            (cx + off, cy + off, hour_end[0] + off, hour_end[1] + off),
            (cx + off, cy + off, minute_end[0] + off, minute_end[1] + off),
        ]:
            d.line([x0, y0, x1, y1], fill=SHADOW, width=hand_w)
        # 让投影只留在指针外侧：重画一次白色指针覆盖主体
        d.line([cx, cy, hour_end[0], hour_end[1]], fill=WHITE, width=hand_w)
        d.line([cx, cy, minute_end[0], minute_end[1]], fill=WHITE, width=hand_w)
    else:
        d.line([cx, cy, hour_end[0], hour_end[1]], fill=WHITE, width=hand_w)
        d.line([cx, cy, minute_end[0], minute_end[1]], fill=WHITE, width=hand_w)

    # ---- 中心点（小尺寸画了会变成一坨，只在 detailed 时画）----
    if detailed:
        dot = n * 0.046
        d.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=WHITE)

    return icon.resize((size, size), Image.LANCZOS) if ss > 1 else icon


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.normpath(os.path.join(here, "..", "assets"))
    os.makedirs(out_dir, exist_ok=True)

    frames = []
    # 大尺寸：8 倍超采样后缩放，细节完整
    for size in (256, 128, 64, 48, 32):
        frames.append(render(size, detailed=True, ss=SS))
    # 小尺寸：简化几何 + 4 倍超采样（保留抗锯齿，又不至于糊成一坨）
    for size in (24, 20, 16):
        frames.append(render(size, detailed=False, ss=4))

    # 校验：非方形或尺寸不符会让 ICO 写失败
    for f in frames:
        assert f.width == f.height, "图标必须是正方形"

    ico_path = os.path.join(out_dir, "app.ico")
    frames[0].save(
        ico_path,
        format="ICO",
        sizes=[(f.width, f.height) for f in frames],
        append_images=frames[1:],
    )
    print(f"已生成 ICO: {ico_path}")
    print(f"  含尺寸: {', '.join(f'{f.width}x{f.height}' for f in frames)}")

    for f, tag in [(frames[0], 256), (frames[4], 32), (frames[5], 24), (frames[7], 16)]:
        p = os.path.join(out_dir, f"icon-{tag}.png")
        f.save(p, format="PNG")
    print(f"已导出预览 PNG 到: {out_dir}")


if __name__ == "__main__":
    main()
