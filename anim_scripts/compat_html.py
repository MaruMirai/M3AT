#!/usr/bin/env python3
"""Writes the compatibility table as one self-contained web page, from Docs/Compatibility.csv (the same list
compat_table.py turns into Compatibility.md's tables).

    python3 anim_scripts/compat_html.py [out.html]

The default output is previews/compatibility.html (not in git). Open it in a browser as is, or publish it as an
artifact from whichever Claude account is in use: the page holds everything, so any account can republish it,
and the CSV stays the one list."""
import csv, html, json, os, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
CSV = os.path.join(ROOT, "M3AT: Maru's 3D Animated Toons", "Docs", "Compatibility.csv")
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "previews", "compatibility.html")
COLUMNS = ["Status", "Clothes", "Gene parts", "Also needs", "What works", "Known gaps"]

PAGE = """<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>M3AT Mod Compatibility</title>
<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Figtree:wght@400;600;800&display=swap" rel="stylesheet">
<style>
/* VOICE work: a reference table players and modders scan
   IDEA the in-game content switcher's support notes, as a printed sheet
   PICTURE a tally strip of every mod, one block each, coloured by support level
   COLOUR white / night #121417, ink, the switcher's greens, amber and orange
   TYPE Figtree 800 + Figtree
   LAYOUT title and tally across the top, search, then one table per category */
:root{color-scheme:light dark;--bg:light-dark(#fff,#121417);--ink:light-dark(#16181c,#e9ebee);--dim:light-dark(#5d636b,#9aa1aa);--rule:light-dark(#e3e5e8,#2a2e34);--band:light-dark(#f2f6ee,#1a1f1a);--full:light-dark(#2e9e48,#4cc46a);--most:light-dark(#1f8f86,#3cc0b4);--gen:light-dark(#7aa632,#9cc94e);--part:light-dark(#e07a1f,#f0943f);--unt:light-dark(#8a9099,#6f767f);--req:light-dark(#3a5f9e,#7fa3e0)}
:root[data-theme=light]{color-scheme:light}:root[data-theme=dark]{color-scheme:dark}
*{box-sizing:border-box;min-width:0}
body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.55 Figtree,system-ui,sans-serif;overflow-x:clip}
.band{padding:clamp(24px,4vw,48px) max(5vw,16px)}
.top{background:var(--band);border-bottom:1px solid var(--rule)}
.wrap{max-width:1240px;margin:0 auto}
h1{font-weight:800;font-size:clamp(32px,5vw,46px);line-height:1.05;letter-spacing:-.02em;margin:0 0 10px;text-wrap:balance}
.lede{margin:0 0 22px;max-width:68ch;color:var(--dim)}
.tally{display:grid;grid-template-columns:repeat(auto-fill,minmax(22px,1fr));gap:4px;margin-bottom:12px}
.tally a{height:44px;border-radius:4px;display:block}
.tally a:hover,.tally a:focus-visible{outline:2px solid var(--ink);outline-offset:1px}
.key{display:flex;flex-wrap:wrap;gap:6px 18px;font-size:14px;color:var(--dim);margin:0}
.key span{white-space:nowrap}.key i{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:6px;vertical-align:-1px}
.tools{display:flex;flex-wrap:wrap;gap:12px;align-items:center;margin-bottom:8px}
input{font:inherit;font-size:16px;padding:10px 12px;min-height:44px;width:min(100%,360px);border:1px solid var(--rule);border-radius:6px;background:var(--bg);color:var(--ink)}
input:focus-visible{outline:2px solid var(--full);outline-offset:1px}
.count{color:var(--dim);font-size:14px}
.req{margin:0 0 8px}.req a{color:inherit}
h2{font-size:22px;font-weight:800;letter-spacing:-.01em;margin:32px 0 8px}
.scroll{overflow-x:auto}
table{border-collapse:collapse;width:100%;font-size:14.5px}
th{text-align:left;font-weight:600;color:var(--dim);font-size:13px;padding:8px 10px;border-bottom:2px solid var(--rule);white-space:nowrap}
td{padding:10px;border-bottom:1px solid var(--rule);vertical-align:top}
td.mod{font-weight:600;width:16%}td.mod a{color:inherit;text-decoration-color:var(--rule);text-underline-offset:3px}td.mod a:hover{text-decoration-color:currentColor}
td.works{width:30%}td.needs{width:18%}td.gaps{width:24%;color:var(--dim)}
.pill{display:inline-block;white-space:nowrap;font-size:13px;font-weight:600;padding:2px 8px;border-radius:99px;color:#fff}
.Full{background:var(--full)}.Most{background:var(--most)}.General{background:var(--gen)}.Partial{background:var(--part)}.Untested{background:var(--unt)}.Required{background:var(--req)}
.pill{color:light-dark(#fff,#0d0f12)}
.tag{text-decoration:underline dotted;text-underline-offset:3px;cursor:help}
.tags{margin:14px 0 0;font-size:14px;color:var(--dim);max-width:80ch}.tags summary{cursor:pointer;color:var(--ink)}
.tags h3{font-size:14px;margin:12px 0 4px;color:var(--ink)}.tags dl{margin:0;display:grid;grid-template-columns:max-content 1fr;gap:4px 14px}.tags dt{font-weight:600;color:var(--ink)}.tags dd{margin:0}.tags p{margin:10px 0 0}
tr:target td{background:color-mix(in srgb,var(--full) 12%,transparent)}
.none{color:var(--dim);display:none}
footer{color:var(--dim);font-size:13px;border-top:1px solid var(--rule)}
@media(max-width:600px){table{font-size:13px}.stack thead{display:none}.stack tr{display:grid;padding-block:10px;border-bottom:1px solid var(--rule)}.stack td{display:flow-root;border:0;padding:3px 0;padding-inline-start:7.5em;text-align:start;width:auto!important}.stack td::before{content:attr(data-l);float:inline-start;width:7em;margin-inline-start:-7.5em;color:var(--dim);font-weight:400}.stack td:empty{display:none}.stack td.mod{padding-inline-start:0;font-size:16px}.stack td.mod::before{display:none}}
</style></head><body>
<header class="band top"><div class="wrap">
<h1>M3AT mod compatibility</h1>
<p class="lede" id="lede"></p>
<div class="tally" id="tally" aria-label="Every mod by support level"></div>
<p class="key" id="key"></p>
<details class="tags" id="tags"><summary>What the Clothes and Gene parts tags mean</summary></details>
</div></header>
<main class="band"><div class="wrap">
<div class="tools"><input id="q" type="search" placeholder="Search mods, parts, gaps" aria-label="Search"><span class="count" id="count"></span></div>
<p class="req" id="req"></p>
<div id="tables"></div>
<p class="none" id="none">No mod matches that search.</p>
</div></main>
<footer class="band"><div class="wrap">Made from Docs/Compatibility.csv by anim_scripts/compat_html.py. "Full" means built for and checked in the game, not finished: M3AT is an early prototype.</div></footer>
<script>
{
const ROWS = __ROWS__;
const COLS = __COLS__;
const esc = s => String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
const kind = s => s.split(" ")[0];
const id = r => "m-" + r.Mod.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
const LEVELS = [["Full","Built for it, checked in the game"],["Most","Built for it, clothes automatched"],["General","Works for any mod of the kind"],["Partial","Some of it works"],["Untested","Not yet checked"]];
const rest = ROWS.filter(r => r.Category !== "Required");
const n = k => rest.filter(r => kind(r.Status) === k).length;
document.getElementById("lede").textContent = `${rest.length} mods and kinds of mod beside the four required ones: ${n("Full")} fully supported, ${n("Most")} mostly (clothes automatched), ${n("General")} covered by general rules, ${n("Partial")} partly, ${n("Untested")} untested.`;
document.getElementById("tally").innerHTML = rest.slice().sort((a,b) => LEVELS.findIndex(l => l[0]===kind(a.Status)) - LEVELS.findIndex(l => l[0]===kind(b.Status)))
  .map(r => `<a href="#${id(r)}" class="${esc(kind(r.Status))}" title="${esc(r.Mod)}: ${esc(r.Status)}"></a>`).join("");
document.getElementById("key").innerHTML = LEVELS.map(l => `<span><i class="${l[0]}"></i>${l[0]}: ${l[1]}</span>`).join("");
// What each tag in the Clothes and Gene parts columns means: the same words as the Content tab in M3AT's options,
// kept in step with the lists above the tables in Docs/Compatibility.md. Shown in the fold-out key and on hover.
const TAGS = {
  "Clothes": [
    ["Custom models", "M3AT has its own 3D garment for every piece of the mod's apparel."],
    ["Automatched", "Apparel with no garment of its own is drawn as the closest of M3AT's garments (by body coverage and layer), painted with the apparel's own sprites so colours and patterns follow the original. The shape is the closest garment's, not a new one. The clothing tool can pick a different garment per apparel."],
    ["Automatched (tuned)", "Automatched and painted the same way, with the matches checked and improved."],
  ],
  "Gene parts": [
    ["Custom models", "M3AT has its own models of the race's ears, tail or wings."],
    ["Autogenerated (WIP)", "Built in 3D from the race's own pictures. Rougher than M3AT's other automatic parts."],
    ["Mapped", "The mod's gene parts are drawn as M3AT's own parts, matched to them by hand."],
  ],
};
const meaning = (col, v) => (TAGS[col] || []).find(t => t[0] === v)?.[1];
document.getElementById("tags").insertAdjacentHTML("beforeend", Object.entries(TAGS).map(([col, ts]) =>
  `<h3>${esc(col)}</h3><dl>${ts.map(t => `<dt>${esc(t[0])}</dt><dd>${esc(t[1])}</dd>`).join("")}</dl>`).join("") +
  "<p>A race's face comes from its own Facial Animation mod, listed under Also needs; without one the race has no face. Hair is always built in 3D from the loaded hair sprites.</p>");
const link = r => r.Workshop ? `<a href="${esc(r.Workshop)}">${esc(r.Mod)}</a>` : esc(r.Mod);
document.getElementById("req").innerHTML = "<strong>Required:</strong> " + ROWS.filter(r => r.Category === "Required").map(link).join(", ");
const cats = [...new Set(rest.map(r => r.Category))];
const cls = {"What works":"works","Known gaps":"gaps","Also needs":"needs"};
document.getElementById("tables").innerHTML = cats.map(c => {
  const g = rest.filter(r => r.Category === c);
  const shown = COLS.filter(k => g.some(r => r[k]));
  return `<section data-cat><h2>${esc(c)}</h2><div class="scroll"><table class="stack"><thead><tr><th>Mod</th>${shown.map(k => `<th>${esc(k)}</th>`).join("")}</tr></thead><tbody>` +
    g.map(r => `<tr id="${id(r)}" data-text="${esc(Object.values(r).join(" ").toLowerCase())}"><td class="mod">${link(r)}</td>` +
      shown.map(k => `<td class="${cls[k]||""}">${k === "Status" ? `<span class="pill ${esc(kind(r.Status))}" title="${esc((LEVELS.find(l => l[0] === kind(r.Status)) || ["", ""])[1])}">${esc(r.Status)}</span>`
        : meaning(k, r[k]) ? `<span class="tag" title="${esc(meaning(k, r[k]))}">${esc(r[k])}</span>` : esc(r[k])}</td>`).join("") + "</tr>").join("") +
    "</tbody></table></div></section>";
}).join("");
const q = document.getElementById("q"), count = document.getElementById("count");
const filter = () => {
  const t = q.value.trim().toLowerCase(); let seen = 0;
  for (const s of document.querySelectorAll("[data-cat]")) {
    let any = false;
    for (const tr of s.querySelectorAll("tbody tr")) { const on = !t || tr.dataset.text.includes(t); tr.hidden = !on; any = any || on; if (on) seen++; }
    s.hidden = !any;
  }
  count.textContent = t ? `${seen} of ${rest.length}` : "";
  document.getElementById("none").style.display = seen ? "none" : "block";
};
q.addEventListener("input", filter);
for (const c of document.querySelectorAll(".stack td:not([data-l])")) { const t = c.closest("table"); c.dataset.l = t.rows[0].cells[c.cellIndex]?.textContent || ""; }
}
</script>
</body></html>
"""

def main():
    rows = list(csv.DictReader(open(CSV, newline="")))
    page = PAGE.replace("__ROWS__", json.dumps(rows, ensure_ascii=False).replace("</", "<\\/")).replace("__COLS__", json.dumps(COLUMNS))
    os.makedirs(os.path.dirname(os.path.abspath(OUT)), exist_ok=True)
    open(OUT, "w").write(page)
    print("%d mods -> %s" % (len(rows), OUT))

main()
