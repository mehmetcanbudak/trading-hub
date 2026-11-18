#!/usr/bin/env python3
"""Approve individually reviewed source paths in tools/english-localization-review.json.

usage: python3 tools/TradingHub.I18n/approve_review.py <reviews.json>   (reviews.json: {"src/...cs": "reason", ...}; build Release first)
Exports fingerprints with the unchanged audit algorithm, then edits only the touched lines.
"""
import json, os, pathlib, shutil, subprocess, sys, tempfile

root = str(pathlib.Path(__file__).resolve().parents[2])
reviews_path = sys.argv[1]
dotnet = shutil.which('dotnet') or os.path.expanduser('~/.dotnet/dotnet')
reviews = json.load(open(reviews_path))
dll = os.path.join(root, 'tools/TradingHub.UiSnapshot/bin/Release/net10.0/TradingHub.UiSnapshot.dll')
out = os.path.join(tempfile.mkdtemp(), 'fingerprints.json')
subprocess.run([dotnet, dll, '--source-fingerprints', root, reviews_path, out], check=True)
prints = json.load(open(out))['fingerprints']
mpath = os.path.join(root, 'tools/english-localization-review.json')
lines = open(mpath, encoding='utf-8').read().split('\n')


def section_bounds(name):
    start = next(i for i, l in enumerate(lines) if l.startswith(f'  "{name}": {{'))
    end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith('  }'))
    return start, end


def put(section, key, value):
    start, end = section_bounds(section)
    entry = f'    {json.dumps(key)}: {json.dumps(value, ensure_ascii=False)}'
    for i in range(start + 1, end):
        if lines[i].startswith(f'    {json.dumps(key)}:'):
            lines[i] = entry + (',' if lines[i].rstrip().endswith(',') else '')
            return 'updated'
    keys = [(i, json.loads('{' + lines[i].rstrip(',') + '}')) for i in range(start + 1, end)]
    pos = next((i for i, d in keys if next(iter(d)) > key), end)
    if pos == end:
        lines[end - 1] = lines[end - 1].rstrip(',') + ','
        lines.insert(end, entry)
    else:
        lines.insert(pos, entry + ',')
    return 'inserted'


changed = {}
for path, data in prints.items():
    changed[path] = (put('approvedStructure', path, data['approvedStructure']), put('reviewedFiles', path, data['reviewReason']))
open(mpath, 'w', encoding='utf-8').write('\n'.join(lines))
json.load(open(mpath))
print(json.dumps(changed))
