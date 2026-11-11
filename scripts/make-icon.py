"""Generate the original Region Launcher icon at Windows tray and shell sizes."""
from io import BytesIO
from pathlib import Path
import struct
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def draw_icon(size):
    scale = 4
    image = Image.new('RGBA', (size * scale, size * scale))
    draw = ImageDraw.Draw(image)
    unit = size * scale / 64
    def box(values): return tuple(round(v * unit) for v in values)
    def line(points, color, width=3):
        draw.line([box(p) for p in points], fill=color, width=max(scale, round(width * unit)), joint='curve')
    navy, mint, white = '#123F4B', '#62E6CF', '#F7FFFE'
    draw.rounded_rectangle(box((2, 2, 62, 62)), radius=round(16 * unit), fill=navy)
    stroke = max(scale, round(3 * unit))
    draw.ellipse(box((12, 16, 48, 52)), outline=white, width=stroke)
    draw.ellipse(box((23, 16, 37, 52)), outline=white, width=stroke)
    line(((13, 34), (47, 34)), white)
    # Separate the launch arrow from the globe at small tray sizes.
    line(((34, 30), (51, 13)), navy, 10)
    line(((38, 13), (51, 13), (51, 26)), navy, 10)
    line(((34, 30), (51, 13)), mint, 5)
    line(((38, 13), (51, 13), (51, 26)), mint, 5)
    return image.resize((size, size), Image.Resampling.LANCZOS)


def main():
    assets = ROOT / 'src/assets'; assets.mkdir(exist_ok=True)
    images = [draw_icon(size) for size in SIZES]
    blobs = []
    for image in images:
        output = BytesIO(); image.save(output, format='PNG'); blobs.append(output.getvalue())
    offset = 6 + 16 * len(SIZES)
    entries = []
    for size, blob in zip(SIZES, blobs):
        entries.append(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    (assets / 'RegionLauncher.ico').write_bytes(struct.pack('<HHH', 0, 1, len(SIZES)) + b''.join(entries) + b''.join(blobs))
    preview = Image.new('RGB', (560, 240), '#F1F5F6')
    draw = ImageDraw.Draw(preview); draw.rectangle((0, 120, 560, 240), fill='#171D24')
    for y in (0, 120):
        for x, size in zip((24, 100, 190, 300, 430), (16, 24, 32, 48, 80)):
            icon = draw_icon(size); preview.paste(icon, (x, y + (120-size)//2), icon)
    target = ROOT / 'build/test-results/app-icon'; target.mkdir(parents=True, exist_ok=True)
    preview.save(target / 'preview.png')
    with Image.open(assets / 'RegionLauncher.ico') as icon:
        assert icon.ico.sizes() == {(n, n) for n in SIZES}
        for size in SIZES: assert icon.ico.getimage((size, size)).getbbox()
    print('PASS: nine original icon sizes, 16 through 256 pixels')


if __name__ == '__main__': main()
