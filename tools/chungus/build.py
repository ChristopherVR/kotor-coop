"""Builds the boss's game files: the model, its two textures and two original synthesized sounds.

    python tools/chungus/build.py                 writes into profiles/override
    python tools/chungus/build.py --out DIR       writes somewhere else
    python tools/chungus/build.py --preview DIR   also renders pictures of the model and each animation

The output is deterministic: the same sources always give the same bytes, and
tests/KotorCoop.Tests/ChungusBossTests.cs checks that the shipped files are well formed.

Needs numpy, scipy and xatlas (imported by the TGA helper); Pillow is needed for previews.
The replacement no longer uses scikit-image or fast-simplification:

    python -m venv artifacts/chungus-venv
    artifacts/chungus-venv/Scripts/python -m pip install numpy scipy pillow xatlas
    artifacts/chungus-venv/Scripts/python tools/chungus/build.py
"""
from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import anims  # noqa: E402
import bake  # noqa: E402
import kmdl  # noqa: E402
import model  # noqa: E402
import sfx  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=os.path.join(REPO, "profiles", "override"))
    ap.add_argument("--preview", help="directory for preview pictures")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    built = model.build()
    animations = anims.build(built.root)
    mdl, mdx = kmdl.write(kmdl.Model(model.NAME, built.root, animations))
    files = {model.NAME + ".mdl": mdl, model.NAME + ".mdx": mdx}
    for name, pixels in built.textures.items():
        files[name + ".tga"] = bake.tga_bytes(pixels)
    for name, text in built.txi.items():
        files[name + ".txi"] = text.encode("ascii")
    for name, make in sfx.SOUNDS.items():
        files[name + ".wav"] = sfx.wav_bytes(make())
    for name, data in files.items():
        with open(os.path.join(args.out, name), "wb") as out:
            out.write(data)
        print(f"{name:24} {len(data):8} bytes")
    nodes, _ = kmdl.flatten(built.root)
    print(f"{len(nodes)} nodes, {sum(len(n.mesh.faces) for n in nodes if n.mesh)} triangles, "
          f"{len(animations)} animations; walk {anims.WALK_DISTANCE:.2f} m, run {anims.RUN_DISTANCE:.2f} m per cycle")

    if args.preview:
        import preview
        os.makedirs(args.preview, exist_ok=True)
        views = [preview.render(built.root, built.textures, yaw=y, pitch=p) for y, p in ((0, 5), (-35, 10), (90, 5), (180, 5))]
        views.append(preview.render(built.root, built.textures, yaw=-8, pitch=4, centre=(0, 0.4, 2.3), span=1.7))
        views.append(preview.render(built.root, built.textures, yaw=0, pitch=5, centre=(0, 0.4, 1.6), span=1.5))
        preview.sheet(views, 3, ["front", "front right", "side", "back", "face", "resonator"]).save(
            os.path.join(args.preview, "rest.png"))
        for anim in animations:
            frames = [preview.render(built.root, built.textures, anim, anim.length * k / 7, size=(260, 260), yaw=-20,
                                     centre=(0, 0.2, 1.8), span=5.0) for k in range(8)]
            preview.sheet(frames, 8).save(os.path.join(args.preview, anim.name + ".png"))


if __name__ == "__main__":
    main()
