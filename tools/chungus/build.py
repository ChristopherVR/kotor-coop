"""Builds the boss's game files: the model, its texture and its sounds.

    python tools/chungus/build.py                 writes into profiles/override
    python tools/chungus/build.py --out DIR       writes somewhere else
    python tools/chungus/build.py --preview DIR   also renders pictures of the model and each animation

The output is deterministic: the same sources always give the same bytes, and
tests/KotorCoop.Tests/ChungusAssetTests.cs checks that the shipped files are well formed.
Needs numpy, scipy and Pillow.
"""
from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import anims  # noqa: E402
import kmdl  # noqa: E402
import model  # noqa: E402
import sfx  # noqa: E402
import texture  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def build_model():
    root = model.build_nodes()
    return kmdl.Model(model.NAME, root, anims.build(root))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=os.path.join(REPO, "profiles", "override"))
    ap.add_argument("--preview", help="directory for preview pictures")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    built = build_model()
    mdl, mdx = kmdl.write(built)
    image = texture.paint()
    files = {
        model.NAME + ".mdl": mdl,
        model.NAME + ".mdx": mdx,
        model.TEXTURE + ".tga": texture.tga_bytes(image),
    }
    for name, make in sfx.SOUNDS.items():
        files[name + ".wav"] = sfx.wav_bytes(make())
    for name, data in files.items():
        with open(os.path.join(args.out, name), "wb") as out:
            out.write(data)
        print(f"{name:24} {len(data):8} bytes")
    nodes, _ = kmdl.flatten(built.root)
    print(f"{len(nodes)} nodes, {sum(len(n.mesh.faces) for n in nodes if n.mesh)} triangles, "
          f"{len(built.animations)} animations; walk {anims.WALK_DISTANCE:.2f} m, run {anims.RUN_DISTANCE:.2f} m per cycle")

    if args.preview:
        import preview
        os.makedirs(args.preview, exist_ok=True)
        views = [preview.render(built.root, image, yaw=y, pitch=p) for y, p in ((0, 5), (-35, 10), (90, 5), (180, 5))]
        preview.sheet(views, 4, ["front", "front right", "side", "back"]).save(os.path.join(args.preview, "rest.png"))
        for anim in built.animations:
            frames = [preview.render(built.root, image, anim, anim.length * k / 7, size=(240, 240), yaw=-25,
                                     centre=(0, 0.2, 1.6), span=5.6) for k in range(8)]
            preview.sheet(frames, 8).save(os.path.join(args.preview, anim.name + ".png"))


if __name__ == "__main__":
    main()
