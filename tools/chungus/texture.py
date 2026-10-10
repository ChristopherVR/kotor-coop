"""Paints the one texture the model uses and says where each part's patch sits on it."""
from __future__ import annotations

import struct

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

SIZE = 512

GREY = (126, 128, 132)
GREY_DARK = (92, 94, 100)
WHITE = (238, 236, 228)
PINK = (226, 150, 160)
GOLD = (214, 160, 52)
GOLD_DARK = (128, 84, 22)
GOLD_LIGHT = (255, 226, 130)
MOUTH = (150, 22, 30)
BLACK = (18, 16, 18)

STONES = {
    "stone_power": (170, 60, 255),
    "stone_space": (60, 120, 255),
    "stone_reality": (255, 50, 50),
    "stone_soul": (255, 150, 30),
    "stone_time": (60, 230, 90),
    "stone_mind": (255, 235, 70),
}

# Pixel rectangles (left, top, right, bottom), top-left origin as PIL draws.
_PIXELS = {
    "body": (0, 0, 256, 256),
    "head": (256, 0, 512, 256),
    "ear": (0, 256, 96, 512),
    "eye": (96, 256, 224, 384),
    "gauntlet": (224, 256, 480, 384),
    "fur": (96, 384, 160, 448),
    "white": (160, 384, 224, 448),
    "pink": (224, 384, 288, 448),
    "gold": (288, 384, 352, 448),
    "mouth": (352, 384, 416, 448),
    "black": (416, 384, 480, 448),
    "tooth": (96, 448, 160, 512),
}
for _index, _name in enumerate(STONES):
    _PIXELS[_name] = (160 + 48 * _index, 448, 208 + 48 * _index, 496)


def rect(name):
    """The patch as (u0, v0, u1, v1) with v pointing up, as the model file wants it."""
    left, top, right, bottom = _PIXELS[name]
    return (left / SIZE, 1 - bottom / SIZE, right / SIZE, 1 - top / SIZE)


def _fur(image, box, colour, rng, strength=10):
    """Fill with a colour and a little soft mottling so a big surface is not flat."""
    left, top, right, bottom = box
    w, h = right - left, bottom - top
    noise = rng.normal(0.0, 1.0, (h // 4 + 2, w // 4 + 2))
    noise = np.asarray(Image.fromarray((noise * 40 + 128).clip(0, 255).astype(np.uint8)).resize((w, h), Image.BICUBIC),
                       dtype=float)
    noise = (noise - 128) / 40 * strength
    fine = rng.normal(0.0, strength * 0.35, (h, w))
    block = np.clip(np.array(colour, dtype=float)[None, None, :] + (noise + fine)[:, :, None], 0, 255).astype(np.uint8)
    image.paste(Image.fromarray(block, "RGB"), (left, top))


def _soft_blob(size, box, feather):
    """A white-on-black mask of an ellipse with a soft edge."""
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).ellipse(box, fill=255)
    return mask.filter(ImageFilter.GaussianBlur(feather))


def paint():
    rng = np.random.default_rng(1138)
    image = Image.new("RGB", (SIZE, SIZE), GREY)

    # Body, unwrapped like a globe: the middle column is the front. Grey coat, white belly and chest.
    box = _PIXELS["body"]
    _fur(image, box, GREY, rng)
    belly = Image.new("RGB", (256, 256))
    _fur(belly, (0, 0, 256, 256), WHITE, rng, strength=5)
    image.paste(belly, (box[0], box[1]), _soft_blob((256, 256), (58, 30, 198, 262), 5))
    # The coat darkens a little toward the back (both edges of the patch).
    shade = Image.new("L", (256, 256), 0)
    draw = ImageDraw.Draw(shade)
    draw.rectangle((0, 0, 26, 256), fill=70)
    draw.rectangle((230, 0, 256, 256), fill=70)
    image.paste(Image.new("RGB", (256, 256), GREY_DARK), (box[0], box[1]), shade.filter(ImageFilter.GaussianBlur(14)))

    # Head, same unwrap: grey skull, white lower face reaching up between the eyes.
    box = _PIXELS["head"]
    _fur(image, box, GREY, rng)
    face = Image.new("RGB", (256, 256))
    _fur(face, (0, 0, 256, 256), WHITE, rng, strength=5)
    mask = Image.new("L", (256, 256), 0)
    draw = ImageDraw.Draw(mask)
    draw.ellipse((44, 118, 212, 300), fill=255)          # cheeks and chin
    draw.ellipse((100, 70, 156, 190), fill=255)          # the blaze up the nose
    image.paste(face, (box[0], box[1]), mask.filter(ImageFilter.GaussianBlur(4)))

    # Ear seen from the front: grey rim, pink inside.
    box = _PIXELS["ear"]
    _fur(image, box, GREY, rng)
    inner = Image.new("RGB", (96, 256), PINK)
    image.paste(inner, (box[0], box[1]), _soft_blob((96, 256), (26, 26, 70, 236), 4))

    # Eye seen from the front: white, a big black pupil low and toward the nose, a glint.
    box = _PIXELS["eye"]
    draw = ImageDraw.Draw(image)
    draw.rectangle(box, fill=(250, 250, 248))
    cx, cy = box[0] + 64, box[1] + 70
    draw.ellipse((cx - 30, cy - 34, cx + 30, cy + 34), fill=BLACK)
    draw.ellipse((cx - 16, cy - 26, cx - 2, cy - 10), fill=(255, 255, 255))

    # Gauntlet, wrapped around the forearm: beaten gold with darker bands and rivets.
    box = _PIXELS["gauntlet"]
    _fur(image, box, GOLD, rng, strength=14)
    draw = ImageDraw.Draw(image)
    for y in (box[1] + 10, box[1] + 62, box[1] + 110):
        draw.rectangle((box[0], y, box[2], y + 7), fill=GOLD_DARK)
        draw.rectangle((box[0], y + 7, box[2], y + 10), fill=GOLD_LIGHT)
    for x in range(box[0] + 12, box[2], 32):
        for y in (box[1] + 40, box[1] + 90):
            draw.ellipse((x - 4, y - 4, x + 4, y + 4), fill=GOLD_LIGHT, outline=GOLD_DARK)

    _fur(image, _PIXELS["fur"], GREY, rng, strength=8)
    _fur(image, _PIXELS["white"], WHITE, rng, strength=4)
    _fur(image, _PIXELS["pink"], PINK, rng, strength=4)
    _fur(image, _PIXELS["gold"], GOLD, rng, strength=12)
    draw = ImageDraw.Draw(image)
    draw.rectangle(_PIXELS["mouth"], fill=MOUTH)
    draw.rectangle(_PIXELS["black"], fill=BLACK)
    draw.rectangle(_PIXELS["tooth"], fill=(252, 250, 240))
    for name, colour in STONES.items():
        left, top, right, bottom = _PIXELS[name]
        draw.rectangle((left, top, right, bottom), fill=colour)
        # A pale core so the stone reads as lit from inside.
        core = tuple(min(255, c + 110) for c in colour)
        draw.ellipse((left + 14, top + 14, right - 14, bottom - 14), fill=core)
    return image


def tga_bytes(image):
    """Uncompressed 24-bit TGA, bottom row first (the origin the game expects)."""
    rgb = np.asarray(image.convert("RGB"))
    bgr = rgb[::-1, :, ::-1]
    header = struct.pack("<BBBHHBHHHHBB", 0, 0, 2, 0, 0, 0, 0, 0, image.width, image.height, 24, 0)
    return header + bgr.tobytes()
