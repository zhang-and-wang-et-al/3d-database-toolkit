"""Writes data/pyformat_cases.json: Python's own formatting of many numbers (the C# port must match it)."""
import json, math, os, random, struct, sys
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", ".."))
from scenekit.osgtext import _short_float
from scenekit.osgwrite import _num
import numpy as np

rng = random.Random(7)
vals = [0.0, -0.0, 1.0, -1.0, 0.5, 0.125, 0.1, 1e-5, 1e-4, 0.0001234, 123456789.0, 1e15, 1e16, 1.5e16, 1e22, 1e-300,
        5e-324, 1.7976931348623157e308, 2.5, 3.5, 1234567.5, 0.30000000000000004, 123.456, 1e30, 6e-8, 2.0**53, 2.0**-30]
for _ in range(400):
    e = rng.uniform(-12, 12)
    vals.append(rng.choice([-1, 1]) * 10 ** e)
    vals.append(float(np.float32(rng.uniform(-1000, 1000))))
    vals.append(round(rng.uniform(-100, 100), rng.randint(0, 6)))
cases = []
for v in vals:
    cases.append({"v": struct.unpack("<q", struct.pack("<d", v))[0], "repr": repr(v), "g9": "%.9g" % v, "g3": "%.3g" % v,
                  "g6": "%g" % v, "f1": f"{v:.1f}", "f0g": f"{v:,.0f}", "pct1": f"{v:.1%}", "short": _short_float(v),
                  "num": _num(v) if abs(v) < 1e300 else "", "g9f32": ("%.9g" % float(np.float32(v))) if abs(v) < 3e38 else ""})
with open(os.path.join(os.path.dirname(__file__), "data", "pyformat_cases.json"), "w") as fh:
    json.dump(cases, fh, indent=0)
print(len(cases), "cases")
