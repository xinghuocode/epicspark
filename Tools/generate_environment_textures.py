"""Generates tileable procedural textures for the SparkCode temple scene.

Run: <bundled python> Tools/generate_environment_textures.py
Outputs PNGs into Assets/Art/Textures (Unity imports them; TempleBuildKit sets import settings).
"""
import os
import numpy as np
from PIL import Image

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Assets", "Art", "Textures")
os.makedirs(OUT, exist_ok=True)
rng = np.random.default_rng(20261106)


def smoothstep(t):
    return t * t * (3.0 - 2.0 * t)


def value_noise(res, freq, seed):
    """Tileable smooth value noise with `freq` lattice cells per side."""
    r = np.random.default_rng(seed)
    grid = r.random((freq, freq)).astype(np.float32)
    coords = np.arange(res, dtype=np.float32) / res * freq
    x0 = np.floor(coords).astype(np.int32) % freq
    x1 = (x0 + 1) % freq
    fx = smoothstep(coords - np.floor(coords)).astype(np.float32)
    # rows/cols sampling
    a = grid[np.ix_(x0, x0)]
    b = grid[np.ix_(x0, x1)]
    c = grid[np.ix_(x1, x0)]
    d = grid[np.ix_(x1, x1)]
    top = a + (b - a) * fx[None, :]
    bot = c + (d - c) * fx[None, :]
    return top + (bot - top) * fx[:, None]


def fbm(res, base_freq=4, octaves=5, seed=0, persistence=0.5):
    total = np.zeros((res, res), np.float32)
    amp = 1.0
    norm = 0.0
    for o in range(octaves):
        freq = base_freq * (2 ** o)
        total += amp * value_noise(res, freq, seed + o * 131)
        norm += amp
        amp *= persistence
    return total / norm


def warp(noise, amount, res):
    """Simple domain warp with d/dx of another noise field."""
    gy, gx = np.gradient(noise)
    return amount * gx, amount * gy


def gradient_normal(height, strength=6.0):
    gy, gx = np.gradient(height.astype(np.float32))
    nx = -gx * strength
    ny = -gy * strength
    nz = np.ones_like(nx)
    length = np.sqrt(nx * nx + ny * ny + nz * nz)
    nx /= length
    ny /= length
    nz /= length
    rgb = np.stack([nx * 0.5 + 0.5, ny * 0.5 + 0.5, nz * 0.5 + 0.5], axis=-1)
    return np.clip(rgb, 0, 1)


def save(name, arr):
    data = (np.clip(arr, 0, 1) * 255.0 + 0.5).astype(np.uint8)
    Image.fromarray(data).save(os.path.join(OUT, name), optimize=True)
    print("wrote", name, data.shape)


def make_marble(size=2048):
    base = fbm(size, 5, 5, seed=11)
    turb = fbm(size, 3, 6, seed=23)
    x = np.linspace(0, 1, size, endpoint=False, dtype=np.float32)[None, :]
    y = np.linspace(0, 1, size, endpoint=False, dtype=np.float32)[:, None]
    warp_x, warp_y = warp(turb, 0.055, size) if False else (0, 0)
    # domain warp using fbm derivatives
    gy, gx = np.gradient(fbm(size, 4, 4, seed=31))
    wx = x + gx * 0.45
    wy = y + gy * 0.45
    phase = (wx * 7.0 + wy * 5.0 + base * 2.4) * np.pi * 2.0
    folds = np.abs(np.sin(phase))
    vein = np.power(1.0 - folds, 6.0)
    micro = fbm(size, 26, 3, seed=47)
    fine = np.power(1.0 - np.abs(np.sin((wx * 31.0 + wy * 19.0 + micro * 1.7) * np.pi)), 12.0)

    lum = 0.945 + 0.035 * base + 0.018 * micro
    vein_mask = np.clip(vein * 0.55 + fine * 0.20, 0, 1)

    albedo = np.zeros((size, size, 3), np.float32)
    albedo[..., 0] = lum - vein_mask * 0.10
    albedo[..., 1] = lum - vein_mask * 0.115
    albedo[..., 2] = lum - vein_mask * 0.085
    # faint warm gold threads
    gold = np.clip(vein * 0.10 * (0.5 + 0.5 * np.sin(base * 30)), 0, 1)[..., None]
    albedo = albedo * (1.0 - gold) + np.array([0.78, 0.66, 0.47], np.float32) * gold
    # speckle
    speck = rng.random((size, size)).astype(np.float32)
    speck = (speck > 0.99965).astype(np.float32) * 0.20
    albedo -= speck[..., None]

    height = base * 0.25 - vein * 0.55 - fine * 0.25
    save("MarbleWhite_Albedo.png", albedo)
    save("MarbleWhite_Normal.png", gradient_normal(height, 2.6))
    return albedo, height


def make_floor(size=2048, tiles=4):
    tile_px = size // tiles
    albedo = np.zeros((size, size, 3), np.float32)
    height = np.zeros((size, size), np.float32)

    base = fbm(size, 5, 5, seed=61)
    micro = fbm(size, 24, 3, seed=67)
    gy, gx = np.gradient(fbm(size, 3, 4, seed=71))
    x = np.linspace(0, 1, size, endpoint=False, dtype=np.float32)[None, :]
    y = np.linspace(0, 1, size, endpoint=False, dtype=np.float32)[:, None]
    phase = (x * 6.0 + y * 8.0 + gx * 0.4 + base * 2.0) * np.pi * 2.0
    vein = np.power(1.0 - np.abs(np.sin(phase)), 8.0)
    vein_mask = np.clip(vein * 0.45 + base * 0.06, 0, 1)

    for ty in range(tiles):
        for tx in range(tiles):
            h = (tx * 73856093 ^ ty * 19349663) & 0xFFFF
            tone = 0.955 + ((h % 100) / 100.0 - 0.5) * 0.035
            rot = ((h % 7) / 7.0) * np.pi
            xs = slice(tx * tile_px, (tx + 1) * tile_px)
            ys = slice(ty * tile_px, (ty + 1) * tile_px)
            v = np.roll(vein_mask, int(rot * 40), axis=1)[ys, xs]
            m = micro[ys, xs]
            base_tile = tone + 0.02 * (m - 0.5)
            tile = np.zeros((tile_px, tile_px, 3), np.float32)
            tile[..., 0] = base_tile - v * 0.085
            tile[..., 1] = base_tile - v * 0.095
            tile[..., 2] = base_tile - v * 0.075
            albedo[ys, xs] = tile

            # bevel + grout mask inside tile
            yy, xx = np.mgrid[0:tile_px, 0:tile_px].astype(np.float32)
            edge = np.minimum.reduce([xx, yy, tile_px - 1 - xx, tile_px - 1 - yy])
            grout_w = 5.0
            bevel_w = 16.0
            groove = np.clip((grout_w - edge) / grout_w, 0, 1)
            bevel = np.clip((bevel_w - edge) / bevel_w, 0, 1)
            height[ys, xs] = (1.0 - groove * 1.4) * (0.65 + 0.35 * (1.0 - bevel)) + 0.02 * m
            grout = groove[..., None] * 0.9
            albedo[ys, xs] = albedo[ys, xs] * (1.0 - grout * 0.45) + np.array([0.30, 0.28, 0.25]) * grout * 0.45
            # edge highlight
            lip = (np.abs(edge - bevel_w) < 1.5).astype(np.float32) * 0.06
            albedo[ys, xs] += lip[..., None]

    save("FloorTiles_Albedo.png", albedo)
    save("FloorTiles_Normal.png", gradient_normal(height, 5.5))
    return albedo


def make_water_normal(size=1024):
    n1 = fbm(size, 6, 5, seed=101)
    n2 = fbm(size, 14, 4, seed=113)
    x = np.linspace(0, 2 * np.pi, size, endpoint=False, dtype=np.float32)[None, :]
    y = np.linspace(0, 2 * np.pi, size, endpoint=False, dtype=np.float32)[:, None]
    ripples = 0.5 * np.sin(x * 9 + n1 * 7.0) + 0.35 * np.sin(y * 13 - n2 * 5.0) + 0.22 * np.sin((x + y) * 21 + n1 * 4.0)
    height = n1 * 0.6 + n2 * 0.25 + ripples * 0.18
    save("Water_Normal.png", gradient_normal(height, 5.0))


def radial(size):
    y, x = np.mgrid[0:size, 0:size].astype(np.float32)
    cx = cy = (size - 1) * 0.5
    dx = (x - cx) / cx
    dy = (y - cy) / cy
    r = np.sqrt(dx * dx + dy * dy)
    return dx, dy, r


def make_spark_halo(size=1024):
    dx, dy, r = radial(size)
    angle = np.arctan2(dy, dx)
    glow = np.exp(-np.power(np.clip(r, 0, 4), 1.15) * 5.2)
    core = np.exp(-np.power(np.clip(r, 0, 4), 2.0) * 34.0)
    ring = np.exp(-np.power(np.abs(r - 0.20) * 12.0, 2.0)) * 0.35
    spikes = np.zeros_like(r)
    for k in range(4):
        a = angle - k * np.pi / 2.0
        a = (a + np.pi) % (2 * np.pi) - np.pi
        spikes += np.exp(-np.power(a * 34.0, 2.0)) * np.exp(-np.clip(r, 0, 4) * 3.2)
    spikes += 0.4 * np.exp(-np.power(np.abs(angle) * 45.0, 2.0)) * np.exp(-r * 4.0)
    a = np.clip(glow * 0.75 + core * 1.1 + ring + spikes * 0.85, 0, 1)
    a = np.power(a, 1.05)
    rgb = np.zeros((size, size, 4), np.float32)
    warm = np.array([1.0, 0.76, 0.36])
    hot = np.array([1.0, 0.95, 0.82])
    mix = np.clip(core * 1.4 + spikes * 0.5, 0, 1)[..., None]
    rgb[..., :3] = warm[None, None, :] * (1 - mix) + hot[None, None, :] * mix
    rgb[..., 3] = a
    save("SparkHalo.png", rgb)


def make_spark_mote(size=256):
    dx, dy, r = radial(size)
    r = np.clip(r, 0, 2)
    glow = np.exp(-r * r * 22.0) + 0.45 * np.exp(-r * r * 5.0)
    angle = np.arctan2(dy, dx)
    spikes = 0.0
    for k in range(4):
        a = angle - k * np.pi / 2.0
        a = (a + np.pi) % (2 * np.pi) - np.pi
        spikes = spikes + np.exp(-np.power(a * 26.0, 2.0)) * np.exp(-r * 9.0)
    a = np.clip(glow + spikes * 0.7, 0, 1)
    rgba = np.zeros((size, size, 4), np.float32)
    rgba[..., 0] = 1.0
    rgba[..., 1] = 0.82
    rgba[..., 2] = 0.5
    rgba[..., 3] = a
    save("SparkMote.png", rgba)


def make_soft_glow(size=512):
    dx, dy, r = radial(size)
    a = np.exp(-np.power(np.clip(r, 0, 2), 1.8) * 6.0)
    rgba = np.zeros((size, size, 4), np.float32)
    rgba[..., :3] = 1.0
    rgba[..., 3] = a
    save("SoftGlow.png", rgba)


def make_spark_surface(size=1024):
    base = fbm(size, 7, 5, seed=201)
    fine = fbm(size, 28, 4, seed=211)
    x = np.linspace(0, 1, size, endpoint=False, dtype=np.float32)[None, :]
    y = np.linspace(0, 1, size, endpoint=False, dtype=np.float32)[:, None]
    cells = np.sin((x * 37 + fine * 3.0) * np.pi) * np.sin((y * 31 - fine * 2.0) * np.pi)
    mott = np.clip(base * 0.7 + 0.3 * (0.5 + 0.5 * cells), 0, 1)
    gold = np.array([1.0, 0.70, 0.25])
    deep = np.array([0.72, 0.38, 0.08])
    albedo = deep[None, None, :] + (gold - deep)[None, None, :] * mott[..., None]
    albedo = albedo * (0.75 + 0.35 * fine[..., None])
    save("SparkSurface_Albedo.png", albedo)

    r = rng.random((size, size)).astype(np.float32)
    em = np.zeros((size, size, 3), np.float32)
    # hot mottled patches
    patch = np.clip((fine - 0.48) * 3.0, 0, 1)
    em[..., 0] = 0.55 * patch + 0.45 * (r > 0.999).astype(np.float32)
    em[..., 1] = 0.38 * patch + 0.42 * (r > 0.999).astype(np.float32)
    em[..., 2] = 0.12 * patch + 0.25 * (r > 0.999).astype(np.float32)
    # sparse bright pin lights
    pin = (r > 0.99965).astype(np.float32)
    em += pin[..., None] * np.array([1.0, 0.85, 0.55])
    save("SparkSurface_Emissive.png", np.clip(em, 0, 1))


if __name__ == "__main__":
    make_marble()
    make_floor()
    make_water_normal()
    make_spark_halo()
    make_spark_mote()
    make_soft_glow()
    make_spark_surface()
    print("done ->", OUT)
