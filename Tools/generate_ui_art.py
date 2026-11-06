# -*- coding: utf-8 -*-
"""
SparkCode - start menu UI art baker.

Bakes every piece of player facing text (Chinese & English) plus frames,
panels and decorative glows into transparent PNGs under Assets/Art/UI/.
The Unity side (StartMenuBuilder) only loads sprites and never relies on
TMP / system fonts at runtime, which keeps the Chinese glyphs pixel exact.

All text art is rendered at 2x the intended UI size (PPU 100 -> display
size = sprite rect / 2).

Run:
  C:\\Users\\wsy\\.dsh\\dsh-runtimes\\dsh-primary-runtime\\dependencies\\python\\python.exe Tools\\generate_ui_art.py
"""

import math
import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Assets", "Art", "UI")
SCALE = 2

F_TITLE = "C:/Windows/Fonts/STXINGKA.TTF"        # brush titles
F_SERIF = "C:/Windows/Fonts/NotoSerifSC-VF.ttf"  # body / buttons
F_SANS = "C:/Windows/Fonts/msyhbd.ttc"           # latin small caps

GOLD_BRIGHT = [
    (0.00, (255, 250, 228)),
    (0.22, (255, 233, 178)),
    (0.45, (244, 206, 126)),
    (0.66, (214, 166, 80)),
    (0.85, (166, 117, 46)),
    (1.00, (226, 180, 104)),
]
GOLD = [
    (0.00, (255, 244, 208)),
    (0.30, (248, 220, 154)),
    (0.56, (226, 183, 100)),
    (0.80, (176, 126, 52)),
    (1.00, (206, 158, 84)),
]
CREAM = [
    (0.00, (255, 249, 235)),
    (0.45, (244, 234, 212)),
    (1.00, (214, 199, 168)),
]
CREAM_DIM = [
    (0.00, (236, 226, 203)),
    (1.00, (190, 178, 152)),
]

DARK_STROKE = (24, 15, 6)
DARK_STROKE_SOFT = (18, 14, 9)

MANIFEST = []


def S(v):
    return int(round(v * SCALE))


def lerp(c1, c2, t):
    return tuple(int(round(a + (b - a) * t)) for a, b in zip(c1, c2))


def gradient_img(w, h, stops):
    col = Image.new("RGBA", (1, h))
    px = col.load()
    for y in range(h):
        t = y / max(1, h - 1)
        c = stops[-1][1]
        for i in range(len(stops) - 1):
            p0, c0 = stops[i]
            p1, c1 = stops[i + 1]
            if p0 <= t <= p1:
                k = (t - p0) / max(1e-6, p1 - p0)
                c = lerp(c0, c1, k)
                break
        px[0, y] = c if len(c) == 4 else c + (255,)
    return col.resize((w, h), Image.BILINEAR)


def make_font(path, size, weight=None):
    f = ImageFont.truetype(path, max(4, int(size)))
    if weight is not None:
        try:
            f.set_variation_by_axes([float(weight)])
        except Exception:
            pass
    return f


def line_width(font, text, spacing):
    if not text:
        return 0.0
    return sum(font.getlength(ch) for ch in text) + spacing * (len(text) - 1)


def crop_rgba(img, margin=2):
    bbox = img.getbbox()
    if not bbox:
        return img
    l, t, r, b = bbox
    l = max(0, l - margin)
    t = max(0, t - margin)
    r = min(img.width, r + margin)
    b = min(img.height, b + margin)
    return img.crop((l, t, r, b))


def render_text(lines, fontpath, size, weight=None, spacing=0.0, line_gap=0.0,
                align="left", gradient=None, stroke=0.0, stroke_color=DARK_STROKE,
                glow=0.0, glow_color=(255, 186, 92), glow_alpha=0.85,
                slant=0.0, pad=12.0, alpha=1.0):
    """Render text into a tightly cropped transparent RGBA image.

    Sizes are given in display units; the bitmap is baked at SCALE (2x).
    """
    if isinstance(lines, str):
        lines = [lines]
    size_b = max(6, S(size))
    spacing_b = S(spacing)
    line_gap_b = S(line_gap)
    stroke_b = S(stroke)
    glow_b = max(0, S(glow))
    pad_b = max(6, S(pad))

    font = make_font(fontpath, size_b, weight)
    widths = [line_width(font, ln, spacing_b) for ln in lines]
    maxw = max(1, int(math.ceil(max(widths) if widths else 1)))
    asc, desc = font.getmetrics()
    lh = asc + desc
    H = lh + max(0, len(lines) - 1) * (lh + line_gap_b)

    inner = Image.new("L", (maxw + 2 * pad_b, H + 2 * pad_b), 0)
    outer = Image.new("L", (maxw + 2 * pad_b, H + 2 * pad_b), 0)
    di = ImageDraw.Draw(inner)
    do = ImageDraw.Draw(outer)

    for i, line in enumerate(lines):
        lw = widths[i]
        x = pad_b + (maxw - lw) * 0.5 if align == "center" else pad_b
        y = pad_b + i * (lh + line_gap_b)
        for ch in line:
            if ch == " ":
                x += font.getlength(ch) + spacing_b
                continue
            di.text((x, y), ch, font=font, fill=255)
            if stroke_b > 0:
                do.text((x, y), ch, font=font, fill=255,
                        stroke_width=stroke_b, stroke_fill=255)
            else:
                do.text((x, y), ch, font=font, fill=255)
            x += font.getlength(ch) + spacing_b

    if abs(slant) > 1e-4:
        k = slant
        extra = int(abs(k) * inner.size[1]) + 4

        def shear(mask):
            wm, hm = mask.size
            return mask.transform((wm + extra, hm), Image.AFFINE,
                                  (1, k, -k * (hm - 1), 0, 1, 0),
                                  resample=Image.BICUBIC)

        inner = shear(inner)
        outer = shear(outer)

    wc, hc = inner.size
    out = Image.new("RGBA", (wc, hc), (0, 0, 0, 0))
    if glow_b > 0:
        blur = outer.filter(ImageFilter.GaussianBlur(glow_b))
        a = blur.point(lambda v: int(v * glow_alpha))
        gl = Image.new("RGBA", (wc, hc), glow_color + (255,))
        gl.putalpha(a)
        out = Image.alpha_composite(out, gl)
    if stroke_b > 0:
        rim = Image.new("RGBA", (wc, hc), stroke_color + (255,))
        rim.putalpha(outer)
        out = Image.alpha_composite(out, rim)
    grad = gradient_img(wc, hc, gradient or CREAM)
    fill = Image.new("RGBA", (wc, hc), (0, 0, 0, 0))
    fill.paste(grad, (0, 0), inner)
    out = Image.alpha_composite(out, fill)

    if alpha < 1.0:
        r, g, b, a = out.split()
        a = a.point(lambda v: int(v * alpha))
        out = Image.merge("RGBA", (r, g, b, a))
    return crop_rgba(out, margin=1)


def save(img, name):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name + ".png")
    img.save(path, "PNG", optimize=True)
    MANIFEST.append((name, img.size))


def super_canvas(w, h, factor=4):
    return Image.new("RGBA", (w * factor, h * factor), (0, 0, 0, 0)), factor


def downscale(img, w, h):
    return img.resize((w, h), Image.LANCZOS)


# --------------------------------------------------------------------------- text
def build_text():
    # brand small caps ------------------------------------------------------
    brand = render_text(["CODE", "CREATE", "BUILD", "TOGETHER"],
                        F_SANS, 17, spacing=6, line_gap=3, align="left",
                        gradient=CREAM_DIM, stroke=1.0, stroke_color=DARK_STROKE_SOFT,
                        glow=6, glow_color=(255, 205, 140), glow_alpha=0.45,
                        pad=14, alpha=0.96)
    save(brand, "ui_brand_words")

    # main title ------------------------------------------------------------
    title = render_text("星火代码", F_TITLE, 120, spacing=4, align="left",
                        gradient=GOLD_BRIGHT, stroke=2.4, stroke_color=(20, 12, 4),
                        glow=16, glow_color=(255, 196, 104), glow_alpha=0.9,
                        pad=26, alpha=1.0)
    save(title, "ui_title_cn")

    title_en = render_text("SparkCode", F_SERIF, 54, weight=620, spacing=1.5,
                           align="left", gradient=GOLD, stroke=1.8,
                           stroke_color=(22, 14, 6), glow=11,
                           glow_color=(255, 198, 108), glow_alpha=0.7,
                           slant=0.20, pad=20, alpha=0.99)
    save(title_en, "ui_title_en")

    subtitle = render_text("SPARK OF CODE · 神殿序章", F_SERIF, 20, weight=500,
                           spacing=3.2, align="left", gradient=CREAM,
                           stroke=0.8, glow=6, glow_color=(255, 208, 140),
                           glow_alpha=0.4, pad=14, alpha=0.92)
    save(subtitle, "ui_subtitle")

    # main menu buttons -----------------------------------------------------
    for name, label in [("ui_btn_start", "开始游戏"), ("ui_btn_settings", "设置"),
                        ("ui_btn_about", "关于"), ("ui_btn_quit", "退出")]:
        btn = render_text(label, F_SERIF, 36, weight=600, spacing=4,
                          align="left", gradient=GOLD_BRIGHT, stroke=1.4,
                          stroke_color=(20, 12, 4), glow=8,
                          glow_color=(255, 200, 112), glow_alpha=0.6,
                          pad=16, alpha=1.0)
        save(btn, name)

    back = render_text("返回", F_SERIF, 32, weight=600, spacing=4,
                       align="center", gradient=GOLD_BRIGHT, stroke=1.4,
                       stroke_color=(20, 12, 4), glow=9,
                       glow_color=(255, 200, 112), glow_alpha=0.6,
                       pad=16, alpha=1.0)
    save(back, "ui_btn_back")

    # bottom hints / version ------------------------------------------------
    save(render_text("点击星火，点燃旅程", F_SERIF, 20, weight=500, spacing=2,
                     align="center", gradient=GOLD_BRIGHT, stroke=1.0,
                     stroke_color=(18, 12, 6), glow=7,
                     glow_color=(255, 198, 108), glow_alpha=0.5,
                     pad=14, alpha=0.95), "ui_hint_click")

    save(render_text("↑↓ 选择 · Enter 确认 · Esc 返回", F_SERIF, 16, weight=400,
                     spacing=1.6, align="center", gradient=CREAM,
                     stroke=0.8, glow=5, glow_color=(255, 210, 150),
                     glow_alpha=0.32, pad=12, alpha=0.8), "ui_hint_keys")

    save(render_text("v0.1.0 · 星火序章", F_SERIF, 15, weight=400, spacing=1.6,
                     align="left", gradient=CREAM_DIM, stroke=0.6,
                     glow=4, glow_color=(255, 210, 150), glow_alpha=0.25,
                     pad=10, alpha=0.7), "ui_version")

    save(render_text("© 2026 SparkCode Team", F_SERIF, 14, weight=400,
                     spacing=1.4, align="right", gradient=CREAM_DIM,
                     stroke=0.6, glow=4, glow_color=(255, 210, 150),
                     glow_alpha=0.22, pad=10, alpha=0.62), "ui_copyright")

    # intro caption ---------------------------------------------------------
    save(render_text("序章 · 星火已点燃", F_TITLE, 46, spacing=3, align="center",
                     gradient=GOLD_BRIGHT, stroke=1.8, stroke_color=(20, 12, 4),
                     glow=16, glow_color=(255, 196, 104), glow_alpha=0.85,
                     pad=24, alpha=1.0), "ui_caption_title")

    save(render_text("按 ESC 返回主菜单", F_SERIF, 20, weight=500, spacing=2.4,
                     align="center", gradient=CREAM, stroke=0.9,
                     glow=7, glow_color=(255, 205, 140), glow_alpha=0.4,
                     pad=14, alpha=0.9), "ui_caption_hint")

    # settings / about ------------------------------------------------------
    save(render_text("设置", F_TITLE, 40, spacing=3, align="left",
                     gradient=GOLD_BRIGHT, stroke=1.6, stroke_color=(20, 12, 4),
                     glow=12, glow_color=(255, 196, 104), glow_alpha=0.7,
                     pad=18), "ui_panel_settings")
    save(render_text("关于", F_TITLE, 40, spacing=3, align="left",
                     gradient=GOLD_BRIGHT, stroke=1.6, stroke_color=(20, 12, 4),
                     glow=12, glow_color=(255, 196, 104), glow_alpha=0.7,
                     pad=18), "ui_panel_about")

    for name, label in [("ui_label_quality", "画质"),
                        ("ui_label_volume", "音量"),
                        ("ui_label_fullscreen", "全屏")]:
        save(render_text(label, F_SERIF, 23, weight=500, spacing=3, align="left",
                         gradient=CREAM, stroke=0.8,
                         glow=5, glow_color=(255, 205, 140), glow_alpha=0.35,
                         pad=12, alpha=0.94), name)

    for name, label in [("ui_val_low", "低"), ("ui_val_mid", "中"),
                        ("ui_val_high", "高"), ("ui_val_on", "开"),
                        ("ui_val_off", "关")]:
        save(render_text(label, F_SERIF, 22, weight=600, spacing=2, align="center",
                         gradient=GOLD_BRIGHT, stroke=0.9,
                         glow=6, glow_color=(255, 200, 112), glow_alpha=0.45,
                         pad=12), name)

    for pct in (0, 25, 50, 75, 100):
        save(render_text("%d%%" % pct, F_SERIF, 22, weight=600, spacing=1.2,
                         align="center", gradient=GOLD_BRIGHT, stroke=0.9,
                         glow=6, glow_color=(255, 200, 112), glow_alpha=0.45,
                         pad=11), "ui_val_vol%d" % pct)

    # about panel
    save(render_text("星火代码", F_TITLE, 30, spacing=3, align="left",
                     gradient=GOLD_BRIGHT, stroke=1.2, stroke_color=(20, 12, 4),
                     glow=10, glow_color=(255, 196, 104), glow_alpha=0.6,
                     pad=14), "ui_about_name")
    save(render_text("SparkCode", F_SERIF, 24, weight=600, spacing=1.2,
                     align="left", gradient=GOLD, stroke=0.9,
                     glow=7, glow_color=(255, 198, 108), glow_alpha=0.5,
                     slant=0.20, pad=12, alpha=0.95), "ui_about_name_en")
    save(render_text(["以代码点燃星火，以创意筑起神殿。",
                      "光与影之间，写下属于你的第一行史诗。",
                      "每一次构建，都是一次新的启程。"],
                     F_SERIF, 18, weight=400, spacing=0.6, line_gap=10,
                     align="left", gradient=CREAM, stroke=0.7,
                     glow=5, glow_color=(255, 205, 140), glow_alpha=0.3,
                     pad=13, alpha=0.88), "ui_about_body")
    save(render_text("制作　SparkCode Team", F_SERIF, 17, weight=500,
                     spacing=1.2, align="left", gradient=CREAM_DIM,
                     stroke=0.7, glow=4, glow_color=(255, 205, 140),
                     glow_alpha=0.3, pad=10, alpha=0.85), "ui_about_team")
    save(render_text("引擎　Unity URP · 2026", F_SERIF, 16, weight=400,
                     spacing=1.2, align="left", gradient=CREAM_DIM,
                     stroke=0.7, glow=4, glow_color=(255, 205, 140),
                     glow_alpha=0.28, pad=10, alpha=0.75), "ui_about_engine")
    save(render_text("版本　v0.1.0 · 星火序章", F_SERIF, 16, weight=400,
                     spacing=1.2, align="left", gradient=CREAM_DIM,
                     stroke=0.6, glow=4, glow_color=(255, 205, 140),
                     glow_alpha=0.25, pad=10, alpha=0.7), "ui_about_version")


# ---------------------------------------------------------------------- decor
def build_corner():
    size = 220
    img, f = super_canvas(size, size, 4)
    d = ImageDraw.Draw(img)
    gold = (236, 202, 138, 235)
    gold_soft = (214, 176, 112, 130)

    d.line([(10 * f, 12 * f), (150 * f, 12 * f)], fill=gold, width=int(2.2 * f))
    d.line([(12 * f, 10 * f), (12 * f, 150 * f)], fill=gold, width=int(2.2 * f))
    d.line([(34 * f, 40 * f), (122 * f, 40 * f)], fill=gold_soft, width=int(1.2 * f))
    d.line([(40 * f, 34 * f), (40 * f, 122 * f)], fill=gold_soft, width=int(1.2 * f))

    cx, cy, r = 13 * f, 13 * f, 8 * f
    d.polygon([(cx, cy - r), (cx + r * 0.62, cy), (cx, cy + r), (cx - r * 0.62, cy)],
              fill=(255, 232, 176, 240))
    for (dx, dy) in ((150 * f, 12 * f), (12 * f, 150 * f)):
        rr = 3.2 * f
        d.ellipse([dx - rr, dy - rr, dx + rr, dy + rr], fill=(245, 210, 146, 210))
    save(downscale(img, size, size), "ui_corner")


def build_frame(name, hover=False):
    size = 128
    f = 4
    img, _ = super_canvas(size, size, f)
    W = size * f
    d = ImageDraw.Draw(img)
    inset = int(1.4 * f)
    radius = int(6 * f)

    if hover:
        top = (78, 56, 22, 92)
        bot = (34, 24, 10, 120)
    else:
        top = (14, 16, 19, 105)
        bot = (8, 9, 11, 150)
    fill = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    grad = gradient_img(W, W, [(0.0, top), (1.0, bot)])
    mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [inset, inset, W - inset, W - inset], radius=radius, fill=255)
    fill.paste(grad, (0, 0), mask)
    img = Image.alpha_composite(img, fill)
    d = ImageDraw.Draw(img)

    if hover:
        border = (255, 226, 160, 245)
        inner = (255, 208, 128, 120)
    else:
        border = (219, 181, 116, 205)
        inner = (156, 124, 72, 105)
    d.rounded_rectangle([inset, inset, W - inset, W - inset], radius=radius,
                        outline=border, width=int(2.0 * f))
    d.rounded_rectangle([inset + 6 * f, inset + 6 * f, W - inset - 6 * f,
                         W - inset - 6 * f], radius=max(2, radius - 3 * f),
                        outline=inner, width=int(0.9 * f))

    tick = 13 * f
    tick_c = (255, 236, 186, 235) if hover else (232, 198, 136, 185)
    for (x, y, sx, sy) in ((inset + 3 * f, inset + 3 * f, 1, 1),
                           (W - inset - 3 * f, inset + 3 * f, -1, 1),
                           (inset + 3 * f, W - inset - 3 * f, 1, -1),
                           (W - inset - 3 * f, W - inset - 3 * f, -1, -1)):
        d.line([(x, y), (x + sx * tick, y)], fill=tick_c, width=int(1.6 * f))
        d.line([(x, y), (x, y + sy * tick)], fill=tick_c, width=int(1.6 * f))
        r = 2.2 * f
        d.polygon([(x, y - r), (x + r, y), (x, y + r), (x - r, y)], fill=tick_c)

    if hover:
        glow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
        gd = ImageDraw.Draw(glow)
        gd.rounded_rectangle([inset + 2 * f, inset + 2 * f, W - inset - 2 * f,
                              W - inset - 2 * f], radius=radius,
                             outline=(255, 196, 104, 120), width=int(5 * f))
        glow = glow.filter(ImageFilter.GaussianBlur(4 * f))
        img = Image.alpha_composite(img, glow)

    save(downscale(img, size, size), name)


def build_panel():
    size = 240
    f = 4
    img, _ = super_canvas(size, size, f)
    W = size * f
    d = ImageDraw.Draw(img)
    inset = int(1.5 * f)
    radius = int(7 * f)

    mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [inset, inset, W - inset, W - inset], radius=radius, fill=255)
    grad = gradient_img(W, W, [(0.0, (17, 19, 23, 210)),
                               (0.45, (10, 12, 15, 220)),
                               (1.0, (6, 7, 9, 232))])
    fill = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    fill.paste(grad, (0, 0), mask)
    img = Image.alpha_composite(img, fill)
    d = ImageDraw.Draw(img)

    d.rounded_rectangle([inset, inset, W - inset, W - inset], radius=radius,
                        outline=(212, 174, 106, 225), width=int(2.0 * f))
    d.rounded_rectangle([inset + 7 * f, inset + 7 * f, W - inset - 7 * f,
                         W - inset - 7 * f], radius=max(2, radius - 3 * f),
                        outline=(140, 110, 64, 120), width=int(0.9 * f))

    wash = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    wd = ImageDraw.Draw(wash)
    wd.ellipse([-W * 0.4, -W * 0.55, W * 1.4, W * 0.30],
               fill=(96, 68, 30, 32))
    wash = wash.filter(ImageFilter.GaussianBlur(18 * f))
    wash.putalpha(Image.composite(wash.split()[3], Image.new("L", (W, W), 0), mask))
    img = Image.alpha_composite(img, wash)

    cc = (232, 198, 136, 200)
    for (x, y, sx, sy) in ((inset + 5 * f, inset + 5 * f, 1, 1),
                           (W - inset - 5 * f, inset + 5 * f, -1, 1),
                           (inset + 5 * f, W - inset - 5 * f, 1, -1),
                           (W - inset - 5 * f, W - inset - 5 * f, -1, -1)):
        d.line([(x, y), (x + sx * 26 * f, y)], fill=cc, width=int(1.5 * f))
        d.line([(x, y), (x, y + sy * 26 * f)], fill=cc, width=int(1.5 * f))
        r = 3.0 * f
        d.polygon([(x, y - r), (x + r, y), (x, y + r), (x - r, y)],
                  fill=(252, 228, 174, 230))
    save(downscale(img, size, size), "ui_panel_bg")


def build_row_hover():
    size = (160, 80)
    f = 4
    w, h = size[0] * f, size[1] * f
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    for x in range(w):
        t = x / max(1, w - 1)
        a = int(58 * (1.0 - t) ** 1.4)
        ImageDraw.Draw(img).line([(x, 0), (x, h)], fill=(150, 112, 54, a))
    d = ImageDraw.Draw(img)
    d.line([(6 * f, h - 2 * f), (w - 6 * f, h - 2 * f)],
           fill=(224, 184, 116, 120), width=int(1.2 * f))
    save(downscale(img, size[0], size[1]), "ui_row_hover")


def build_shades():
    w = h = 512

    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    px = img.load()
    for x in range(w):
        t = x / (w - 1)
        a = int(168 * (1.0 - t) ** 1.55)
        for y in range(h):
            px[x, y] = (3, 5, 8, a)
    save(img, "ui_shade_left")

    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    px = img.load()
    for y in range(h):
        t = y / (h - 1)
        a = int(150 * t ** 1.6)
        for x in range(w):
            px[x, y] = (4, 5, 8, a)
    save(img, "ui_shade_bottom")

    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    px = img.load()
    for y in range(h):
        t = y / (h - 1)
        a = int(110 * (1.0 - t) ** 1.8)
        for x in range(w):
            px[x, y] = (4, 6, 9, a)
    save(img, "ui_shade_top")


def build_fourpoint():
    size = 256
    f = 4
    img, _ = super_canvas(size, size, f)
    W = size * f
    cx = cy = W / 2
    r = W * 0.46
    waist = r * 0.115
    pts = [(cx, cy - r), (cx + waist, cy - waist), (cx + r, cy),
           (cx + waist, cy + waist), (cx, cy + r), (cx - waist, cy + waist),
           (cx - r, cy), (cx - waist, cy - waist)]
    glow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    ImageDraw.Draw(glow).polygon(pts, fill=(255, 196, 104, 210))
    glow = glow.filter(ImageFilter.GaussianBlur(16 * f))
    img = Image.alpha_composite(img, glow)

    grad = gradient_img(W, W, GOLD_BRIGHT)
    body = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(mask).polygon(pts, fill=255)
    body.paste(grad, (0, 0), mask)
    img = Image.alpha_composite(img, body)

    d = ImageDraw.Draw(img)
    cr = W * 0.045
    d.ellipse([cx - cr, cy - cr, cx + cr, cy + cr], fill=(255, 255, 245, 255))
    save(downscale(img, size, size), "ui_fourpoint")


def build_glow(name, size, tint, power=2.2, core=0.16):
    f = 2
    W = size * f
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    px = img.load()
    cx = cy = (W - 1) / 2.0
    maxd = W / 2.0
    for y in range(W):
        for x in range(W):
            d = math.hypot(x - cx, y - cy) / maxd
            if d >= 1.0:
                continue
            v = min(1.0, (1.0 - d) ** power * 1.15)
            px[x, y] = (tint[0], tint[1], tint[2], int(255 * v))
    if core > 0:
        ImageDraw.Draw(img).ellipse(
            [cx - maxd * core, cy - maxd * core, cx + maxd * core, cy + maxd * core],
            fill=(255, 255, 250, 210))
    save(downscale(img, size, size), name)


def build_mote():
    size = 64
    f = 2
    W = size * f
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    px = img.load()
    cx = cy = (W - 1) / 2.0
    maxd = W / 2.0
    for y in range(W):
        for x in range(W):
            d = math.hypot(x - cx, y - cy) / maxd
            if d >= 1.0:
                continue
            px[x, y] = (255, 238, 196, int(255 * (1.0 - d) ** 2.6))
    save(downscale(img, size, size), "ui_mote")


def build_divider(name, width, height, diamond_center=True, fade_right=False,
                  diamond_left=False):
    f = 3
    w, h = width * f, height * f
    line = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(line)
    y = h / 2
    d.line([(0, y), (w, y)], fill=(228, 192, 126, 235), width=int(1.6 * f))
    alpha = Image.new("L", (w, 1), 0)
    ap = alpha.load()
    for x in range(w):
        t = x / (w - 1)
        edge = min(1.0, t / 0.12) * min(1.0, (1.0 - t) / 0.12)
        if fade_right:
            edge = min(1.0, (1.0 - t) / 0.45) * min(1.0, t / 0.03)
        ap[x, 0] = int(255 * max(0.0, edge) ** 1.1)
    alpha = alpha.resize((w, h), Image.BILINEAR)
    line.putalpha(Image.composite(alpha, Image.new("L", (w, h), 0), line.split()[3]))
    img = Image.alpha_composite(Image.new("RGBA", (w, h), (0, 0, 0, 0)), line)
    d = ImageDraw.Draw(img)
    marks = []
    if diamond_center:
        marks.append((w / 2, h / 2))
    if diamond_left:
        marks.append((10 * f, h / 2))
    for cx, cy in marks:
        r = 5.0 * f
        d.polygon([(cx, cy - r), (cx + r * 0.6, cy), (cx, cy + r), (cx - r * 0.6, cy)],
                  fill=(255, 236, 190, 245))
    save(downscale(img, width, height), name)


def build_chevron():
    size = (48, 72)
    f = 4
    w, h = size[0] * f, size[1] * f
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    pts = [(w * 0.22, h * 0.18), (w * 0.72, h * 0.5), (w * 0.22, h * 0.82)]
    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(glow).line(pts, fill=(255, 200, 116, 170),
                              width=int(3.4 * f), joint="curve")
    glow = glow.filter(ImageFilter.GaussianBlur(3 * f))
    img = Image.alpha_composite(img, glow)
    ImageDraw.Draw(img).line(pts, fill=(255, 236, 190, 240),
                             width=int(2.2 * f), joint="curve")
    save(downscale(img, size[0], size[1]), "ui_chevron")


def build_halo_bar():
    w, h = 512, 96
    f = 2
    W, H = w * f, h * f
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    px = img.load()
    for y in range(H):
        ty = abs((y / (H - 1)) * 2.0 - 1.0)
        for x in range(W):
            tx = abs((x / (W - 1)) * 2.0 - 1.0)
            v = max(0.0, 1.0 - (tx ** 1.8 + ty ** 2.6) ** 0.75)
            px[x, y] = (255, 206, 128, int(170 * v ** 1.7))
    save(downscale(img, w, h), "ui_halo_bar")


def main():
    os.makedirs(OUT, exist_ok=True)
    build_text()

    Image.new("RGBA", (8, 8), (236, 202, 138, 255)).save(
        os.path.join(OUT, "ui_line.png"))
    MANIFEST.append(("ui_line", (8, 8)))

    build_corner()
    build_frame("ui_frame_normal", hover=False)
    build_frame("ui_frame_hover", hover=True)
    build_panel()
    build_row_hover()
    build_shades()
    build_fourpoint()
    build_glow("ui_glow", 256, (255, 214, 150), power=2.1, core=0.10)
    build_glow("ui_glow_soft", 256, (255, 228, 186), power=2.9, core=0.0)
    build_mote()
    build_divider("ui_divider", 480, 20, diamond_center=True)
    build_divider("ui_underline", 400, 20, diamond_center=False, diamond_left=True,
                  fade_right=True)
    build_chevron()
    build_halo_bar()

    print("Baked %d UI textures -> %s" % (len(MANIFEST), OUT))
    for name, size in MANIFEST:
        print("  %-24s %dx%d" % (name + ".png", size[0], size[1]))


if __name__ == "__main__":
    main()
