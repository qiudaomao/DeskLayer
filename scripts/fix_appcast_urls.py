#!/usr/bin/env python3
"""Point every enclosure at the GitHub release that actually holds it.

generate_appcast takes one --download-url-prefix and applies it to every
item, so a run that publishes 1.0.1 rewrites 1.0.0's URL into the 1.0.1
release, where that file does not exist. Each item's assets live in the
release named after its own version — deltas included, since a delta is
downloaded when updating *to* that version.
"""
import re
import sys

BASE = "https://github.com/qiudaomao/DeskLayer/releases/download"

path = sys.argv[1]
xml = open(path).read()

def fix_item(match):
    item = match.group(0)
    version = re.search(r"<sparkle:shortVersionString>([^<]+)", item)
    if not version:
        return item
    tag = version.group(1)
    return re.sub(r'url="[^"]*/([^/"]+)"', lambda m: f'url="{BASE}/{tag}/{m.group(1)}"', item)

fixed = re.sub(r"<item>.*?</item>", fix_item, xml, flags=re.S)
open(path, "w").write(fixed)
print("rewrote enclosure URLs per release tag")
