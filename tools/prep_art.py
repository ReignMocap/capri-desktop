"""Capri's desktop art, made from her existing pictures (never redrawn).

    python -I tools/prep_art.py [out folder]        default out: E:/VoxtaArt/CapriDesktop

One folder per picture she can stand in, all cropped to one shared box and shrunk to SCALE, so every layer lines up
1:1 and her feet stay put between pictures:
    idle_N/   f0.png (the pose, E:/VoxtaArt/Capri/recovered/idle_N.png), f1..f7.png (its breathing frames,
              E:/VoxtaArt/Capri/breathing/idle_N__bK.webp), m.png (mouth open), blink.png (eyes off)
    talk_Nx/  f0.png, m.png, blink.png: a talking gesture of idle_N's family (no breathing; she's talking)
blink.png is made by blinks.py from the Cyberpunk module.
"""
import json
import os
import re
import sys

sys.dont_write_bytecode = True
TOOLS = r"C:\Users\Dan\RiderProjects\voxta-module-cyberpunk\game\capri\tools"
sys.path.append(TOOLS)

import numpy as np
from PIL import Image
from scipy import ndimage

from blinks import blink_patch  # noqa: E402  (read from the module repo, unchanged)

ART = r"E:\VoxtaArt\Capri"
SCALE = 0.25          # 2160x3240 -> 540x810 before cropping; the app shrinks further to its chosen height
FAINT = 64            # alpha below this is wiped (thin haze round her outline)
PAD = 12              # px of room round her, full size


def clean(img):
    a = np.array(img.convert("RGBA"))
    a[..., 3][a[..., 3] < FAINT] = 0
    empty = a[..., 3] == 0
    if empty.any() and not empty.all():
        _, (iy, ix) = ndimage.distance_transform_edt(empty, return_indices=True)
        a[..., :3][empty] = a[iy, ix, :3][empty]
    return Image.fromarray(a, "RGBA")


def load(path):
    return clean(Image.open(path))


# The pictures a desktop Capri uses: standing, talking, and her moods and gestures. (The game's fly_, float_,
# kick_, sit_, back_ and side_ are for moving about in Night City.)
KINDS = ("idle", "talk", "angry", "annoyed", "cast", "flirty", "laugh", "peace", "point", "think", "thumbs", "wave",
         "embarrassed", "love")
# Pictures whose eyes aren't her usual open visor eyes (closed, squinting): no blink, as in the game (NO_BLINK).
NO_BLINK = ("laugh_", "angry_", "love_")


def tidy(stem):
    """The game's name fixes (build_atlas.py tidy())."""
    stem = re.sub(r"^talking_(\d+)$", r"talk_\1a", stem)
    stem = re.sub(r"^hover_(\d+)$", r"float_\1", stem)
    return re.sub(r"^embaressed_", "embarrassed_", stem)


def pictures():
    """{folder name: {"frames": [...], "mouth": img or None}}"""
    out = {}
    for folder in ("recovered", "newpics"):
        src = os.path.join(ART, folder)
        files = {os.path.splitext(f)[0]: f for f in os.listdir(src) if f.lower().endswith((".png", ".webp"))}
        for stem, f in sorted(files.items()):
            if stem.endswith("_m"):
                continue
            name = tidy(stem)
            if name.split("_")[0] not in KINDS or name in out:
                continue
            frames = [load(os.path.join(src, f))]
            if name.startswith("idle_"):
                for k in range(1, 8):
                    p = os.path.join(ART, "breathing", f"{name}__b{k}.webp")
                    if os.path.exists(p):
                        frames.append(load(p))
            mouth = files.get(stem + "_m")
            out[name] = {"frames": frames, "mouth": load(os.path.join(src, mouth)) if mouth else None}
    return out


def shrink(img, box):
    c = img.crop(box)
    size = (round(c.width * SCALE), round(c.height * SCALE))
    return c.convert("RGBa").resize(size, Image.LANCZOS).convert("RGBA")


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else r"E:\VoxtaArt\CapriDesktop"
    os.makedirs(out, exist_ok=True)
    pics = pictures()
    W, H = next(iter(pics.values()))["frames"][0].size
    # one shared box round her in every picture
    x0, y0, x1, y1 = W, H, 0, 0
    for p in pics.values():
        for f in p["frames"] + ([p["mouth"]] if p["mouth"] else []):
            b = f.getchannel("A").getbbox()
            if b:
                x0, y0, x1, y1 = min(x0, b[0]), min(y0, b[1]), max(x1, b[2]), max(y1, b[3])
    box = (max(0, x0 - PAD), max(0, y0 - PAD), min(W, x1 + PAD), min(H, y1 + PAD))
    info = {"box": box, "scale": SCALE, "pictures": {}}
    for name, p in pics.items():
        d = os.path.join(out, name)
        os.makedirs(d, exist_ok=True)
        for k, f in enumerate(p["frames"]):
            shrink(f, box).save(os.path.join(d, f"f{k}.png"), optimize=True)
        if p["mouth"]:
            shrink(p["mouth"], box).save(os.path.join(d, "m.png"), optimize=True)
        got = None if name.startswith(NO_BLINK) else blink_patch(p["frames"][0])
        if got:
            (bx0, by0, _, _), patch = got
            full = Image.new("RGBA", (W, H), (0, 0, 0, 0))
            full.alpha_composite(patch, (bx0, by0))
            shrink(full, box).save(os.path.join(d, "blink.png"), optimize=True)
        info["pictures"][name] = {"frames": len(p["frames"]), "mouth": bool(p["mouth"]), "blink": bool(got)}
        print(name, "frames", len(p["frames"]), "mouth", bool(p["mouth"]), "blink", bool(got))
    with open(os.path.join(out, "info.json"), "w") as fh:
        json.dump(info, fh, indent=2)
    print("box", box, "->", out)


if __name__ == "__main__":
    main()
