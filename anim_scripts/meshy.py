"""Meshy's API for the garment pipeline: reference images to an untextured mesh,
remeshing a mesh, and texturing one from reference images.

    python3 anim_scripts/meshy.py image <out.glb> <front.png> [side.png ...]
    python3 anim_scripts/meshy.py remesh <in.glb|obj> <out.glb> [polycount] [quad|triangle]
    python3 anim_scripts/meshy.py texture <in.glb> <out.glb> <style.png|-> [prompt] [--keep-uvs]   (- for the prompt alone; --keep-uvs paints on the mesh's own UVs)
    python3 anim_scripts/meshy.py multitexture <in.glb> <out.glb> <front.png> [view.png ...] [--keep-uvs]   (at most four views, front first; --keep-uvs paints on the mesh's own UVs)
    python3 anim_scripts/meshy.py resume <kind> <task id> <out.glb>
    python3 anim_scripts/meshy.py library [search] [--category C] [--sub S] [--json out.json]
    python3 anim_scripts/meshy.py rig [model.glb]
    python3 anim_scripts/meshy.py animate <action id> [action id ...] [--out folder]

Animation clips: `library` lists Meshy's animation library (free; matched on the name, the
key, the category and the sub-category, case ignored), each clip's action id and preview GIF.
`rig` has Meshy rig the chibi (`RIG_MODEL`, the body with no shape keys, as the clips in
animDLs/ were made on), with fingers, for 5 credits, and keeps the task in `animDLs/meshy_rig.json`;
`animate` puts clips on that rig (3 credits each, ten to a request), rigging first if there is
no rig or it has expired, and saves each as `animDLs/Meshy_AI_chibiBodyNoKeys_<key>.glb`, the name
the clips fetched from Meshy's site have, ready for `import_pluck.py`.

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
    t = poll(kind, task)
    urllib.request.urlretrieve(t["model_urls"]["glb"], out)
    with open(out + ".json", "w") as f:
        json.dump(t, f, indent=1)
    print("saved", out)


RIG_MODEL = os.path.join(ROOT, "Models", "unused", "chibiBodyNoKeys.glb")
RIG_FILE = os.path.join(ROOT, "animDLs", "meshy_rig.json")


def poll(kind, task):
    """Polls a task until it is done and returns it, or stops on a failure."""
    print("task", kind, task, flush=True)
    while True:
        t = call("GET", f"/{kind}/{task}")
        print(f"  {t.get('status')} {t.get('progress', 0)}%", flush=True)
        if t["status"] == "SUCCEEDED":
            return t
        if t["status"] in ("FAILED", "CANCELED", "EXPIRED"):
            sys.exit(f"task {t['status']}: {t.get('task_error')}")
        time.sleep(POLL)


def library(a):
    """The animation library, filtered by the words and options in a."""
    category = sub = out = None
    words = []
    i = 0
    while i < len(a):
        if a[i] in ("--category", "--sub", "--json"):
            if a[i] == "--category":
                category = a[i + 1]
            elif a[i] == "--sub":
                sub = a[i + 1]
            else:
                out = a[i + 1]
            i += 2
        else:
            words.append(a[i].lower())
            i += 1
    clips = call("GET", "/animations/library")
    found = []
    for c in clips:
        text = " ".join(str(c.get(k, "")) for k in ("name", "key", "category", "sub_category")).lower()
        if category and c["category"].lower() != category.lower():
            continue
        if sub and c["sub_category"].lower() != sub.lower():
            continue
        if all(w in text for w in words):
            found.append(c)
    if out:
        with open(out, "w") as f:
            json.dump(found, f, indent=1)
        print(f"{len(found)} clips written to {out}")
        return
    for c in found:
        print(f"{c['action_id']:4d}  {c['name']:<40} {c['category']}/{c['sub_category']}  {c['preview_url']}")
    print(f"{len(found)} of {len(clips)} clips")


def rig(model):
    """Rigs the model on Meshy and keeps the task, for animate."""
    body = {"model_url": data_uri(model), "include_fingers": True}
    t = poll("rigging", call("POST", "/rigging", body)["result"])
    kept = {"id": t["id"], "model": os.path.relpath(model, ROOT), "expires_at": t.get("expires_at")}
    with open(RIG_FILE, "w") as f:
        json.dump(kept, f, indent=1)
    print("rig", kept["id"], "kept in", os.path.relpath(RIG_FILE, ROOT))
    return kept["id"]


def live_rig():
    """The kept rig task's id if Meshy still has it, else None."""
    if not os.path.exists(RIG_FILE):
        return None
    with open(RIG_FILE) as f:
        task = json.load(f)["id"]
    req = urllib.request.Request(f"{API}/rigging/{task}", headers={"Authorization": "Bearer " + key()})
    try:
        with urllib.request.urlopen(req) as r:
            t = json.load(r)
    except urllib.error.HTTPError:
        return None
    expires = t.get("expires_at") or 0
    # expires_at is in milliseconds; a rig with less than ten minutes left would run out under a long request.
    if t.get("status") != "SUCCEEDED" or (expires and expires / 1000 < time.time() + 600):
        return None
    return task


def animate(a):
    """Puts each action id in a on the kept rig and saves the clips in animDLs/ (or --out)."""
    out = os.path.join(ROOT, "animDLs")
    if "--out" in a:
        out = a[a.index("--out") + 1]
        a = a[:a.index("--out")] + a[a.index("--out") + 2:]
    ids = [int(x) for x in a]
    keys = {c["action_id"]: c["key"] for c in call("GET", "/animations/library?action_ids=" + ",".join(map(str, ids)))}
    missing = [i for i in ids if i not in keys]
    if missing:
        sys.exit(f"no such action ids: {missing}")
    task = live_rig()
    if task is None:
        print("no live rig: rigging", os.path.relpath(RIG_MODEL, ROOT))
        task = rig(RIG_MODEL)
    # One request per clip, so each comes back as a file of its own with one action in it, as import_pluck.py
    # reads them; ten to a request would merge them into one file.
    for i in ids:
        t = poll("animations", call("POST", "/animations", {"rig_task_id": task, "action_id": i})["result"])
        path = os.path.join(out, f"Meshy_AI_chibiBodyNoKeys_{keys[i]}.glb")
        urllib.request.urlretrieve(t["result"]["animation_glb_url"], path)
        print("saved", os.path.relpath(path, ROOT))


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
    elif a[0] == "library":
        library(a[1:])
    elif a[0] == "rig":
        rig(a[1] if len(a) > 1 else RIG_MODEL)
    elif a[0] == "animate":
        animate(a[1:])
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:] or ["help"])
