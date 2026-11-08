"""Capri's face HUD art and her sound effects for the desktop, made from what she already has (never redrawn).

    python -I tools/prep_face.py [out folder]        default out: E:/VoxtaArt/CapriDesktop

    face/<name>.png   each picture in E:/VoxtaArt/CapriEyes at 1024 wide: the eyes with the green round them cut
                      away (the game's build_eyes.py cut_out), the mouths cleaned (its clean_transparent)
    sfx/*.wav         her zap and glitch sounds, copied from the Cyberpunk module (our own, make_sounds.py)
"""
import os
import shutil
import sys

sys.dont_write_bytecode = True
MODULE = r"C:\Users\Dan\RiderProjects\voxta-module-cyberpunk\game"
sys.path.append(os.path.join(MODULE, "capri", "tools"))

from PIL import Image

from build_atlas import bleed, wipe_faint            # noqa: E402  (read from the module repo, unchanged)
from build_eyes import clean_transparent, cut_out, drawn_on_transparent   # noqa: E402

EYES = r"E:\VoxtaArt\CapriEyes"
SFX = os.path.join(MODULE, "cet", "VoxtaForCyberpunk", "sfx")
WIDTH = 1024


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else r"E:\VoxtaArt\CapriDesktop"
    face = os.path.join(out, "face")
    os.makedirs(face, exist_ok=True)
    for f in sorted(os.listdir(EYES)):
        if not f.lower().endswith(".png"):
            continue
        path = os.path.join(EYES, f)
        clear_bg = drawn_on_transparent(path)
        pic = clean_transparent(path) if clear_bg else cut_out(path)
        pic = wipe_faint(pic.resize((WIDTH, round(pic.height * WIDTH / pic.width)), Image.LANCZOS), 24)
        pic = wipe_faint(bleed(pic), 24)
        pic.save(os.path.join(face, f), optimize=True)
        print(f, pic.size)
    sfx = os.path.join(out, "sfx")
    os.makedirs(sfx, exist_ok=True)
    for f in os.listdir(SFX):
        if f.startswith("capri_zap") or f.startswith("capri_glitch"):
            shutil.copy2(os.path.join(SFX, f), sfx)
            print("sfx", f)


if __name__ == "__main__":
    main()
