"""Built-in importers for formats Blender cannot read out of the box.

``BUILTIN`` maps an extension to ``load(path, ctx) -> details dict``.
``ctx`` provides ``log(msg)`` and ``add_issue(code, message)``.
"""

from . import binfmt, flt, osgfmt, textfmt, xmlfmt

BUILTIN = {
    ".flt": flt.load,
    ".osgt": osgfmt.load,
    ".osg": osgfmt.load,
    ".off": textfmt.load_off,
    ".smd": textfmt.load_smd,
    ".ac": textfmt.load_ac,
    ".ac3d": textfmt.load_ac,
    ".vtk": textfmt.load_vtk,
    ".dxf": textfmt.load_dxf,
    ".ase": textfmt.load_ase,
    ".x": textfmt.load_x,
    ".3mf": xmlfmt.load_3mf,
    ".amf": xmlfmt.load_amf,
    ".dae": xmlfmt.load_collada,
    ".zae": xmlfmt.load_collada,
    ".3ds": binfmt.load_3ds,
    ".lwo": binfmt.load_lwo,
    ".md2": binfmt.load_md2,
}


class Context:
    def __init__(self, log, add_issue):
        self.log = log
        self.add_issue = add_issue
