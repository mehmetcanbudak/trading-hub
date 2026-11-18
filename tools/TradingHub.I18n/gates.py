#!/usr/bin/env python3
"""Localization gates for the canonical checkout.

usage: python3 tools/TradingHub.I18n/gates.py [translations.json]   (run from any directory; build Release first)
translations.json (optional): {"English key": {"es": "...", "zh-CN": "..."}} added to overrides.json and published.
Then: extract the current catalog, move reviewed non-display exclusions whose locations shifted (reasons kept),
check es/zh-CN with --require-complete, and run the English checker.
"""
import json, os, pathlib, shutil, subprocess, sys, tempfile

root = pathlib.Path(__file__).resolve().parents[2]
dotnet = shutil.which('dotnet') or os.path.expanduser('~/.dotnet/dotnet')
i18n = root / 'tools/TradingHub.I18n'
overrides = i18n / 'overrides.json'

if len(sys.argv) > 1:
    new = json.load(open(sys.argv[1], encoding='utf-8'))
    text = overrides.read_text(encoding='utf-8').rstrip()
    existing = json.loads(text)
    added = [k for k in new if k not in existing]
    assert text.endswith('}')
    body = ''.join(',\n  ' + json.dumps(k, ensure_ascii=False) + ': {\n    "es": ' + json.dumps(new[k]['es'], ensure_ascii=False)
                   + ',\n    "zh-CN": ' + json.dumps(new[k]['zh-CN'], ensure_ascii=False) + '\n  }' for k in added)
    overrides.write_text(text[:-1].rstrip() + body + '\n}\n', encoding='utf-8')
    json.loads(overrides.read_text(encoding='utf-8'))
    sys.path.insert(0, str(i18n))
    import rebase
    rebase.publish(i18n / 'provenance.json', overrides, root / 'src/TradingHub.App/Localization')
    print('added translations:', added)

dll = i18n / 'bin/Release/net10.0/TradingHub.I18n.dll'
cat = pathlib.Path(tempfile.mkdtemp()) / 'catalog.json'
subprocess.run([dotnet, str(dll), 'extract', 'src', str(cat)], cwd=root, check=True, capture_output=True)
catalog = json.loads(cat.read_text())
excl_path = i18n / 'non-display-exclusions.json'
excl = json.loads(excl_path.read_text())
text = excl_path.read_text()
moved = []
for key, value in excl.items():
    now = catalog.get(key)
    if now is not None and now != value['locations']:
        # Several entries may share one location string, so replace only inside this key's own entry.
        anchor = '  ' + json.dumps(key, ensure_ascii=False) + ': {'
        start = text.find(anchor)
        assert start >= 0 and text.count(anchor) == 1, key
        old = '"locations": ' + json.dumps(value['locations'], ensure_ascii=False)
        at = text.find(old, start)
        end = text.find('\n  }', start)
        assert 0 <= at < end, key
        text = text[:at] + '"locations": ' + json.dumps(now, ensure_ascii=False) + text[at + len(old):]
        moved.append(key)
excl_path.write_text(text)
print('moved exclusions:', moved)
ok = True
for lang in ('es', 'zh-CN'):
    r = subprocess.run([dotnet, str(dll), 'check', str(cat), f'src/TradingHub.App/Localization/{lang}.json', '--require-complete',
                        '--reviewed-exclusions', 'tools/TradingHub.I18n/non-display-exclusions.json'], cwd=root, capture_output=True, text=True)
    print(lang, r.returncode, r.stdout.strip().splitlines()[-1] if r.stdout.strip() else r.stderr.strip()[-300:])
    ok &= r.returncode == 0
r = subprocess.run(['python3', 'tools/check_english_translation.py', '--ui-snapshot-assembly',
                    'tools/TradingHub.UiSnapshot/bin/Release/net10.0/TradingHub.UiSnapshot.dll'], cwd=root, capture_output=True, text=True,
                   env={**os.environ, 'PYTHONDONTWRITEBYTECODE': '1'})
print('english', r.returncode, (r.stdout.strip().splitlines() or [''])[-1], r.stderr.strip()[-600:] if r.returncode else '')
ok &= r.returncode == 0
sys.exit(0 if ok else 1)
