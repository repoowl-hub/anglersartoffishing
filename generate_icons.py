"""Generates the placeholder 16x16 pixel-art item icon for Angler's Art of Fishing (pure stdlib)."""
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


write_png(os.path.join(OUT, "bait-spinner.png"), spinner())
print("done")
