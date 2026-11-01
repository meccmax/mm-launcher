"""
Originality check: how much of MM Launcher's source also appears in other projects.

  python simcheck.py <our src folder> <folder of other projects>
  python simcheck.py --show <our file> <their file>     # print the matching runs

Comments are removed. In code (.cs/.ts/.js/.py) the contents of string literals are blanked, so
only code counts; in markup (.axaml/.xaml) attribute values are kept, since they are the content.
  exact     = share of our 20-token runs that appear word for word in one other file
  structure = share of our 40-token runs that match once every name is replaced by "ID"
              (catches copied code with renamed variables; meaningless for XAML, where
              almost everything is a name)
Run it from a folder of its own; it only reads files.
"""
import collections, os, re, sys

EXT = ('.cs', '.ts', '.js', '.vue', '.py', '.axaml', '.xaml')
TOK = re.compile(r'[A-Za-z_][A-Za-z0-9_]*|\d+|==|=>|!=|<=|>=|&&|\|\||\S')
KW = set("""abstract as base bool break byte case catch char checked class const continue decimal default delegate do
double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal
is lock long namespace new null object operator out override params private protected public readonly ref return sbyte
sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort
using virtual void volatile while var async await get set init record yield def import from let function export""".split())
K_EXACT, K_NORM = 20, 40


def strip(text, ext):
    if ext == '.py':
        text = re.sub(r'#.*', '', text)
    else:
        text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
        text = re.sub(r'//.*', '', text)
        text = re.sub(r'<!--.*?-->', '', text, flags=re.S)
    if ext in ('.axaml', '.xaml'):
        return text  # in markup the attribute values are the content, so they count
    return re.sub(r'@?"(?:[^"\\\n]|\\.)*"', '"S"', text)


def tokens(path):
    try:
        text = open(path, encoding='utf-8', errors='ignore').read()
    except OSError:
        return [], []
    toks = TOK.findall(strip(text, os.path.splitext(path)[1]))
    norm = [t if (t in KW or not re.match(r'[A-Za-z_]', t)) else 'ID' for t in toks]
    return toks, norm


def shingles(seq, k):
    return {hash(tuple(seq[i:i + k])) for i in range(len(seq) - k + 1)}


def walk(root):
    for d, ds, fs in os.walk(root):
        ds[:] = [x for x in ds if x not in ('bin', 'obj', 'node_modules', '.git')]
        for f in fs:
            if f.endswith(EXT):
                yield os.path.join(d, f)


def show(a, b, k=K_EXACT):
    ta, _ = tokens(a)
    tb, _ = tokens(b)
    sb = {tuple(tb[i:i + k]) for i in range(len(tb) - k + 1)}
    i, runs = 0, []
    while i < len(ta) - k + 1:
        if tuple(ta[i:i + k]) in sb:
            j = i + k
            while j < len(ta) and tuple(ta[j - k + 1:j + 1]) in sb:
                j += 1
            runs.append(' '.join(ta[i:j]))
            i = j
        else:
            i += 1
    for r in runs:
        print('  >', r[:400])
    print(f'  {len(runs)} matching run(s)')


def main(ours_root, ext_root, top=30):
    ext_files = list(walk(ext_root))
    exact_index, norm_index = collections.defaultdict(set), collections.defaultdict(set)
    for i, p in enumerate(ext_files):
        t, n = tokens(p)
        for h in shingles(t, K_EXACT):
            exact_index[h].add(i)
        for h in shingles(n, K_NORM):
            norm_index[h].add(i)
    rows = []
    for p in walk(ours_root):
        if os.sep + 'MMLauncher.Tests' + os.sep in p:
            continue
        t, n = tokens(p)
        se, sn = shingles(t, K_EXACT), shingles(n, K_NORM)
        if not se:
            continue
        ce, cn = collections.Counter(), collections.Counter()
        for h in se:
            for i in exact_index.get(h, ()):
                ce[i] += 1
        for h in sn:
            for i in norm_index.get(h, ()):
                cn[i] += 1
        be = ce.most_common(1)[0] if ce else (None, 0)
        bn = cn.most_common(1)[0] if cn else (None, 0)
        rows.append((os.path.relpath(p, ours_root), len(t), be[1] / len(se),
                     os.path.relpath(ext_files[be[0]], ext_root) if be[0] is not None else '-',
                     bn[1] / max(1, len(sn)),
                     os.path.relpath(ext_files[bn[0]], ext_root) if bn[0] is not None else '-'))
    rows.sort(key=lambda r: -r[2])
    print(f'Compared {len(rows)} of our files ({sum(r[1] for r in rows):,} tokens) with {len(ext_files):,} files in {ext_root}')
    print(f'{"exact":>7} {"struct":>7}  file  ->  closest exact match')
    for name, ntok, e, ef, s, sf in rows[:top]:
        print(f'{e * 100:6.1f}% {s * 100:6.1f}%  {name} ({ntok} tokens)  ->  {ef}')


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == '--show':
        show(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 3:
        main(sys.argv[1], sys.argv[2])
    else:
        print(__doc__)
