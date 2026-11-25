#!/usr/bin/env python3
"""Rewrites the at-a-glance tables in Docs/Compatibility.md from Docs/Compatibility.csv: one table per category,
under its own heading, with a column left out of a table when every row in it is empty.

    python3 anim_scripts/compat_table.py

Edit the CSV (the one list), run this, and commit both. The tables sit between the two marker comments."""
import csv, os, re, sys

DOCS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "M3AT: Maru's 3D Animated Toons", "Docs")
BEGIN, END = "<!-- compat-table:begin (made by anim_scripts/compat_table.py from Compatibility.csv; edit the CSV) -->", "<!-- compat-table:end -->"
COLUMNS = ["Status", "Clothes", "Also needs", "What works", "Known gaps", "Settings"]

def cell(text):
    return text.replace("|", "\\|").replace("\n", " ")

def main():
    rows = list(csv.DictReader(open(os.path.join(DOCS, "Compatibility.csv"), newline="")))
    categories = []
    for r in rows:
        if r["Category"] not in categories:
            categories.append(r["Category"])
    out = [BEGIN, ""]
    for c in categories:
        group = [r for r in rows if r["Category"] == c]
        shown = [k for k in COLUMNS if any(r[k] for r in group)]
        out.append("### " + c)
        out.append("")
        out.append("| Mod | " + " | ".join(shown) + " |")
        out.append("|---|" + "---|" * len(shown))
        for r in group:
            name = "[%s](%s)" % (r["Mod"], r["Workshop"]) if r["Workshop"] else r["Mod"]
            out.append("| " + name + " | " + " | ".join(cell(r[k]) for k in shown) + " |")
        out.append("")
    out.append(END)
    path = os.path.join(DOCS, "Compatibility.md")
    text = open(path).read()
    block = "\n".join(out)
    if BEGIN in text:
        text = re.sub(re.escape(BEGIN) + ".*?" + re.escape(END), lambda m: block, text, flags=re.S)
    else:
        marker = "## Required mods"
        if marker not in text:
            sys.exit("no marker and no '## Required mods' heading")
        text = text.replace(marker, block + "\n\n" + marker, 1)
    open(path, "w").write(text)
    print("%d mods in %d categories" % (len(rows), len(categories)))

main()
