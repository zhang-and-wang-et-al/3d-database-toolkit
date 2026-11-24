"""Tokenizer, document tree and writer for OpenSceneGraph's two text formats.

* ``.osgt`` - the serializer ("ascii scene") format of OSG 3.x
* ``.osg``  - the older ("deprecated") dot-osg format, still written by osgconv

Both are parsed into the same light-weight document tree (DOM):

``Stmt``  one statement: a list of tokens plus an optional block. The block is
          ``None``, a list of statements, a ``Num`` (a block that holds only
          numbers - vertex data, indices, matrices) or an ``Obj``.
``Obj``   an object with an identity (osgt ``UniqueID`` / legacy ``UniqueID`` +
          ``Use``). A shared object appears as the *same* ``Obj`` instance in
          several places, so sharing (instancing) survives a load/save round trip.

The tree is format agnostic enough that unknown classes (callbacks, shaders,
text, particles, ...) are written back unchanged.
"""

import re

import numpy as np

_TOKEN = re.compile(r'"[^"\\]*(?:\\.[^"\\]*)*"|[{}]|\n|[^\s{}"]+')
_UNESCAPE = re.compile(r"\\(.)", re.S)
_INT_LIKE = re.compile(r"^-?\d+$")


class Q(str):
    """A quoted string token (the value without quotes, escapes resolved)."""
    __slots__ = ()


class Num:
    """A block that only contains numbers."""
    __slots__ = ("data", "per_line")

    def __init__(self, data, per_line=0):
        self.data = data
        self.per_line = per_line

    def __len__(self):
        return len(self.data)


class Obj:
    """An object with identity. ``style`` is 'osgt', 'image' (osgt image block) or 'osg' (legacy)."""
    __slots__ = ("cls", "uid", "stmts", "style", "__weakref__")

    def __init__(self, cls, uid, stmts, style):
        self.cls = cls
        self.uid = uid
        self.stmts = stmts
        self.style = style

    def get(self, key):
        for s in self.stmts:
            if s.toks and s.toks[0] == key:
                return s
        return None

    def value(self, key, default=None, index=1):
        s = self.get(key)
        if s is None or len(s.toks) <= index:
            return default
        return s.toks[index]

    def __repr__(self):
        return f"<Obj {self.cls} #{self.uid}>"


class Stmt:
    __slots__ = ("toks", "block")

    def __init__(self, toks, block=None):
        self.toks = toks
        self.block = block

    @property
    def key(self):
        return self.toks[0] if self.toks else ""

    def __repr__(self):
        b = "" if self.block is None else (" {%s}" % type(self.block).__name__)
        return f"<Stmt {' '.join(self.toks[:6])}{b}>"


class Document:
    def __init__(self, dialect, stmts, header):
        self.dialect = dialect          # 'osgt' or 'osg'
        self.stmts = stmts              # top level statements
        self.header = header            # list of header lines (osgt only)

    @property
    def version(self):
        for line in self.header:
            parts = line.split()
            if len(parts) >= 2 and parts[0] == "#Version":
                try:
                    return int(parts[1])
                except ValueError:
                    return 0
        return 0

    def root_objects(self):
        return [s.block for s in self.stmts if isinstance(s.block, Obj)]


def _unquote(tok):
    inner = tok[1:-1]
    if "\\" in inner:
        inner = _UNESCAPE.sub(r"\1", inner)
    return Q(inner)


_WS = re.compile(r"\s*")
_NUM_START = frozenset("0123456789-+.")


def _numeric_block(text, start):
    """If the block starting right after '{' at ``start`` holds only numbers,
    return (Num, index after the closing brace); otherwise None."""
    i = _WS.match(text, start).end()
    if i >= len(text):
        return None
    c = text[i]
    if c not in _NUM_START and not text.startswith(("nan", "inf"), i):
        return None                     # a block of statements: decided without copying anything
    end = text.find("}", i)
    if end < 0:
        return None
    seg = text[start:end]
    if "{" in seg or '"' in seg:
        return None
    try:
        data = np.fromstring(seg, dtype=np.float64, sep=" ")   # C parser, no per-number Python objects
    except (ValueError, DeprecationWarning):
        return None
    if not len(data):
        return None
    per_line = 0
    start_line = 0
    while start_line < len(seg):
        nl = seg.find("\n", start_line)
        nl = len(seg) if nl < 0 else nl
        per_line = len(seg[start_line:nl].split())
        if per_line:
            break
        start_line = nl + 1
    return Num(data, per_line), end + 1


_ASCII_HEAD = re.compile(r"[\s\ufeff]*#Ascii")


def sniff_dialect(text):
    """'osgt' or 'osg' (without copying the text: str.lstrip() of a 1 GB file copies 1 GB)."""
    return "osgt" if _ASCII_HEAD.match(text) else "osg"


def parse(text, dialect=None, progress=None):
    """Parse osgt / legacy osg text into a ``Document``. ``progress(msg)`` gets the percentage read."""
    import warnings
    with warnings.catch_warnings():
        # old numpy versions only warn (and return a partial result) for non-numeric text
        warnings.filterwarnings("error", category=DeprecationWarning)
        return _parse(text, dialect, progress)


def _parse(text, dialect, progress=None):
    header = []
    pos = 0
    if dialect is None:
        dialect = sniff_dialect(text)
    if dialect == "osgt":
        # header lines: '#Ascii Scene', '#Version 161', '#Generator ...', '#CustomDomain ...'
        while True:
            m = re.compile(r"[ \t\r]*(#[^\n]*)\n?").match(text, pos)
            if not m or not m.group(1):
                # blank lines between header and body
                m2 = re.compile(r"[ \t\r]*\n").match(text, pos)
                if m2 and m2.end() > pos:
                    pos = m2.end()
                    continue
                break
            header.append(m.group(1).strip())
            pos = m.end()

    root = []
    stack = [root]
    n = len(text)
    search = _TOKEN.search
    find = text.find
    legacy = dialect == "osg"
    # keywords, class names and enums repeat millions of times in big files: keep one copy of each
    pool = {}
    keep = pool.setdefault

    def word(t):
        return keep(t, t) if (t[0].isalpha() or len(t) <= 2) else t

    step = max(n // 50, 1 << 20)
    next_report = step if progress else n + 1
    while pos < n:
        if pos >= next_report:
            progress(f"parsing ... {pos * 100 // n} % ({pos / 1e6:,.0f} of {n / 1e6:,.0f} MB)")
            next_report = pos + step
        nl = find("\n", pos)
        if nl < 0:
            nl = n
        line = text[pos:nl]
        if '"' not in line and not (legacy and "//" in line):
            ob, cb = line.count("{"), line.count("}")
            if not ob and not cb:
                # plain statement line (the vast majority): one statement, whitespace separated tokens
                toks = line.split()
                if toks:
                    stack[-1].append(Stmt([keep(t, t) if (t[0].isalpha() or len(t) <= 2) else t for t in toks]))
                pos = nl + 1
                continue
            stripped = line.strip()
            if cb == 1 and not ob and stripped == "}":
                if len(stack) > 1:
                    stack.pop()
                pos = nl + 1
                continue
            if ob == 1 and not cb and stripped.endswith("{") and len(stripped) > 1:
                # 'Key values {' - a statement with a block
                toks = stripped[:-1].split()
                target = Stmt([keep(t, t) if (t[0].isalpha() or len(t) <= 2) else t for t in toks])
                stack[-1].append(target)
                pos = pos + line.index("{") + 1
                num = _numeric_block(text, pos)
                if num is not None:
                    target.block, pos = num
                    # rest of the line after the closing brace is handled by the token loop below
                else:
                    target.block = []
                    stack.append(target.block)
                    pos = nl + 1
                    continue
        # line with quotes, braces or comments: token by token until the end of the line
        cur = None
        while pos < n:
            m = search(text, pos)
            if m is None:
                pos = n
                break
            tok = m.group(0)
            pos = m.end()
            c = tok[0]
            if c == "\n":
                break
            if c == "{" and len(tok) == 1:
                lst = stack[-1]
                if cur is None:
                    if lst and lst[-1].block is None:
                        target = lst[-1]          # '{' on its own line (legacy .osg)
                    else:
                        target = Stmt([])
                        lst.append(target)
                else:
                    target = cur
                num = _numeric_block(text, pos)
                if num is not None:
                    target.block, pos = num
                    cur = None
                else:
                    target.block = []
                    stack.append(target.block)
                    cur = None
            elif c == "}" and len(tok) == 1:
                if len(stack) > 1:
                    stack.pop()
                cur = None
            else:
                if c == '"':
                    tok = _unquote(tok)
                elif legacy and c == "/" and tok.startswith("//"):
                    e = find("\n", pos)
                    pos = n if e < 0 else e
                    continue
                else:
                    tok = word(tok)
                if cur is None:
                    cur = Stmt([tok])
                    stack[-1].append(cur)
                else:
                    cur.toks.append(tok)
    doc = Document(dialect, root, header)
    _resolve_objects(doc)
    return doc


def _walk_lists(stmts):
    """Yield every statement list (iteratively, depth first)."""
    todo = [stmts]
    while todo:
        lst = todo.pop()
        yield lst
        for s in lst:
            b = s.block
            if isinstance(b, list):
                todo.append(b)


def _resolve_objects(doc):
    """Turn object blocks into ``Obj`` instances and resolve shared references.

    Must run in document order, because a reference always follows its definition.
    """
    table = {}
    osgt = doc.dialect == "osgt"

    def visit(lst):
        # iterative pre-order traversal keeping document order
        work = [(lst, 0)]
        while work:
            cur, i = work.pop()
            while i < len(cur):
                s = cur[i]
                i += 1
                b = s.block
                if not isinstance(b, list):
                    if osgt or not (len(s.toks) == 2 and s.toks[0] == "Use"):
                        continue
                    ref = table.get(s.toks[1])
                    if ref is not None:
                        s.toks = [ref.cls]
                        s.block = ref
                    continue
                obj = None
                if osgt:
                    if len(s.toks) == 1 and "::" in s.toks[0] and b and b[0].toks[:1] == ["UniqueID"] and len(b[0].toks) > 1:
                        uid = b[0].toks[1]
                        obj = table.get(uid)
                        if obj is None:
                            obj = Obj(s.toks[0], uid, b[1:], "osgt")
                            table[uid] = obj
                            s.block = obj
                            work.append((cur, i))
                            work.append((obj.stmts, 0))
                            break
                        s.block = obj
                        continue
                    if (len(b) >= 2 and b[0].toks[:1] == ["ClassName"] and len(b[0].toks) > 1
                            and b[1].toks[:1] == ["UniqueID"] and len(b[1].toks) > 1):
                        uid = b[1].toks[1]
                        obj = table.get(uid)
                        if obj is None:
                            obj = Obj(b[0].toks[1], uid, b[2:], "image")
                            table[uid] = obj
                            s.block = obj
                            work.append((cur, i))
                            work.append((obj.stmts, 0))
                            break
                        s.block = obj
                        continue
                elif len(s.toks) == 1:
                    uid = None
                    rest = b
                    for k, sub in enumerate(b):
                        if sub.toks[:1] == ["UniqueID"] and len(sub.toks) > 1 and sub.block is None:
                            uid = sub.toks[1]
                            rest = b[:k] + b[k + 1:]
                            break
                    obj = Obj(s.toks[0], uid, rest, "osg")
                    if uid is not None:
                        table[uid] = obj
                    s.block = obj
                    work.append((cur, i))
                    work.append((obj.stmts, 0))
                    break
                work.append((cur, i))
                work.append((b, 0))
                break

    visit(doc.stmts)


# --------------------------------------------------------------------------- writing

def _quote(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def fmt_token(t):
    if isinstance(t, Q):
        return _quote(t)
    return str(t)


def format_numbers(data, per_line, double=False):
    """Numbers -> text lines (a generator; big arrays are formatted in chunks, never all at once).
    Integral data is written without decimals."""
    a = np.asarray(data, dtype=np.float64).ravel()
    if per_line <= 0:
        per_line = 4 if len(a) > 4 else max(len(a), 1)
    if len(a) == 0:
        return
    # one decision for the whole array, so the output is the same as formatting it in one piece
    if np.all(np.isfinite(a)) and np.all(a == np.round(a)) and np.all(np.abs(a) < 2 ** 53):
        mode = "int"
    elif double or len(a) <= 64:
        mode = "short"
    else:
        mode = "f32"
    step = per_line * 16384
    for s0 in range(0, len(a), step):
        part = a[s0:s0 + step]
        if mode == "int":
            vals = [str(int(v)) for v in part.tolist()]
        elif mode == "short":
            vals = [_short_float(v) for v in part.tolist()]
        else:
            vals = [("%.9g" % v) for v in part.astype(np.float32).tolist()]
        for i in range(0, len(vals), per_line):
            yield " ".join(vals[i:i + per_line])


def _short_float(v):
    if v != v:
        return "nan"
    if v in (float("inf"), float("-inf")):
        return "inf" if v > 0 else "-inf"
    if v == int(v) and abs(v) < 1e15:
        return str(int(v))
    r = repr(v)
    # prefer the float32 representation when it round-trips the same float32
    f = "%.9g" % v
    if len(f) < len(r) and float(np.float32(float(f))) == float(np.float32(v)) and abs(float(f) - v) <= abs(v) * 1e-15:
        return f
    return r


class Writer:
    """Serialises a statement tree. ``uid_of`` assigns output ids to objects."""

    def __init__(self, dialect, sink=None):
        self.dialect = dialect
        self.out = []
        self.sink = sink       # file-like: lines are streamed to it instead of kept in memory
        self.seen = {}
        self.next_id = 1
        self.use_count = None  # legacy: object -> number of references (for UniqueID emission)
        self.calls = {}        # id(obj) -> how often write_obj was called (dry run for use_count)

    def line(self, depth, text):
        out = self.out
        out.append("  " * depth + text)
        if self.sink is not None and len(out) >= 20000:
            self.flush()

    def flush(self):
        if self.sink is not None and self.out:
            self.sink.write("\n".join(self.out) + "\n")
            self.out.clear()

    def write_stmts(self, stmts, depth):
        for s in stmts:
            self.write_stmt(s, depth)

    def write_stmt(self, s, depth):
        head = " ".join(fmt_token(t) for t in s.toks)
        b = s.block
        if b is None:
            self.line(depth, head + (" " if self.dialect == "osgt" else ""))
        elif isinstance(b, Num):
            self.line(depth, (head + " {").strip())
            for ln in format_numbers(b.data, b.per_line):
                self.line(depth + 1, ln + (" " if self.dialect == "osgt" else ""))
            self.line(depth, "}")
        elif isinstance(b, Obj):
            self.write_obj(s.toks, b, depth)
        else:
            self.line(depth, (head + " {").strip())
            self.write_stmts(b, depth + 1)
            self.line(depth, "}")

    def write_obj(self, toks, obj, depth, body=None):
        """Write an object; ``body`` (callable(depth)) overrides its statements."""
        first = id(obj) not in self.seen
        self.calls[id(obj)] = self.calls.get(id(obj), 0) + 1
        if self.dialect == "osgt":
            if first:
                self.seen[id(obj)] = self.next_id
                self.next_id += 1
            uid = self.seen[id(obj)]
            if obj.style == "image":
                self.line(depth, " ".join(fmt_token(t) for t in toks) + " {")
                self.line(depth + 1, f"ClassName {obj.cls} ")
                self.line(depth + 1, f"UniqueID {uid} ")
            else:
                self.line(depth, f"{obj.cls} {{")
                self.line(depth + 1, f"UniqueID {uid} ")
            if first:
                if body is not None:
                    body(depth + 1)
                else:
                    self.write_stmts(obj.stmts, depth + 1)
            self.line(depth, "}")
            return
        # legacy .osg
        if not first:
            self.line(depth, f"Use {self.seen[id(obj)]}")
            return
        shared = self.use_count is None or self.use_count.get(id(obj), 0) > 1
        name = None
        if shared:
            name = obj.uid if obj.uid and obj.uid not in self.seen.values() else f"{obj.cls.split('::')[-1]}_{self.next_id}"
            self.next_id += 1
            self.seen[id(obj)] = name
        else:
            self.seen[id(obj)] = None
        self.line(depth, f"{obj.cls} {{")
        if name:
            self.line(depth + 1, f"UniqueID {name}")
        if body is not None:
            body(depth + 1)
        else:
            self.write_stmts(obj.stmts, depth + 1)
        self.line(depth, "}")

    def text(self):
        return "\n".join(self.out) + "\n"


class NullSink:
    """Discards everything (dry runs that only count objects)."""

    def write(self, _text):
        return 0


def count_uses(stmts, counts=None):
    """Number of places each object is referenced from (legacy writer needs it)."""
    counts = {} if counts is None else counts
    todo = [stmts]
    while todo:
        lst = todo.pop()
        for s in lst:
            b = s.block
            if isinstance(b, Obj):
                c = counts.get(id(b), 0)
                counts[id(b)] = c + 1
                if c == 0:
                    todo.append(b.stmts)
            elif isinstance(b, list):
                todo.append(b)
    return counts


def is_int_token(t):
    return bool(_INT_LIKE.match(t))
