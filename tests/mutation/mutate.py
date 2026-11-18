"""Mutation check: one deliberate bug at a time, the tests run, the file put back whatever happens.

    python tests/mutation/mutate.py <list.py> [--shard I/N] [--only TEXT]... [--no-baseline]

A list (tests/mutation/lists/*.py, here and in Fonts.Lib) defines M, a list of
(name, file, tests, old, new): `file` is relative to the repository the list is in, `old` must be found in it
exactly once, and `tests` says which tests have to fail: 'codecs', 'fonts', or 'both' (Codecs first, then Fonts.Lib).
The repositories are expected side by side (Codecs/ and Fonts.Lib/ in one folder), so that Fonts.Lib builds against
the Codecs source and a bug in the reader reaches its tests.

Every bug is a rebuild and a test run, a minute or more each: the whole of a list is for CI
(.github/workflows/mutation.yml, split into shards). On a working machine use --only for the few that matter.
The exit code is 0 only when the unmutated tests pass and every bug was applied, built and caught.
"""
import argparse
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))      # the folder that holds Codecs/ and Fonts.Lib/
TESTS = {
    'codecs': ('Codecs', ['dotnet', 'test', 'tests/SharpAstro.Codecs.Tests/SharpAstro.Codecs.Tests.csproj', '-c', 'Release',
                          '--filter', 'FullyQualifiedName~Svg|FullyQualifiedName~VectorScene']),
    'fonts': ('Fonts.Lib', ['dotnet', 'test', 'tests/SharpAstro.Fonts.Tests', '-c', 'Release',
                            '--filter', 'FullyQualifiedName~SceneText|FullyQualifiedName~SceneEffects|FullyQualifiedName~SceneRasterizer']),
}
TIMEOUT = 1200   # seconds for one build and test run; a bug that makes a test hang counts as caught


def run(kind, show=False):
    """(verdict, detail, skipped) of one test run. With `show`, failing tests' messages are printed."""
    repo, command = TESTS[kind]
    try:
        p = subprocess.run(command, cwd=os.path.join(ROOT, repo), capture_output=True, text=True, timeout=TIMEOUT)
        out = p.stdout + p.stderr
    except subprocess.TimeoutExpired:
        return 'CAUGHT (timeout)', '', 0
    m = re.search(r'(Failed|Passed)!\s+- Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+)', out)
    if m is None:
        errors = re.findall(r'error [A-Z]+\d+: [^\[]+', out)
        return 'BUILD ERROR', (errors[0][:200] if errors else out[-400:]), 0
    if int(m.group(2)) > 0:
        if show:
            # What failed and why, as the test runner printed it (the lines from each [FAIL] to the next result).
            # Again, with the logger that prints each failing test's message (its summary line is another, so
            # the verdict comes from the first run).
            try:
                again = subprocess.run(command + ['--logger', 'console;verbosity=normal'], cwd=os.path.join(ROOT, repo),
                                       capture_output=True, text=True, timeout=TIMEOUT)
                text = again.stdout + again.stderr
                print(text[max(0, text.find('  Failed ') - 200):][:20000], flush=True)
            except subprocess.TimeoutExpired:
                pass
        failed = sorted(set(re.findall(r'\]\s+SharpAstro\.[\w.]+\.(\w+\.\w+)[^\n]*\[FAIL\]', out)))
        return 'CAUGHT by %s' % m.group(2), ', '.join(failed)[:300], int(m.group(4))
    return 'MISSED', '%s passed' % m.group(3), int(m.group(4))


def available(kind):
    return os.path.isdir(os.path.join(ROOT, TESTS[kind][0]))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('list')
    parser.add_argument('--shard', default='0/1', help='I/N: every Nth bug, starting at the Ith')
    parser.add_argument('--only', action='append', default=[], help='only bugs whose name contains this')
    parser.add_argument('--no-baseline', action='store_true', help='do not run the unmutated tests first')
    args = parser.parse_args()

    listed = os.path.abspath(args.list)
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(listed))))   # <repo>/tests/mutation/lists/x.py
    scope = {}
    exec(compile(open(listed, encoding='utf-8').read(), listed, 'exec'), scope)
    index, count = (int(x) for x in args.shard.split('/'))
    bugs = [b for i, b in enumerate(scope['M']) if i % count == index]
    if args.only:
        bugs = [b for b in bugs if any(o in b[0] for o in args.only)]

    results = []      # (name, verdict, detail)
    ok = True

    # The unmutated tests first: a bug is only "caught" if the tests pass without it, and tests that are skipped
    # (no browser on this machine) catch nothing, so the number skipped is printed.
    if not args.no_baseline:
        kinds = sorted({k for b in bugs for k in (('codecs', 'fonts') if b[2] == 'both' else (b[2],))})
        for kind in kinds:
            if not available(kind):
                results.append(('(unmutated, %s)' % kind, 'NOT RUN', TESTS[kind][0] + ' is not beside this repository'))
                continue
            verdict, detail, skipped = run(kind, show=True)
            good = verdict == 'MISSED'
            ok &= good
            results.append(('(unmutated, %s)' % kind, 'PASS' if good else 'BASELINE ' + verdict, '%s, %d skipped' % (detail, skipped)))
            print(results[-1], flush=True)

    for name, relative, tests, old, new in bugs if ok else []:
        path = os.path.join(repo_root, relative)
        raw = open(path, 'rb').read()
        bom = raw.startswith(b'\xef\xbb\xbf')
        crlf = raw.count(b'\r\n') > raw.count(b'\n') // 2
        text = raw[3 if bom else 0:].decode('utf-8').replace('\r\n', '\n')
        if text.count(old) != 1:
            results.append((name, 'NOT APPLIED', '%d matches in %s' % (text.count(old), relative)))
            print(results[-1], flush=True)
            continue
        mutated = text.replace(old, new)
        if crlf:
            mutated = mutated.replace('\n', '\r\n')
        try:
            open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + mutated.encode('utf-8'))
            verdict, detail = 'MISSED', ''
            for kind in (('codecs', 'fonts') if tests == 'both' else (tests,)):
                if not available(kind):
                    detail = (detail + '; ' if detail else '') + TESTS[kind][0] + ' is not beside this repository, its tests did not run'
                    continue
                verdict, detail, _ = run(kind)
                if verdict != 'MISSED':
                    if verdict.startswith('CAUGHT') and tests == 'both':
                        verdict += ' (%s)' % kind
                    break
        finally:
            open(path, 'wb').write(raw)
        results.append((name, verdict, detail))
        print(results[-1], flush=True)

    caught = sum(r[1].startswith('CAUGHT') for r in results)
    missed = [r for r in results if r[1] == 'MISSED']
    other = [r for r in results if not r[1].startswith('CAUGHT') and r[1] not in ('MISSED', 'PASS')]
    summary = ['### Mutation check: %s, shard %s' % (os.path.basename(listed), args.shard), '',
               '%d caught, %d missed, %d not judged.' % (caught, len(missed), len(other)), '',
               '| Bug | Verdict | Detail |', '|---|---|---|']
    for name, verdict, detail in results:
        mark = '**%s**' % verdict if (name, verdict, detail) in missed or (name, verdict, detail) in other else verdict
        summary.append('| %s | %s | %s |' % (name.replace('|', '\\|'), mark, detail.replace('|', '\\|').replace('\n', ' ')))
    print('\n'.join(summary))
    target = os.environ.get('GITHUB_STEP_SUMMARY')
    if target:
        with open(target, 'a', encoding='utf-8') as f:
            f.write('\n'.join(summary) + '\n\n')
    return 0 if ok and not missed and not other else 1


if __name__ == '__main__':
    sys.exit(main())
