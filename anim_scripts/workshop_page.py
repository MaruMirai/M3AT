"""Renders a Workshop description (Steam BBCode) as an HTML page styled like
the Workshop, with the mod's preview picture on top and a button that copies
the BBCode, to look over before pasting it into Steam or to publish as an
artifact.

    python3 anim_scripts/workshop_page.py [description.txt] [out.html]

Defaults: workshop_description_short_bold.txt at the repository's root, and
previews/<its name>.html (previews/ is not in git). Wants Pillow.
"""
import sys, os, re, html, base64, json, io
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
srcPath = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, "workshop_description_short_bold.txt")
outPath = sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, "previews", os.path.splitext(os.path.basename(srcPath))[0] + ".html")
src = open(srcPath).read()
prev = os.path.join(root, "M3AT: Maru's 3D Animated Toons", "About", "Preview.png")
# Shrink the preview to a JPEG to keep the page small.
from PIL import Image
im = Image.open(prev).convert("RGB"); im = im.resize((960, im.height*960//im.width), Image.LANCZOS)
buf = io.BytesIO(); im.save(buf, "JPEG", quality=82); jpg = buf.getvalue()
img = "data:image/jpeg;base64," + base64.b64encode(jpg).decode()

def conv(t):
    t = html.escape(t, quote=False)
    t = re.sub(r'\[url=([^\]]+)\](.*?)\[/url\]', lambda m: f'<a href="{html.escape(m.group(1))}">{m.group(2)}</a>', t, flags=re.S)
    for a, b in [('b','strong'),('i','em'),('u','u'),('h1','h1'),('h2','h2'),('h3','h3')]:
        t = t.replace(f'[{a}]', f'<{b}>').replace(f'[/{a}]', f'</{b}>')
    t = t.replace('[hr][/hr]', '<hr>')
    t = re.sub(r'\[list\]\s*', '<ul>', t)
    t = re.sub(r'\s*\[/list\]', '</ul>', t)
    t = re.sub(r'\[\*\](.*?)(?=\n?\[\*\]|</ul>)', lambda m: f'<li>{m.group(1).strip()}</li>', t, flags=re.S)
    out = []
    for line in t.split('\n'):
        out.append(line)
    t = '\n'.join(out)
    # Line breaks as Steam shows them, but not around block tags.
    t = re.sub(r'\n*(<(?:/?ul|h1|h2|/h1|/h2|hr|li|/li)[^>]*>)\n*', r'\1', t)
    t = t.replace('\n', '<br>\n')
    return t

body = conv(src)
page = f'''<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>M3AT Workshop description (short, bold)</title>
<style>
:root {{ color-scheme: dark; }}
body {{ margin:0; background:#1b2838; color:#acb2b8; font:14px/1.55 "Motiva Sans", Arial, Helvetica, sans-serif; }}
.wrap {{ max-width: 960px; margin: 0 auto; padding: 24px 16px 48px; }}
.bar {{ display:flex; gap:12px; align-items:center; justify-content:space-between; margin-bottom:16px; color:#8f98a0; font-size:13px; }}
button {{ background:linear-gradient(to right,#47bfff 5%,#1a44c2 60%); color:#fff; border:0; border-radius:2px; padding:7px 14px; font-size:13px; cursor:pointer; }}
button:hover {{ filter:brightness(1.15); }}
.hero {{ width:100%; display:block; border-radius:2px; margin-bottom:16px; }}
.desc {{ background:rgba(0,0,0,.2); padding:18px 22px; border-radius:3px; }}
h1 {{ color:#5aa9d6; font-size:22px; font-weight:normal; margin:4px 0 10px; }}
h2 {{ color:#5aa9d6; font-size:18px; font-weight:normal; margin:14px 0 6px; }}
strong {{ color:#dfe3e6; }}
a {{ color:#ffffff; text-decoration:none; }}
a:hover {{ color:#66c0f4; }}
hr {{ border:0; border-top:1px solid #4b5a68; margin:14px 0; }}
ul {{ margin:4px 0 8px; padding-left:22px; }}
li {{ margin:2px 0; }}
</style></head><body><div class="wrap">
<div class="bar"><span>Steam Workshop preview · rendered from <code>{html.escape(os.path.basename(srcPath))}</code></span>
<button id="copy">Copy BBCode</button></div>
<img class="hero" src="{img}" alt="M3AT preview picture">
<div class="desc">{body}</div>
</div>
<script>
const bb = {json.dumps(src)};
document.getElementById('copy').addEventListener('click', async e => {{
  try {{ await navigator.clipboard.writeText(bb); e.target.textContent = 'Copied'; }}
  catch (_) {{ e.target.textContent = 'Copy blocked'; }}
  setTimeout(() => e.target.textContent = 'Copy BBCode', 1500);
}});
</script>
</body></html>'''
os.makedirs(os.path.dirname(outPath), exist_ok=True)
open(outPath, 'w').write(page)
print(outPath)
