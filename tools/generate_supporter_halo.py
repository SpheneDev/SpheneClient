"""Generate the lossless, client-bundled Sphene Halo supporter icon."""

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


FRAMES = 24
SIZE = 256
CENTER = SIZE // 2
OUTPUT = Path(__file__).resolve().parent.parent / "Sphene/images/supporter_halo.webp"


def point(radius: float, angle: float) -> tuple[float, float]:
    return CENTER + math.cos(angle) * radius, CENTER + math.sin(angle) * radius


def build_frame(frame: int) -> Image.Image:
    phase = frame / FRAMES
    pulse = 0.5 + 0.5 * math.sin(phase * math.tau - math.pi / 2)
    canvas = Image.new("RGBA", (SIZE, SIZE))
    glow = Image.new("RGBA", (SIZE, SIZE))
    halo = ImageDraw.Draw(glow)

    halo.ellipse((39, 39, 217, 217), outline=(244, 194, 92, 64 + int(pulse * 45)), width=15)
    halo.polygon(
        [(CENTER, 64), (165, CENTER), (CENTER, 192), (91, CENTER)],
        fill=(75, 227, 231, 65 + int(pulse * 45)),
    )
    canvas.alpha_composite(glow.filter(ImageFilter.GaussianBlur(12)))
    draw = ImageDraw.Draw(canvas)

    # A broken gold ring and two slowly orbiting accents echo the Sphene mark.
    draw.arc((37, 37, 219, 219), 12, 158, fill=(246, 213, 132, 225), width=11)
    draw.arc((37, 37, 219, 219), 192, 338, fill=(246, 213, 132, 225), width=11)
    draw.arc((48, 48, 208, 208), 35 + phase * 50, 89 + phase * 50,
             fill=(111, 232, 233, 185), width=7)
    draw.arc((48, 48, 208, 208), 211 + phase * 50, 265 + phase * 50,
             fill=(171, 140, 241, 190), width=7)

    for index in range(3):
        x, y = point(91, phase * math.tau + index * math.tau / 3 - math.pi / 2)
        radius = 4 + pulse * 2 if index == 0 else 3
        draw.ellipse((x - radius, y - radius, x + radius, y + radius),
                     fill=(254, 247, 211, 190 + int(55 * pulse)))

    # A faceted, four-point crystal stays legible at the nameplate's 20px size.
    diamond = [(CENTER, 57), (169, CENTER), (CENTER, 199), (87, CENTER)]
    draw.polygon(diamond, fill=(28, 55, 84, 240))
    draw.polygon([(CENTER, 65), (161, CENTER), (CENTER, CENTER)],
                 fill=(83, 206, 217, 235))
    draw.polygon([(CENTER, 65), (CENTER, CENTER), (95, CENTER)],
                 fill=(214, 237, 219, 220))
    draw.polygon([(CENTER, CENTER), (161, CENTER), (CENTER, 190)],
                 fill=(72, 114, 165, 235))
    draw.polygon([(95, CENTER), (CENTER, CENTER), (CENTER, 190)],
                 fill=(119, 139, 184, 220))
    draw.line(diamond + [diamond[0]], fill=(255, 227, 163, 240), width=7, joint="curve")

    sparkle = 6 + 5 * pulse
    draw.line([(CENTER, 89 - sparkle), (CENTER, 89 + sparkle)],
              fill=(255, 252, 225, 195 + int(60 * pulse)), width=5)
    draw.line([(CENTER - sparkle, 89), (CENTER + sparkle, 89)],
              fill=(255, 252, 225, 195 + int(60 * pulse)), width=5)
    return canvas.resize((64, 64), Image.Resampling.LANCZOS)


def main() -> None:
    frames = [build_frame(index) for index in range(FRAMES)]
    frames[0].save(
        OUTPUT, format="WEBP", save_all=True, append_images=frames[1:],
        duration=80, loop=0, lossless=True, method=6, minimize_size=True,
    )
    print(f"Generated {OUTPUT} ({OUTPUT.stat().st_size} bytes, {FRAMES} frames)")


if __name__ == "__main__":
    main()
