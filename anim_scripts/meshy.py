"""Meshy's API for the garment pipeline: reference images to an untextured mesh,
remeshing a mesh, and texturing one from reference images.

    python3 anim_scripts/meshy.py image <out.glb> <front.png> [side.png ...]
    python3 anim_scripts/meshy.py remesh <in.glb|obj> <out.glb> [polycount] [quad|triangle]
    python3 anim_scripts/meshy.py texture <in.glb> <out.glb> <style.png|-> [prompt] [--keep-uvs]   (- for the prompt alone; --keep-uvs paints on the mesh's own UVs)
    python3 anim_scripts/meshy.py multitexture <in.glb> <out.glb> <front.png> [view.png ...] [--keep-uvs]   (at most four views, front first; --keep-uvs paints on the mesh's own UVs)
    python3 anim_scripts/meshy.py resume <kind> <task id> <out.glb>

The key is read from `meshyAPIkey` at the repository's root (gitignored). Each
task is polled until it finishes and its glb downloaded; the task id is printed
first, so a run cut short can be fetched later with `resume`.
"""
import base64
import json
import mimetypes
import os
import sys
import time
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
API = "https://api.meshy.ai/openapi/v1"
POLL = 10   # seconds between status checks


def key():
    with open(os.path.join(ROOT, "meshyAPIkey")) as f:
        return f.read().strip()


def call(method, path, body=None):
    req = urllib.request.Request(API + path, method=method,
                                 data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Authorization": "Bearer " + key(),
                                          "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        sys.exit(f"{method} {path}: {e.code} {e.read().decode()[:1000]}")


def data_uri(path):
    kind = mimetypes.guess_type(path)[0] or "application/octet-stream"
    if path.endswith((".glb", ".obj")):
        kind = "application/octet-stream"
    with open(path, "rb") as f:
        return f"data:{kind};base64," + base64.b64encode(f.read()).decode()


def wait(kind, task, out):
    print("task", kind, task, flush=True)
    while True:
        t = call("GET", f"/{kind}/{task}")
        print(f"  {t.get('status')} {t.get('progress', 0)}%", flush=True)
        if t["status"] == "SUCCEEDED":
            break
        if t["status"] in ("FAILED", "CANCELED", "EXPIRED"):
            sys.exit(f"task {t['status']}: {t.get('task_error')}")
        time.sleep(POLL)
    urllib.request.urlretrieve(t["model_urls"]["glb"], out)
    with open(out + ".json", "w") as f:
        json.dump(t, f, indent=1)
    print("saved", out)


def main(a):
    if a[0] == "image":
        out, images = a[1], a[2:]
        body = {"image_urls": [data_uri(p) for p in images], "should_texture": False}
        kind = "multi-image-to-3d" if len(images) > 1 else "image-to-3d"
        if kind == "image-to-3d":
            body["image_url"] = body.pop("image_urls")[0]
        wait(kind, call("POST", "/" + kind, body)["result"], out)
    elif a[0] == "remesh":
        src, out = a[1], a[2]
        body = {"model_url": data_uri(src), "target_formats": ["glb", "obj", "fbx"],
                "target_polycount": int(a[3]) if len(a) > 3 else 10000,
                "topology": a[4] if len(a) > 4 else "quad"}
        wait("remesh", call("POST", "/remesh", body)["result"], out)
    elif a[0] == "texture":
        keep = "--keep-uvs" in a
        a = [x for x in a if x != "--keep-uvs"]
        src, out, style = a[1], a[2], a[3]
        body = {"model_url": data_uri(src), "enable_original_uv": keep, "enable_pbr": False}
        if style != "-":
            body["image_style_url"] = data_uri(style)
        if len(a) > 4:
            body["text_style_prompt"] = a[4]
        wait("retexture", call("POST", "/retexture", body)["result"], out)
    elif a[0] == "multitexture":
        # Views of the one object, which only meshy-7 takes, and not with a prompt or a style image.
        src, out, views = a[1], a[2], [v for v in a[3:] if v != "--keep-uvs"]
        body = {"model_url": data_uri(src), "enable_original_uv": "--keep-uvs" in a, "enable_pbr": False,
                "ai_model": "meshy-7", "multiview_image_urls": [data_uri(p) for p in views]}
        wait("retexture", call("POST", "/retexture", body)["result"], out)
    elif a[0] == "resume":
        wait(a[1], a[2], a[3])
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:] or ["help"])
