"""Render the server's light-masked Tile rows as a small indexed PNG."""
from struct import pack
import zlib


# Terrain classes originate in ServerTileLighting.TerrainGlyph. @ is a local overlay.
PALETTE = {
    '?': '16202e', '.': 'fff1cc', '#': '808b96', '=': 'ae7746',
    '~': '63bce0', 'L': 'f25525', 'H': 'e99b22', 'S': 'bc85e1',
    't': 'ffcc28', 'c': '9f5425', 'o': '58a184', '@': 'e73248',
}
LEGEND = ('红点是角色，上方通向地表，下方更深。深蓝为暗区，米色为空地，灰色为实心，棕色为平台，'
          '蓝色为水，橙红为岩浆，橙黄为蜂蜜，紫色为微光，亮黄为火把，深棕为箱子，绿色为矿石。')


def terrain_png(view):
    width, height = view['width'], view['height']
    rows = view['rows']
    if not (1 <= width <= 119 and 1 <= height <= 67 and len(rows) == height
            and all(len(row) == width for row in rows)):
        raise ValueError('Invalid terrain viewport dimensions')
    x = view['actor']['x'] - view['originX']
    y = view['actor']['y'] - view['originY']
    if not (0 <= x < width and 0 <= y < height):
        raise ValueError('Actor outside terrain viewport')
    scale = 5
    indices = {symbol: bytes([i]) * scale for i, symbol in enumerate(PALETTE)}
    pixels = bytearray()
    for j, row in enumerate(rows):
        if j == y:
            row = row[:x] + '@' + row[x + 1:]
        scanline = b'\0' + b''.join(indices[cell] for cell in row)
        pixels.extend(scanline * scale)

    def chunk(kind, payload):
        return pack('>I', len(payload)) + kind + payload + pack('>I', zlib.crc32(kind + payload))

    header = pack('>2I5B', width * scale, height * scale, 8, 3, 0, 0, 0)
    palette = bytes.fromhex(''.join(PALETTE.values()))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', header) + chunk(b'PLTE', palette)
            + chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b''))
