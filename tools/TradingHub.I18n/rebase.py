#!/usr/bin/env python3
"""Rebase upstream Spanish catalogs using checkpoint/fork C# syntax correspondence.

Requires tree-sitter==0.25.2 and tree-sitter-c-sharp==0.23.5 (install with pip).
Inputs are immutable Git revisions, never the user's settings or data. The output records
all upstream keys, including unresolved correspondence, so coverage cannot silently shrink.
"""
import argparse
import collections
import difflib
import itertools
import json
import pathlib
import re
import subprocess

from tree_sitter import Language, Parser
import tree_sitter_c_sharp

HOLE = re.compile(r"(?<!\{)\{(\d+)\}(?!\})")
STRINGS = {"string_literal", "verbatim_string_literal", "raw_string_literal", "interpolated_string_expression"}
parser = Parser(Language(tree_sitter_c_sharp.language()))
lookup_en, lookup_zh = {}, {}


def git(root, *args):
    result = subprocess.run(["git", *args], cwd=root, capture_output=True)
    return result.stdout.decode() if result.returncode == 0 else ""


def walk(node):
    yield node
    for child in node.named_children:
        yield from walk(child)


def atoms(node):
    if node.type in STRINGS:
        yield "STRING", node
    elif node.type != "comment":
        if not node.children:
            yield node.text.decode(), node
        for child in node.children:
            yield from atoms(child)


def unescape(text):
    escapes = {"n": "\n", "r": "\r", "t": "\t", "0": "\0", "a": "\a", "b": "\b", "f": "\f", "v": "\v"}
    return re.sub(r"\\(u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|x[0-9a-fA-F]{1,4}|.)", lambda m: chr(int(m[1][1:], 16)) if m[1][0] in "uUx" else escapes.get(m[1], m[1]), text)


def literal(node):
    text = node.text.decode()
    if text.startswith('@"'):
        return text[2:-1].replace('""', '"')
    if text.startswith('"""'):
        return text.strip('"').strip('\n')
    return unescape(text[1:-1])


def escape(text):
    return text.replace("{", "{{").replace("}", "}}")


def parts(node):
    if node.type == "binary_expression" and any(c.type == "+" for c in node.children):
        yield from parts(node.named_children[0])
        yield from parts(node.named_children[-1])
    else:
        yield node


def template(node):
    """Return a catalog key and its interpolation expressions in source order."""
    if node.type in STRINGS - {"interpolated_string_expression"}:
        return escape(literal(node)), []
    if node.type == "interpolated_string_expression":
        text, expressions = "", []
        for child in node.named_children:
            if child.type in ("string_content", "string_literal_content"):
                content = child.text.decode()
                if not node.text.startswith(b'$@') and not node.text.startswith(b'@$'):
                    content = unescape(content)
                text += escape(content.replace("{{", "{").replace("}}", "}"))
            elif child.type == "interpolation":
                text += "{" + str(len(expressions)) + "}"
                expressions.append(next(c for c in child.named_children if c.type not in ("interpolation_brace", "interpolation_alignment_clause", "interpolation_format_clause")))
        return text, expressions
    if node.type == "binary_expression" and any(c.type == "+" for c in node.children):
        pp = list(parts(node))
        if not any(p.type in STRINGS for p in pp):
            return None, []
        text, expressions = "", []
        for p in pp:
            if p.type in STRINGS:
                inner, ee = template(p)
                offset = len(expressions)
                text += HOLE.sub(lambda m: "{" + str(int(m[1]) + offset) + "}", inner)
                expressions.extend(ee)
            else:
                text += "{" + str(len(expressions)) + "}"
                expressions.append(p)
        return text, expressions
    return None, []


def choices(expressions):
    """Only expand actual literal ternary branches; never infer Spanish morphology."""
    result = {}
    for i, expression in enumerate(expressions):
        while expression.type == "parenthesized_expression":
            expression = expression.named_children[0]
        if expression.type == "conditional_expression":
            condition, yes, no = expression.named_children
            if yes.type in STRINGS - {"interpolated_string_expression"} and no.type in STRINGS - {"interpolated_string_expression"}:
                result[i] = (re.sub(r"\s+", "", condition.text.decode()), literal(yes), literal(no))
    return result


def variants(old, new, english, chinese):
    old_key, oe = template(old)
    new_key, ne = template(new)
    oc, nc = choices(oe), choices(ne)
    conditions = list(dict.fromkeys(c[0] for c in [*oc.values(), *nc.values()]))
    if len(conditions) > 8:
        return []
    result = []
    for bits in itertools.product((True, False), repeat=len(conditions)):
        state = dict(zip(conditions, bits))
        ov = {i: c[1] if state[c[0]] else c[2] for i, c in oc.items()}
        nv = {i: c[1] if state[c[0]] else c[2] for i, c in nc.items()}
        # Remaining slots refer to data, not suffixes. Pair in expression order.
        old_data = [i for i in range(len(oe)) if i not in ov]
        new_data = [i for i in range(len(ne)) if i not in nv]
        if len(old_data) != len(new_data):
            continue
        old_indices = {i: j for j, i in enumerate(old_data)}
        new_indices = {i: j for j, i in enumerate(new_data)}
        def source(match):
            i = int(match[1])
            return escape(nv[i]) if i in nv else "{" + str(new_indices[i]) + "}"
        key = HOLE.sub(source, new_key)
        spanish = HOLE.sub(lambda m: escape(ov[int(m[1])]) if int(m[1]) in ov else "{" + str(old_indices[int(m[1])]) + "}", old_key)
        def translated(value, lang):
            def replace(match):
                i = int(match[1])
                if i not in ov:
                    return "{" + str(old_indices[i]) + "}"
                selected = ov[i]
                source_hole = next((m for m in HOLE.finditer(old_key) if int(m[1]) == i), None)
                if source_hole and source_hole.start() > 0 and old_key[source_hole.start()-1].isalpha() and selected in ("", "s", "es", "n"):
                    return "s" if lang == "en" and selected in ("s", "es") and match.start() > 0 and value[match.start()-1].isalpha() else ""
                lookup = lookup_en if lang == "en" else lookup_zh
                translated = lookup.get(escape(selected))
                if translated is None and selected and selected[0].islower():
                    translated = lookup.get(escape(selected[0].upper() + selected[1:]))
                    if translated and lang == "en":
                        translated = translated[0].lower() + translated[1:]
                return translated if translated is not None else escape(selected)
            return HOLE.sub(replace, value)
        result.append((key, spanish, translated(chinese, "zh"), translated(english, "en")))
    return list(dict.fromkeys(result))


def rebase(root, checkpoint, fork, upstream, output):
    global lookup_en, lookup_zh
    upstream_en = json.loads(git(root, "show", f"{upstream}:src/TradingHub.App/Localization/en.json"))
    upstream_zh = json.loads(git(root, "show", f"{upstream}:src/TradingHub.App/Localization/zh-CN.json"))
    lookup_en, lookup_zh = upstream_en, upstream_zh
    records = collections.defaultdict(list)
    paths = [p for p in git(root, "ls-tree", "-r", "--name-only", checkpoint, "src").splitlines() if p.endswith(".cs") and "/Atas/" not in p and not p.endswith(".g.cs")]
    for path in paths:
        old_source, new_source = git(root, "show", f"{checkpoint}:{path}"), git(root, "show", f"{fork}:{path}")
        if not new_source:
            continue
        old_tree, new_tree = parser.parse(old_source.encode()), parser.parse(new_source.encode())
        old_atoms, new_atoms = list(atoms(old_tree.root_node)), list(atoms(new_tree.root_node))
        paired = {}
        for block in difflib.SequenceMatcher(None, [a for a, _ in old_atoms], [a for a, _ in new_atoms], autojunk=False).get_matching_blocks():
            for i in range(block.size):
                a, on = old_atoms[block.a+i]
                if a == "STRING":
                    paired[on.start_byte] = new_atoms[block.b+i][1]
        for on in walk(old_tree.root_node):
            old_key, _ = template(on)
            if old_key not in upstream_en:
                continue
            nn = paired.get(on.start_byte)
            if nn is None and on.type == "binary_expression":
                first = next((p for p in parts(on) if p.start_byte in paired), None)
                if first:
                    nn = paired[first.start_byte]
                    while nn.parent and nn.type != "binary_expression":
                        nn = nn.parent
                    while nn.parent and nn.parent.type == "binary_expression":
                        nn = nn.parent
            if nn is None or nn.type != on.type or template(nn)[0] is None:
                continue
            for key, spanish, chinese, english in variants(on, nn, upstream_en[old_key], upstream_zh[old_key]):
                entry = {"key": key, "es": spanish, "zh-CN": chinese, "upstreamEnglish": english, "source": f"{path}:{on.start_point.row+1}"}
                if entry not in records[old_key]:
                    records[old_key].append(entry)
        # Do not retain syntax nodes: a full source tree is much larger than its text.
        paired.clear()
    aliases = collections.defaultdict(list)
    upstream_paths = [p for p in git(root, "ls-tree", "-r", "--name-only", upstream, "src").splitlines() if p.endswith(".cs") and "/Atas/" not in p and "/Localization/" not in p and not p.endswith(".g.cs")]
    for path in upstream_paths:
        source = git(root, "show", f"{upstream}:{path}")
        tree = parser.parse(source.encode())
        for node in walk(tree.root_node):
            key, _ = template(node)
            if key not in upstream_en:
                continue
            for _, spanish, chinese, english in variants(node, node, upstream_en[key], upstream_zh[key]):
                entry = {"key": english, "es": spanish, "zh-CN": chinese, "source": f"{path}:{node.start_point.row+1}"}
                if entry not in aliases[key]:
                    aliases[key].append(entry)
    for key, english in upstream_en.items():
        if not aliases[key]:
            aliases[key].append({"key": english, "es": key, "zh-CN": upstream_zh[key], "source": "upstream dictionary"})
    provenance = {"checkpoint": checkpoint, "fork": fork, "upstream": upstream, "entries": {key: {"english": english, "chinese": upstream_zh[key], "correspondence": records[key], "aliases": aliases[key]} for key, english in upstream_en.items()}}
    output.write_text(json.dumps(provenance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{sum(bool(v) for v in records.values())}/{len(upstream_en)} upstream keys have checkpoint/fork correspondence; all {len(upstream_en)} retain upstream aliases. Publishing requires explicit collision choices.")


def publish(provenance_path, overrides_path, folder):
    provenance = json.loads(provenance_path.read_text(encoding="utf-8"))
    overrides = json.loads(overrides_path.read_text(encoding="utf-8"))
    candidates = collections.defaultdict(list)
    for original, entry in provenance["entries"].items():
        for kind in ("aliases", "correspondence"):
            for item in entry[kind]:
                candidates[item["key"]].append((kind, original, item))
    catalogs = {"es": {}, "zh-CN": {}}
    collisions = {}
    for key, items in candidates.items():
        distinct = {(i["es"], i["zh-CN"]) for _, _, i in items}
        if len(distinct) > 1:
            collisions[key] = [{"kind": kind, "original": original, **item} for kind, original, item in items]
            if key not in overrides:
                raise ValueError(f"Unreviewed collision: {key!r}; add an explicit choice to {overrides_path}")
        # Exact fork-source correspondence precedes upstream English aliases.
        selected = next((item for kind, _, item in items if kind == "correspondence"), items[0][2])
        for language in catalogs:
            catalogs[language][key] = overrides.get(key, selected)[language]
    for key, values in overrides.items():
        for language in catalogs:
            catalogs[language][key] = values[language]
    folder.mkdir(parents=True, exist_ok=True)
    for language, catalog in catalogs.items():
        bad = [key for key, value in catalog.items() if sorted(HOLE.findall(key)) != sorted(HOLE.findall(value))]
        if bad:
            raise ValueError(f"{language}: mismatched data holes in {bad}")
        (folder / f"{language}.json").write_text(json.dumps(dict(sorted(catalog.items())), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (overrides_path.parent / "collisions.json").write_text(json.dumps(collisions, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Published {len(catalogs['es'])} English-source keys; {len(collisions)} explicitly reviewed collisions.")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("root", type=pathlib.Path)
    ap.add_argument("checkpoint")
    ap.add_argument("fork")
    ap.add_argument("upstream")
    ap.add_argument("output", type=pathlib.Path)
    ap.add_argument("--publish", type=pathlib.Path, help="Publish catalogs after applying reviewed collision/fork overrides")
    ap.add_argument("--overrides", type=pathlib.Path)
    args = ap.parse_args()
    rebase(args.root, args.checkpoint, args.fork, args.upstream, args.output)
    if args.publish:
        if not args.overrides:
            ap.error("--publish requires --overrides")
        publish(args.output, args.overrides, args.publish)
