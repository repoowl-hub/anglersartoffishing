"""Generates the placeholder 16x16 pixel-art item icons for Angler's Art of Fishing (pure stdlib)."""
import os, zlib, struct

OUT = os.path.join(os.path.dirname(__file__), "assets", "anglersartoffishing", "textures", "item")
os.makedirs(OUT, exist_ok=True)


def write_png(path, pixels, w=16, h=16):
    raw = b"".join(b"\x00" + b"".join(bytes(pixels[y][x]) for x in range(w)) for y in range(h))
    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def blank():
    return [[(0, 0, 0, 0) for _ in range(16)] for _ in range(16)]


def rod(shaft, accent, reel):
    p = blank()
    # diagonal shaft from bottom-left to top-right
    for i in range(14):
        x, y = 1 + i, 14 - i
        p[y][x] = shaft
        if i % 4 == 2:
            p[y][x] = accent
    # line hanging from the tip
    for y in range(2, 8):
        p[y][14] = (225, 225, 225, 255)
    p[8][14] = (200, 60, 60, 255)
    # reel
    for dx, dy in ((4, 9), (5, 9), (4, 10), (5, 10)):
        p[dy][dx] = reel
    return p


def worm():
    p = blank()
    body, dark = (214, 120, 120, 255), (160, 80, 85, 255)
    pts = [(3, 10), (4, 9), (5, 9), (6, 10), (7, 11), (8, 11), (9, 10), (10, 9), (11, 9), (12, 10)]
    for i, (x, y) in enumerate(pts):
        p[y][x] = body if i % 2 == 0 else dark
    p[10][12] = (60, 30, 30, 255)
    return p


def grain():
    p = blank()
    for cx, cy in ((5, 6), (9, 5), (7, 9), (11, 9), (4, 11)):
        for dx, dy in ((0, 0), (0, 1), (1, 0), (1, 1)):
            p[cy + dy][cx + dx] = (225, 190, 90, 255) if (dx + dy) % 2 == 0 else (190, 150, 60, 255)
    return p


def fishscrap():
    p = blank()
    for x in range(3, 13):
        for y in range(6, 11):
            if (x - 8) ** 2 / 25 + (y - 8) ** 2 / 6 <= 1:
                p[y][x] = (205, 110, 100, 255) if (x + y) % 3 else (230, 150, 135, 255)
    for y in range(7, 10):
        p[y][13] = (205, 110, 100, 255)
    p[7][5] = (30, 30, 30, 255)
    return p


def spinner():
    p = blank()
    metal, shine = (190, 195, 205, 255), (240, 245, 255, 255)
    for x, y in ((8, 3), (8, 4), (8, 5)):
        p[y][x] = (120, 120, 130, 255)
    for x in range(5, 12):
        for y in range(6, 10):
            if (x - 8) ** 2 / 9 + (y - 8) ** 2 / 3 <= 1:
                p[y][x] = metal
    p[7][7] = shine
    # hook
    for x, y in ((8, 10), (8, 11), (8, 12), (9, 13), (10, 12)):
        p[y][x] = (150, 150, 160, 255)
    return p


write_png(os.path.join(OUT, "rod-crude.png"), rod((140, 105, 65, 255), (95, 70, 40, 255), (90, 90, 95, 255)))
write_png(os.path.join(OUT, "rod-sturdy.png"), rod((105, 75, 45, 255), (200, 125, 70, 255), (190, 120, 70, 255)))
write_png(os.path.join(OUT, "rod-master.png"), rod((60, 45, 35, 255), (200, 205, 215, 255), (225, 195, 90, 255)))
write_png(os.path.join(OUT, "bait-worm.png"), worm())
write_png(os.path.join(OUT, "bait-grain.png"), grain())
write_png(os.path.join(OUT, "bait-fishscrap.png"), fishscrap())
write_png(os.path.join(OUT, "bait-spinner.png"), spinner())
print("done")
