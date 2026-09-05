# -*- coding: utf-8 -*-
"""Refuse a mutation ledger whose entries cannot possibly mean anything.

TWO HARNESSES, ONE PRE-CHECK. The wall-split and model-doctor harnesses are
different systems with different anchors, different roots and different
evidence, and they are deliberately NOT merged - merging them would flatten two
independently-earned ledgers into one number. But both express a mutation as the
same 5-tuple (label, path, find, replace, test_filter), so the question "can this
entry possibly bite?" is the same question for both and is asked here once.

A ledger costs an hour or more. Every check below is something that would
otherwise be discovered at the END of that hour, as a VACUOUS or ANCHOR-MISSING
line in a report:

  * an anchor that no longer appears in the file - the code moved under it;
  * a replacement identical to its anchor - a mutation that changes nothing;
  * an anchor that appears MORE THAN ONCE - the harness replaces the first
    occurrence, so the entry mutates whichever one happens to come first, and
    the ledger's result does not mean what its label says;
  * two entries sharing an id - two results reported under one name;
  * a mutation in a file the Core test project does not COMPILE, whose guarding
    test does not read that file's source. Nothing executes the mutated code,
    so the entry can only come back VACUOUS however convincing its name.

Read-only. Exit 0 when every entry in the named harness could bite.

    python scripts/mutation-preflight.py scripts/wall-split-mutation-harness.py
"""
import ast
import io
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def literal(node):
    """The constant value of an AST node, or None when it is not a literal."""
    try:
        return ast.literal_eval(node)
    except Exception:
        return None


def module_strings(src):
    """Module-level string constants, so a path given as a NAME resolves.

    Both harnesses write some entries as (label, REQ, ...) where REQ is a
    module constant rather than a literal. Reading those names is the
    difference between checking every entry and silently checking half of them.
    """
    out = {}
    for node in ast.parse(src).body:
        if isinstance(node, ast.Assign):
            value = literal(node.value)
            if isinstance(value, str):
                for target in node.targets:
                    if getattr(target, 'id', None):
                        out[target.id] = value
    return out


def resolve_expr(node, names):
    """A literal, a module constant by name, or a concatenation of those.

    The wall harness writes every path as `SRC + 'File.cs'`; the doctor harness
    writes some as a bare constant. A pre-check that understands only literals
    silently skips whichever style it does not speak, and reporting "file not
    found" for all 81 entries of a healthy harness is worse than not checking.
    """
    lit = literal(node)
    if lit is not None:
        return lit
    name = getattr(node, 'id', None)
    if name is not None:
        return names.get(name)
    if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Add):
        left = resolve_expr(node.left, names)
        right = resolve_expr(node.right, names)
        if isinstance(left, str) and isinstance(right, str):
            return left + right
    return None


def load_mutations(harness_path):
    """The MUTATIONS list, read from the source rather than by importing it.

    Importing would run the module, and the module's job is to edit this
    repository. A pre-check that mutates the tree to decide whether mutating
    the tree is safe has already lost.
    """
    src = io.open(harness_path, encoding='utf-8').read()
    tree = ast.parse(src)
    for node in tree.body:
        if isinstance(node, ast.Assign) and getattr(node.targets[0], 'id', '') == 'MUTATIONS':
            names = module_strings(src)
            out = []
            for element in node.value.elts:
                values = []
                for v in element.elts:
                    values.append(resolve_expr(v, names))
                if len(values) != 5 or any(v is None for v in values[2:]):
                    out.append(None)          # not a plain 5-tuple of literals
                else:
                    out.append(tuple(values))
            return out, src
    return None, src


def roots_of(src):
    """The path prefixes the harness joins its relative paths onto."""
    found = {}
    tree = ast.parse(src)
    for node in tree.body:
        if isinstance(node, ast.Assign):
            name = getattr(node.targets[0], 'id', '')
            if name in ('SRC', 'CORE', 'CMD', 'BASE'):
                value = literal(node.value)
                if isinstance(value, str):
                    found[name] = value
    return found


def resolve(rel, roots):
    """Every path this entry could mean, most likely first."""
    if rel is None:
        return []
    candidates = []
    for prefix in list(roots.values()) + ['']:
        candidates.append(os.path.normpath(os.path.join(ROOT, prefix + rel)))
    seen, out = set(), []
    for c in candidates:
        if c not in seen:
            seen.add(c)
            out.append(c)
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    harness = os.path.join(ROOT, sys.argv[1]) if not os.path.isabs(sys.argv[1]) else sys.argv[1]
    if not os.path.exists(harness):
        print('no such harness: ' + harness)
        return 2

    mutations, src = load_mutations(harness)
    if mutations is None:
        print('no MUTATIONS list in ' + harness)
        return 2
    roots = roots_of(src)

    # What the Core test project actually COMPILES. Anything outside it is code
    # a behavioural test cannot execute, so only a test that READS ITS SOURCE
    # can bite - and that test must name the file.
    csproj = os.path.join(ROOT, 'tests', 'Horizun.Core.Tests', 'Horizun.Core.Tests.csproj')
    linked = set()
    if os.path.exists(csproj):
        for line in io.open(csproj, encoding='utf-8'):
            if '<Compile Include=' in line:
                linked.add(os.path.basename(line.split('"')[1].replace('\\', '/')))

    tests_dir = os.path.join(ROOT, 'tests', 'Horizun.Core.Tests')
    test_text = {}
    if os.path.isdir(tests_dir):
        for name in sorted(os.listdir(tests_dir)):
            if name.endswith('.cs'):
                test_text[name] = io.open(os.path.join(tests_dir, name), encoding='utf-8').read()

    problems = []
    seen_ids = set()
    for index, entry in enumerate(mutations):
        if entry is None:
            problems.append('entry %d is not a 5-tuple of literals' % index)
            continue
        label, rel, find, replace, test_filter = entry
        # THE WHOLE LABEL, not its first token. Both harnesses print the full
        # label in their report, and only the doctor's happens to start each one
        # with a unique code - splitting on the space imposed that convention on
        # the wall harness and reported twenty duplicates in a healthy list.
        # What actually matters is that two results cannot be told apart.
        mid = str(label)
        if mid in seen_ids:
            problems.append('duplicate label, so two results are indistinguishable: %s' % mid)
        seen_ids.add(mid)

        if find == replace:
            problems.append('%s: the replacement is identical to the anchor, so it changes nothing' % mid)

        paths = [p for p in resolve(rel, roots) if os.path.exists(p)]
        if not paths:
            problems.append('%s: file not found (%s)' % (mid, rel))
            continue
        path = paths[0]
        text = io.open(path, encoding='utf-8').read()
        occurrences = text.count(find)
        if occurrences == 0:
            problems.append('%s: anchor not found in %s' % (mid, os.path.relpath(path, ROOT)))
            continue
        if occurrences > 1:
            problems.append(
                '%s: the anchor appears %d times in %s. The harness replaces the FIRST, so this entry '
                'mutates whichever comes first and its result does not mean what its label says.'
                % (mid, occurrences, os.path.relpath(path, ROOT)))

        basename = os.path.basename(rel)
        if basename not in linked:
            guarded = any(test_filter in body and basename in body for body in test_text.values())
            if not guarded:
                problems.append(
                    '%s: mutates %s, which the Core test project does not compile, and its test "%s" is in '
                    'no test file that reads that source. Nothing executes the mutated code, so this can '
                    'only come back VACUOUS.' % (mid, rel, test_filter))

    name = os.path.basename(harness)
    if problems:
        print('PREFLIGHT REFUSED %s:' % name)
        for p in problems:
            print('  ' + p)
        return 1
    print('preflight %s: %d mutations, anchors resolve and are unique, ids unique, '
          'unlinked-source entries have naming guards.' % (name, len(mutations)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
