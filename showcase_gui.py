#!/usr/bin/env python3
"""3D Database Toolkit - GUI.

Pick library folders, tick / untick folders or single models, adjust the
render settings and press Start. Every model gets (next to it, or in a
mirrored output folder):

    <model>.preview.jpg    hero render
    <model>.showcase.jpg   contact sheet (all views + info banner)
    <model>.info.txt       human readable report
    <model>.info.json      machine readable record

plus one ``showcase_index.sqlite`` / ``showcase_index.csv`` per library root.

Only the Python standard library is required (Tkinter). If Pillow is
installed, JPEG previews are shown inside the app.
"""

import os
import platform
import queue
import subprocess
import sys
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk
from tkinter.scrolledtext import ScrolledText

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

WINDOWS_OS = sys.platform == "win32"
from showcase import __version__, config, index_db, netpaths, schema  # noqa: E402
from showcase.runner import Runner, find_record  # noqa: E402
from showcase.selection import SKIP_DIRS, Selection, is_model_file  # noqa: E402

try:
    from PIL import Image, ImageTk  # optional, for JPEG previews
except ImportError:  # pragma: no cover
    Image = ImageTk = None

GLYPH = {"on": "☑", "off": "☐", "partial": "▣"}
DUMMY = "::placeholder::"
LAST_JOB = os.path.join(config.user_config_dir(), "last_job.json")
JOB_TYPES = [("Showcase job", "*.showcase-job.json"), ("JSON", "*.json"), ("All files", "*.*")]
STATUS_FG = {"ok": "#1f7a3a", "warning": "#a15c00", "error": "#7a3aa1", "broken": "#b3261e", "unsupported": "#4a6fa5"}


def open_external(path):
    if not path or not os.path.exists(path):
        return
    if platform.system() == "Windows":
        os.startfile(path)  # noqa: S606
    elif platform.system() == "Darwin":
        subprocess.Popen(["open", path])
    else:
        subprocess.Popen(["xdg-open", path])


class ScrollFrame(ttk.Frame):
    """Vertically scrollable container (``.inner`` holds the content)."""

    def __init__(self, master):
        super().__init__(master)
        bg = ttk.Style().lookup("TFrame", "background") or None
        self.canvas = tk.Canvas(self, highlightthickness=0, borderwidth=0, background=bg)
        sb = ttk.Scrollbar(self, orient="vertical", command=self.canvas.yview)
        self.inner = ttk.Frame(self.canvas)
        win = self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.canvas.configure(yscrollcommand=sb.set)
        self.canvas.pack(side="left", fill="both", expand=True)
        sb.pack(side="right", fill="y")
        self.inner.bind("<Configure>", lambda e: self.canvas.configure(scrollregion=self.canvas.bbox("all")))
        self.canvas.bind("<Configure>", lambda e: self.canvas.itemconfigure(win, width=e.width))
        self.canvas.bind("<Enter>", lambda e: self._wheel(True))
        self.canvas.bind("<Leave>", lambda e: self._wheel(False))

    def _wheel(self, on):
        if on:
            self.canvas.bind_all("<MouseWheel>", lambda e: self.canvas.yview_scroll(int(-e.delta / 120) or (-1 if e.delta > 0 else 1), "units"))
            self.canvas.bind_all("<Button-4>", lambda e: self.canvas.yview_scroll(-3, "units"))
            self.canvas.bind_all("<Button-5>", lambda e: self.canvas.yview_scroll(3, "units"))
        else:
            for seq in ("<MouseWheel>", "<Button-4>", "<Button-5>"):
                self.canvas.unbind_all(seq)


class SettingsPanel(ttk.Frame):
    """All render / output settings, bound to a ``config.Settings``."""

    def __init__(self, master, app):
        super().__init__(master, padding=6)
        self.app = app
        self.vars = {}
        self._building = True

        def var(name, kind=tk.StringVar):
            v = kind()
            self.vars[name] = v
            v.trace_add("write", lambda *_: self._changed(name))
            return v

        row = 0
        g = ttk.LabelFrame(self, text="Quality", padding=6)
        g.grid(row=row, column=0, sticky="ew", pady=3)
        ttk.Label(g, text="Preset").grid(row=0, column=0, sticky="w")
        ttk.Combobox(g, textvariable=var("preset"), values=list(config.PRESETS) + ["custom"],
                     state="readonly", width=12).grid(row=0, column=1, sticky="w")
        ttk.Label(g, text="Engine").grid(row=0, column=2, sticky="w", padx=(12, 0))
        ttk.Combobox(g, textvariable=var("engine"), values=["auto", "eevee", "cycles", "workbench"],
                     state="readonly", width=10).grid(row=0, column=3, sticky="w")
        ttk.Label(g, text="Resolution (px)").grid(row=1, column=0, sticky="w")
        ttk.Spinbox(g, textvariable=var("resolution", tk.IntVar), from_=128, to=4096, increment=64, width=8).grid(row=1, column=1, sticky="w")
        ttk.Label(g, text="Samples").grid(row=1, column=2, sticky="w", padx=(12, 0))
        ttk.Spinbox(g, textvariable=var("samples", tk.IntVar), from_=1, to=4096, width=8).grid(row=1, column=3, sticky="w")
        ttk.Label(g, text="Background").grid(row=2, column=0, sticky="w")
        ttk.Combobox(g, textvariable=var("background"), values=["light", "dark"], state="readonly", width=10).grid(row=2, column=1, sticky="w")
        ttk.Checkbutton(g, text="Floor + shadows", variable=var("ground", tk.BooleanVar)).grid(row=2, column=2, columnspan=2, sticky="w", padx=(12, 0))
        ttk.Checkbutton(g, text="Use GPU (Cycles)", variable=var("gpu", tk.BooleanVar)).grid(row=3, column=2, columnspan=2, sticky="w", padx=(12, 0))
        ttk.Label(g, text="auto = EEVEE, falls back to Cycles if the GPU engine fails",
                  foreground="#666").grid(row=4, column=0, columnspan=4, sticky="w", pady=(4, 0))

        row += 1
        g = ttk.LabelFrame(self, text="Views on the contact sheet", padding=6)
        g.grid(row=row, column=0, sticky="ew", pady=3)
        self.view_vars = {}
        for i, v in enumerate(config.ALL_VIEWS):
            bv = tk.BooleanVar()
            bv.trace_add("write", lambda *_: self._changed("views"))
            self.view_vars[v] = bv
            ttk.Checkbutton(g, text=v, variable=bv).grid(row=0, column=i, sticky="w", padx=(0, 6))

        row += 1
        g = ttk.LabelFrame(self, text="Model handling", padding=6)
        g.grid(row=row, column=0, sticky="ew", pady=3)
        ttk.Label(g, text="Orientation").grid(row=0, column=0, sticky="w")
        ttk.Combobox(g, textvariable=var("orientation"), values=["auto", "importer", "detect"],
                     state="readonly", width=10).grid(row=0, column=1, sticky="w")
        ttk.Checkbutton(g, text="Find missing textures nearby", variable=var("fix_textures", tk.BooleanVar)).grid(row=0, column=2, sticky="w", padx=(12, 0))
        ttk.Label(g, text="auto: stand up lying OBJ/STL/PLY/OFF/3MF models; detect: all; importer: never",
                  foreground="#666").grid(row=1, column=0, columnspan=3, sticky="w")
        ttk.Label(g, text="Poly tiers (max triangles per tier):").grid(row=2, column=0, columnspan=3, sticky="w", pady=(4, 0))
        tf = ttk.Frame(g)
        tf.grid(row=3, column=0, columnspan=3, sticky="w")
        self.tier_vars = []
        for i, name in enumerate(["very low", "low", "medium", "high"]):
            ttk.Label(tf, text=name).grid(row=0, column=i * 2, sticky="w")
            tv = tk.StringVar()
            tv.trace_add("write", lambda *_: self._changed("poly_tiers"))
            self.tier_vars.append(tv)
            ttk.Entry(tf, textvariable=tv, width=8).grid(row=0, column=i * 2 + 1, padx=(2, 8))

        row += 1
        g = ttk.LabelFrame(self, text="Output", padding=6)
        g.grid(row=row, column=0, sticky="ew", pady=3)
        g.columnconfigure(1, weight=1)
        om = var("output_mode")
        ttk.Radiobutton(g, text="Next to each model", value="beside", variable=om).grid(row=0, column=0, sticky="w")
        ttk.Radiobutton(g, text="Separate folder:", value="mirror", variable=om).grid(row=1, column=0, sticky="w")
        ttk.Entry(g, textvariable=var("output_dir")).grid(row=1, column=1, sticky="ew")
        ttk.Button(g, text="...", width=3, command=self._pick_output).grid(row=1, column=2)
        ttk.Label(g, text="Read-only folders:").grid(row=2, column=0, sticky="w")
        ttk.Entry(g, textvariable=var("fallback_output_dir")).grid(row=2, column=1, sticky="ew")
        ttk.Button(g, text="...", width=3, command=lambda: self._pick_dir("fallback_output_dir")).grid(row=2, column=2)
        ttk.Label(g, text=f"(e.g. write-protected network shares; empty = {config.default_fallback_dir()})",
                  foreground="#666").grid(row=3, column=0, columnspan=3, sticky="w")
        ttk.Label(g, text="Image format").grid(row=4, column=0, sticky="w")
        ff = ttk.Frame(g)
        ff.grid(row=4, column=1, sticky="w")
        ttk.Combobox(ff, textvariable=var("image_format"), values=["JPEG", "PNG"], state="readonly", width=6).pack(side="left")
        ttk.Label(ff, text="  JPEG quality").pack(side="left")
        ttk.Spinbox(ff, textvariable=var("jpeg_quality", tk.IntVar), from_=30, to=100, width=5).pack(side="left")

        row += 1
        g = ttk.LabelFrame(self, text="Processing", padding=6)
        g.grid(row=row, column=0, sticky="ew", pady=3)
        ttk.Label(g, text="Parallel Blender processes").grid(row=0, column=0, sticky="w")
        ttk.Spinbox(g, textvariable=var("jobs", tk.IntVar), from_=1, to=64, width=5).grid(row=0, column=1, sticky="w")
        ttk.Label(g, text="Timeout per model (s)").grid(row=1, column=0, sticky="w")
        ttk.Spinbox(g, textvariable=var("timeout", tk.IntVar), from_=30, to=86400, increment=30, width=7).grid(row=1, column=1, sticky="w")
        ttk.Checkbutton(g, text="Skip models that are already up to date", variable=var("skip_existing", tk.BooleanVar)).grid(row=2, column=0, columnspan=2, sticky="w")
        ttk.Checkbutton(g, text="...but retry models that ended with an error", variable=var("retry_errors", tk.BooleanVar)).grid(row=3, column=0, columnspan=2, sticky="w")
        ttk.Checkbutton(g, text="SHA-256 hash (finds duplicate files)", variable=var("hash_files", tk.BooleanVar)).grid(row=4, column=0, columnspan=2, sticky="w")
        ttk.Checkbutton(g, text="Blender --factory-startup (ignore user add-ons)", variable=var("factory_startup", tk.BooleanVar)).grid(row=5, column=0, columnspan=2, sticky="w")
        ttk.Checkbutton(g, text="List proprietary files (.max, .c4d, .skp, CAD...) as 'unsupported'", variable=var("record_unsupported", tk.BooleanVar)).grid(row=6, column=0, columnspan=2, sticky="w")

        row += 1
        g = ttk.LabelFrame(self, text="Optional converters for rare formats (empty = find on PATH)", padding=6)
        g.grid(row=row, column=0, sticky="ew", pady=3)
        g.columnconfigure(1, weight=1)
        for r, (key, label) in enumerate((("assimp_path", "assimp"), ("osgconv_path", "osgconv (OpenSceneGraph)"))):
            ttk.Label(g, text=label).grid(row=r, column=0, sticky="w")
            ttk.Entry(g, textvariable=var(key)).grid(row=r, column=1, sticky="ew", padx=4)
            ttk.Button(g, text="...", width=3, command=lambda k=key: self._pick_tool(k)).grid(row=r, column=2)
        self.tool_status = ttk.Label(g, foreground="#666")
        self.tool_status.grid(row=2, column=0, columnspan=3, sticky="w")
        self.columnconfigure(0, weight=1)
        self._building = False

    def _pick_dir(self, key):
        d = filedialog.askdirectory(title="Choose folder")
        if d:
            self.vars[key].set(d)

    def _pick_tool(self, key):
        f = filedialog.askopenfilename(title=key.replace("_path", ""))
        if f:
            self.vars[key].set(f)

    def update_tool_status(self):
        s = self.app.settings
        found = []
        for key, name in (("assimp_path", "assimp"), ("osgconv_path", "osgconv")):
            path = config.find_tool(getattr(s, key), name)
            found.append(f"{name}: {'found' if path else 'not found'}")
        self.tool_status.configure(text="  |  ".join(found) + "   (built-in readers work without them)")

    def _pick_output(self):
        d = filedialog.askdirectory(title="Output folder")
        if d:
            self.vars["output_dir"].set(d)
            self.vars["output_mode"].set("mirror")

    def load(self, s):
        self._building = True
        for name, v in self.vars.items():
            v.set(getattr(s, name))
        for name, bv in self.view_vars.items():
            bv.set(name in s.views)
        for tv, val in zip(self.tier_vars, s.poly_tiers):
            tv.set(str(val))
        self._building = False
        self.update_tool_status()

    def _changed(self, name):
        if self._building:
            return
        s = self.app.settings
        if name == "views":
            s.views = [v for v in config.ALL_VIEWS if self.view_vars[v].get()]
        elif name == "poly_tiers":
            try:
                s.poly_tiers = [int(t.get().replace(",", "").replace("_", "")) for t in self.tier_vars]
            except ValueError:
                return
        else:
            try:
                setattr(s, name, self.vars[name].get())
            except (tk.TclError, ValueError):
                return
        if name in ("assimp_path", "osgconv_path"):
            self.update_tool_status()
        if name == "preset" and s.preset in config.PRESETS:
            s.apply_preset(s.preset)
            self.load(s)
        elif name in ("resolution", "samples", "views") and s.preset != "custom":
            preset = config.PRESETS.get(s.preset, {})
            if preset.get(name) != getattr(s, name):
                s.preset = "custom"
                self._building = True
                self.vars["preset"].set("custom")
                self._building = False


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(f"3D Database Toolkit {__version__}")
        self.geometry("1360x860")
        self.minsize(960, 600)
        self.settings = config.Settings()
        self.sel = Selection()
        self.job_path = None
        self.events = queue.Queue()
        self.runner = None
        self.worker_thread = None
        self._preview_ref = None
        self._detail_path = None
        self.kinds = {}      # tree item -> "dir" / "file" (avoids slow stat calls on network shares)
        self.records = {}    # model path -> (record, output prefix)
        self._loading = set()
        self._reopen = set()

        self._build_menu()
        self._build_toolbar()
        self._build_body()
        self._load_last_job()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.after(100, self._poll_events)

    # ------------------------------------------------------------------ layout
    def _build_menu(self):
        m = tk.Menu(self)
        f = tk.Menu(m, tearoff=False)
        f.add_command(label="New job", command=self.new_job)
        f.add_command(label="Open job...", command=self.open_job)
        f.add_command(label="Save job", command=self.save_job)
        f.add_command(label="Save job as...", command=lambda: self.save_job(ask=True))
        f.add_separator()
        f.add_command(label="Quit", command=self._on_close)
        m.add_cascade(label="File", menu=f)
        lib = tk.Menu(m, tearoff=False)
        lib.add_command(label="Add library folder...", command=self.add_root)
        lib.add_command(label="Add network folder (UNC / smb / mounted share)...", command=self.add_network_root)
        lib.add_command(label="Remove selected library folder", command=self.remove_root)
        lib.add_command(label="Refresh tree", command=self.refresh_tree)
        lib.add_separator()
        lib.add_command(label="Include selected", command=lambda: self.set_selected(True))
        lib.add_command(label="Exclude selected", command=lambda: self.set_selected(False))
        lib.add_command(label="Include only selected (exclude siblings)", command=self.only_selected)
        lib.add_command(label="Reset all include/exclude marks", command=self.reset_marks)
        m.add_cascade(label="Library", menu=lib)
        t = tk.Menu(m, tearoff=False)
        t.add_command(label="Count selected models", command=self.scan)
        t.add_command(label="Rebuild index from existing .info.json files", command=self.rebuild_index)
        t.add_command(label="Open index folder", command=self.open_index_folder)
        t.add_separator()
        t.add_command(label="Scene inspector (OSG / OpenFlight)...", command=self.open_inspector)
        m.add_cascade(label="Tools", menu=t)
        h = tk.Menu(m, tearoff=False)
        h.add_command(label="How it works", command=self.show_help)
        m.add_cascade(label="Help", menu=h)
        self.config(menu=m)

    def _build_toolbar(self):
        bar = ttk.Frame(self, padding=(6, 6, 6, 0))
        bar.pack(fill="x")
        ttk.Button(bar, text="Add folder...", command=self.add_root).pack(side="left")
        ttk.Button(bar, text="Add network folder...", command=self.add_network_root).pack(side="left", padx=(4, 0))
        ttk.Button(bar, text="Remove folder", command=self.remove_root).pack(side="left", padx=(4, 0))
        ttk.Button(bar, text="Refresh", command=self.refresh_tree).pack(side="left", padx=(4, 12))
        ttk.Button(bar, text="Include", command=lambda: self.set_selected(True)).pack(side="left")
        ttk.Button(bar, text="Exclude", command=lambda: self.set_selected(False)).pack(side="left", padx=(4, 0))
        ttk.Button(bar, text="Only this", command=self.only_selected).pack(side="left", padx=(4, 12))
        ttk.Label(bar, text="Blender:").pack(side="left")
        self.blender_var = tk.StringVar()
        self.blender_var.trace_add("write", lambda *_: setattr(self.settings, "blender", self.blender_var.get().strip()))
        ttk.Entry(bar, textvariable=self.blender_var).pack(side="left", fill="x", expand=True, padx=4)
        ttk.Button(bar, text="Browse...", command=self.browse_blender).pack(side="left")
        ttk.Button(bar, text="Detect", command=self.detect_blender).pack(side="left", padx=(4, 0))

    def _build_body(self):
        vpan = ttk.PanedWindow(self, orient="vertical")
        vpan.pack(fill="both", expand=True, padx=6, pady=6)
        hpan = ttk.PanedWindow(vpan, orient="horizontal")
        vpan.add(hpan, weight=4)

        # --- library tree
        left = ttk.Frame(hpan)
        hpan.add(left, weight=3)
        cols = ("use", "kind", "status", "tris", "issues")
        self.tree = ttk.Treeview(left, columns=cols, selectmode="extended")
        self.tree.heading("#0", text="Library")
        self.tree.heading("use", text="Render")
        self.tree.heading("kind", text="Type")
        self.tree.heading("status", text="Status")
        self.tree.heading("tris", text="Triangles")
        self.tree.heading("issues", text="Issues")
        self.tree.column("#0", width=330, stretch=True)
        self.tree.column("use", width=56, anchor="center", stretch=False)
        self.tree.column("kind", width=70, stretch=False)
        self.tree.column("status", width=105, stretch=False)
        self.tree.column("tris", width=90, anchor="e", stretch=False)
        self.tree.column("issues", width=200, stretch=True)
        for st, fg in STATUS_FG.items():
            self.tree.tag_configure(st, foreground=fg)
        self.tree.tag_configure("excluded", foreground="#999999")
        ys = ttk.Scrollbar(left, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=ys.set)
        self.tree.pack(side="left", fill="both", expand=True)
        ys.pack(side="right", fill="y")
        self.tree.bind("<<TreeviewOpen>>", self._on_open)
        self.tree.bind("<<TreeviewSelect>>", self._on_select)
        self.tree.bind("<Button-1>", self._on_click)
        self.tree.bind("<space>", lambda e: (self.toggle_selected(), "break")[1])
        self.tree.bind("<Double-1>", self._on_double)
        self.tree.bind("<Button-3>", self._on_context)
        self.tree.bind("<Button-2>", self._on_context)
        self.ctx = tk.Menu(self, tearoff=False)
        self.ctx.add_command(label="Include", command=lambda: self.set_selected(True))
        self.ctx.add_command(label="Exclude", command=lambda: self.set_selected(False))
        self.ctx.add_command(label="Include only this", command=self.only_selected)
        self.ctx.add_separator()
        self.ctx.add_command(label="Render selected now (force)", command=self.render_selected)
        self.ctx.add_command(label="Open contact sheet", command=lambda: self.open_output(schema.SUFFIX_SHEET))
        self.ctx.add_command(label="Open info.txt", command=lambda: self.open_output(schema.SUFFIX_TXT))
        self.ctx.add_command(label="Open containing folder", command=self.open_folder)
        self.ctx.add_separator()
        self.ctx.add_command(label="Inspect scene (tree, sizes, optimise)...", command=self.inspect_selected)

        # --- right side: settings + details
        nb = ttk.Notebook(hpan)
        hpan.add(nb, weight=2)
        scroller = ScrollFrame(nb)
        self.settings_panel = SettingsPanel(scroller.inner, self)
        self.settings_panel.pack(fill="both", expand=True)
        nb.add(scroller, text="Settings")
        det = ttk.Frame(nb, padding=6)
        nb.add(det, text="Selected model")
        self.nb = nb
        self.details_tab = det
        self.preview_label = ttk.Label(det, text="Select a model in the tree", anchor="center")
        self.preview_label.pack(fill="both", expand=True)
        btns = ttk.Frame(det)
        btns.pack(fill="x", pady=4)
        ttk.Button(btns, text="Open sheet", command=lambda: self.open_output(schema.SUFFIX_SHEET)).pack(side="left")
        ttk.Button(btns, text="Open info.txt", command=lambda: self.open_output(schema.SUFFIX_TXT)).pack(side="left", padx=4)
        ttk.Button(btns, text="Open folder", command=self.open_folder).pack(side="left")
        ttk.Button(btns, text="Render now", command=self.render_selected).pack(side="right")
        self.info_text = ScrolledText(det, height=14, font=("Courier New", 9), wrap="none")
        self.info_text.pack(fill="both", expand=True)
        self.preview_label.bind("<Configure>", lambda e: self._show_preview(self._detail_path, resize_only=True))

        # --- run controls + log
        bottom = ttk.Frame(vpan)
        vpan.add(bottom, weight=1)
        ctl = ttk.Frame(bottom)
        ctl.pack(fill="x")
        self.start_btn = ttk.Button(ctl, text="▶ Start rendering", command=self.start)
        self.start_btn.pack(side="left")
        self.stop_btn = ttk.Button(ctl, text="■ Stop", command=self.stop, state="disabled")
        self.stop_btn.pack(side="left", padx=4)
        ttk.Button(ctl, text="Count selected", command=self.scan).pack(side="left", padx=(0, 8))
        self.progress = ttk.Progressbar(ctl, mode="determinate")
        self.progress.pack(side="left", fill="x", expand=True, padx=4)
        self.status_var = tk.StringVar(value="Ready")
        ttk.Label(ctl, textvariable=self.status_var, width=48).pack(side="left")
        self.log_text = ScrolledText(bottom, height=8, font=("Courier New", 9), state="disabled")
        self.log_text.pack(fill="both", expand=True, pady=(4, 0))

    # ------------------------------------------------------------------ tree
    # Folder contents and existing results are read in background threads, so a
    # slow or unreachable network share never freezes the window.
    def refresh_tree(self):
        opened = {i for i in self._all_items() if self.tree.item(i, "open")}
        self.records.clear()
        self.tree.delete(*self.tree.get_children(""))
        self._reopen = set(opened)
        for root in self.sel.roots:
            icon = "\U0001F310" if netpaths.is_network_path(root) else "\U0001F4C1"
            self._insert_dir("", root, label=root, icon=icon)
            if root in opened:
                self.tree.item(root, open=True)
                self._populate(root)

    def _all_items(self, parent=""):
        for c in self.tree.get_children(parent):
            if not c.startswith(DUMMY):
                yield c
                yield from self._all_items(c)

    def _insert_dir(self, parent, path, label=None, icon="\U0001F4C1"):
        if self.tree.exists(path):
            return
        self.kinds[path] = "dir"
        self.tree.insert(parent, "end", iid=path, text=f"{icon} {label or os.path.basename(path)}")
        self.tree.insert(path, "end", iid=DUMMY + path, text="...")
        self._update_item(path)

    def _insert_file(self, parent, path):
        if self.tree.exists(path):
            return
        self.kinds[path] = "file"
        self.tree.insert(parent, "end", iid=path, text=os.path.basename(path))
        self._update_item(path)

    def _populate(self, iid):
        dummy = DUMMY + iid
        if not self.tree.exists(dummy) or iid in self._loading:
            return
        self._loading.add(iid)
        self.tree.item(dummy, text="loading ...")
        root = self.sel.root_of(iid)
        settings = config.Settings.from_dict(self.settings.to_dict())
        out_dir = os.path.normcase(os.path.abspath(settings.output_dir)) if settings.output_mode == "mirror" and settings.output_dir else None

        def work():
            dirs, files, error = [], [], None
            try:
                entries = sorted(os.scandir(iid), key=lambda e: e.name.lower())
                for e in entries:
                    if e.name.startswith(".") or e.name in SKIP_DIRS:
                        continue
                    try:
                        is_dir = e.is_dir()
                    except OSError:
                        continue
                    if is_dir:
                        if out_dir is None or os.path.normcase(os.path.abspath(e.path)) != out_dir:
                            dirs.append(e.path)
                    elif is_model_file(e.name):
                        rec, base = find_record(root, e.path, settings) if root else (None, None)
                        files.append((e.path, rec, base))
            except OSError as exc:
                error = str(exc)
            self.events.put({"type": "populated", "iid": iid, "dirs": dirs, "files": files, "error": error})

        threading.Thread(target=work, daemon=True).start()

    def _on_populated(self, e):
        iid = e["iid"]
        self._loading.discard(iid)
        if not self.tree.exists(iid):
            return
        if self.tree.exists(DUMMY + iid):
            self.tree.delete(DUMMY + iid)
        if e["error"]:
            self.log(f"Cannot read {iid}: {e['error']}")
            self.tree.insert(iid, "end", iid=DUMMY + iid, text="(not readable - check the network connection, then Refresh)")
            return
        for d in e["dirs"]:
            self._insert_dir(iid, d)
            if d in self._reopen:
                self.tree.item(d, open=True)
                self._populate(d)
        for path, rec, base in e["files"]:
            self.records[path] = (rec, base)
            self._insert_file(iid, path)

    def _on_open(self, _event):
        iid = self.tree.focus()
        if iid:
            self._populate(iid)

    def _record_for(self, path, refresh=False):
        if not refresh and path in self.records:
            return self.records[path]
        root = self.sel.root_of(path)
        if not root:
            return None, None
        self.records[path] = find_record(root, path, self.settings)
        return self.records[path]

    def _update_item(self, iid):
        kind = self.kinds.get(iid) or ("dir" if os.path.isdir(iid) else "file")
        if kind == "dir":
            state = self.sel.dir_state(iid)
            tags = ("excluded",) if state == "off" else ()
            self.tree.item(iid, values=(GLYPH[state], "folder", "", "", ""), tags=tags)
            return
        inc = self.sel.is_included(iid)
        rec, _ = self.records.get(iid, (None, None))
        ext = os.path.splitext(iid)[1].lower().lstrip(".")
        if rec:
            st = rec.get("status", "")
            tris = rec.get("geometry", {}).get("triangles")
            issues = ", ".join(i["code"] for i in rec.get("issues", []) if i.get("severity") != "info")
            vals = (GLYPH["on" if inc else "off"], ext, st, f"{tris:,}" if tris is not None else "", issues)
            tags = (st,) if inc else ("excluded",)
        else:
            vals = (GLYPH["on" if inc else "off"], ext, "new", "", "")
            tags = () if inc else ("excluded",)
        self.tree.item(iid, values=vals, tags=tags)

    def _refresh_states(self):
        for iid in self._all_items():
            self._update_item(iid)

    def _on_click(self, event):
        if self.tree.identify_region(event.x, event.y) != "cell" or self.tree.identify_column(event.x) != "#1":
            return None
        iid = self.tree.identify_row(event.y)
        if iid and not iid.startswith(DUMMY):
            self.sel.toggle(iid)
            self._refresh_states()
            return "break"
        return None

    def _on_double(self, event):
        iid = self.tree.identify_row(event.y)
        if iid and self.kinds.get(iid) == "file":
            self.open_output(schema.SUFFIX_SHEET)
            return "break"
        return None

    def _on_context(self, event):
        iid = self.tree.identify_row(event.y)
        if iid:
            if iid not in self.tree.selection():
                self.tree.selection_set(iid)
            self.ctx.tk_popup(event.x_root, event.y_root)

    def _selected_paths(self):
        return [i for i in self.tree.selection() if not i.startswith(DUMMY)]

    def set_selected(self, included):
        for p in self._selected_paths():
            self.sel.set(p, included)
        self._refresh_states()

    def toggle_selected(self):
        paths = self._selected_paths()
        if paths:
            target = not self.sel.is_included(paths[0])
            for p in paths:
                self.sel.set(p, target)
            self._refresh_states()

    def only_selected(self):
        paths = self._selected_paths()
        if not paths:
            return
        parents = {self.tree.parent(p) for p in paths}
        for parent in parents:
            siblings = self.tree.get_children(parent) if parent else ()
            for s in siblings:
                if not s.startswith(DUMMY):
                    self.sel.set(s, False)
        for p in paths:
            self.sel.set(p, True)
        self._refresh_states()

    def reset_marks(self):
        if messagebox.askyesno("Reset", "Remove all include/exclude marks (everything included again)?"):
            self.sel.overrides.clear()
            self._refresh_states()

    # ------------------------------------------------------------------ details
    def _on_select(self, _event):
        paths = self._selected_paths()
        if len(paths) == 1 and self.kinds.get(paths[0]) == "file":
            self._show_details(paths[0])

    def _show_details(self, path):
        self._detail_path = path
        rec, base = self._record_for(path, refresh=True)
        if self.tree.exists(path):
            self._update_item(path)
        self.info_text.delete("1.0", "end")
        if rec:
            txt_path = base + schema.SUFFIX_TXT
            try:
                with open(txt_path, encoding="utf-8") as fh:
                    self.info_text.insert("1.0", fh.read())
            except OSError:
                self.info_text.insert("1.0", schema.format_report(rec))
        else:
            self.info_text.insert("1.0", f"{path}\n\nNot processed yet.")
        self._show_preview(path)

    def _sheet_path(self, path):
        rec, base = self._record_for(path)
        if not rec:
            return None
        name = rec.get("render", {}).get("sheet_image")
        return os.path.join(os.path.dirname(base), name) if name else None

    def _show_preview(self, path, resize_only=False):
        if not path:
            return
        img_path = self._sheet_path(path)
        if not img_path or not os.path.exists(img_path):
            if not resize_only:
                self.preview_label.configure(image="", text="No render yet")
            self._preview_ref = None
            return
        w = max(50, self.preview_label.winfo_width() - 4)
        h = max(50, self.preview_label.winfo_height() - 4)
        try:
            if Image is not None:
                im = Image.open(img_path)
                im.thumbnail((w, h))
                photo = ImageTk.PhotoImage(im)
            elif img_path.lower().endswith(".png"):
                photo = tk.PhotoImage(file=img_path)
                factor = max(1, -(-photo.width() // w), -(-photo.height() // h))
                photo = photo.subsample(factor, factor)
            else:
                self.preview_label.configure(image="", text="Install Pillow (pip install pillow)\nto preview JPEG images here,\nor use 'Open sheet'.")
                return
        except Exception as exc:
            self.preview_label.configure(image="", text=f"Cannot show image: {exc}")
            return
        self._preview_ref = photo
        self.preview_label.configure(image=photo, text="")

    def open_output(self, suffix):
        paths = [p for p in self._selected_paths() if self.kinds.get(p) == "file"]
        if not paths:
            return
        rec, base = self._record_for(paths[0])
        if suffix == schema.SUFFIX_SHEET:
            open_external(self._sheet_path(paths[0]))
        else:
            open_external(base + suffix)

    def open_inspector(self, path=None):
        # the scene inspector is the C# / WPF app (csharp/SceneInspector.Wpf), started via its launcher
        bat = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Scene Inspector (C#).bat")
        if not WINDOWS_OS:
            messagebox.showerror("Scene inspector", "The scene inspector (C# / WPF) runs on Windows only.")
            return
        subprocess.Popen(["cmd", "/c", bat] + ([path] if path else []), creationflags=subprocess.CREATE_NEW_CONSOLE)

    def inspect_selected(self):
        paths = [p for p in self._selected_paths() if self.kinds.get(p) == "file"]
        if paths:
            self.open_inspector(paths[0])

    def open_folder(self):
        paths = self._selected_paths()
        if paths:
            p = paths[0]
            open_external(p if self.kinds.get(p) == "dir" else os.path.dirname(p))

    # ------------------------------------------------------------------ roots & blender
    def add_root(self):
        d = filedialog.askdirectory(title="Add a 3D model library folder")
        if d:
            self._add_root_path(os.path.abspath(d))

    def _add_root_path(self, path):
        self.sel.add_root(path)
        net = netpaths.is_network_path(path)
        self._insert_dir("", path, label=path, icon="\U0001F310" if net else "\U0001F4C1")
        if net:
            self.log(f"Added network folder {path}. Results are written next to the models when the share is "
                     f"writable, otherwise to {self.settings.fallback_output_dir or config.default_fallback_dir()}.")

    def add_network_root(self):
        text = simpledialog.askstring(
            "Add network folder",
            "Path of the network folder, for example:\n\n"
            "   \\\\server\\share\\models      (Windows UNC path)\n"
            "   Z:\\models                    (mapped drive)\n"
            "   /Volumes/share/models          (macOS, share connected in Finder)\n"
            "   /mnt/nas/models                (Linux mount)\n"
            "   smb://server/share/models      (must be connected/mounted)\n",
            parent=self)
        if not text:
            return
        self.status_var.set("Checking network folder ...")

        def work():
            try:
                path = netpaths.resolve_user_path(text)
                self.events.put({"type": "add_root", "path": path})
            except ValueError as exc:
                self.events.put({"type": "add_root_failed", "message": str(exc)})

        threading.Thread(target=work, daemon=True).start()

    def remove_root(self):
        for p in self._selected_paths():
            if p in self.sel.roots or os.path.abspath(p) in self.sel.roots:
                self.sel.remove_root(p)
                self.tree.delete(p)

    def browse_blender(self):
        f = filedialog.askopenfilename(title="Blender executable")
        if f:
            self.blender_var.set(f)

    def detect_blender(self):
        found = config.find_blender()
        if found:
            self.blender_var.set(found)
        else:
            messagebox.showwarning("Blender", "Blender was not found automatically. Please browse to blender.exe / Blender.app.")

    # ------------------------------------------------------------------ jobs
    def _apply_job(self, roots, overrides, settings):
        self.settings = settings
        self.sel = Selection(roots, overrides)
        self.blender_var.set(settings.blender)
        self.settings_panel.load(settings)
        self.refresh_tree()

    def new_job(self):
        self.job_path = None
        s = config.Settings(blender=self.settings.blender)
        self._apply_job([], {}, s)

    def open_job(self):
        f = filedialog.askopenfilename(title="Open job", filetypes=JOB_TYPES)
        if f:
            try:
                self._apply_job(*config.load_job(f))
                self.job_path = f
                self.log(f"Loaded job {f}")
            except Exception as exc:
                messagebox.showerror("Open job", str(exc))

    def save_job(self, ask=False):
        if ask or not self.job_path:
            f = filedialog.asksaveasfilename(title="Save job", defaultextension=".showcase-job.json", filetypes=JOB_TYPES)
            if not f:
                return
            self.job_path = f
        config.save_job(self.job_path, self.sel.roots, self.sel.to_overrides(), self.settings)
        self.log(f"Saved job {self.job_path}")

    def _load_last_job(self):
        if os.path.exists(LAST_JOB):
            try:
                self._apply_job(*config.load_job(LAST_JOB))
                return
            except Exception:
                pass
        self.settings.blender = config.find_blender()
        self._apply_job([], {}, self.settings)

    def _on_close(self):
        if self.runner and self.worker_thread and self.worker_thread.is_alive():
            if not messagebox.askyesno("Quit", "Rendering is running. Stop it and quit?"):
                return
            self.runner.stop()
        try:
            config.save_job(LAST_JOB, self.sel.roots, self.sel.to_overrides(), self.settings)
        except OSError:
            pass
        self.destroy()

    # ------------------------------------------------------------------ running
    def log(self, msg):
        self.log_text.configure(state="normal")
        self.log_text.insert("end", msg + "\n")
        lines = int(self.log_text.index("end-1c").split(".")[0])
        if lines > 5000:
            self.log_text.delete("1.0", f"{lines - 5000}.0")
        self.log_text.see("end")
        self.log_text.configure(state="disabled")

    def _busy(self, busy):
        self.start_btn.configure(state="disabled" if busy else "normal")
        self.stop_btn.configure(state="normal" if busy else "disabled")

    def _check_ready(self):
        if not self.sel.roots:
            messagebox.showinfo("Nothing to do", "Add a library folder first.")
            return False
        if not (self.settings.blender or config.find_blender()):
            messagebox.showerror("Blender", "Set the path to the Blender executable first.")
            return False
        if not self.settings.views:
            messagebox.showerror("Views", "Select at least one view.")
            return False
        if self.settings.output_mode == "mirror" and not self.settings.output_dir:
            messagebox.showerror("Output", "Choose an output folder or select 'Next to each model'.")
            return False
        return True

    def start(self, selection=None, settings=None):
        if self.worker_thread and self.worker_thread.is_alive():
            return
        if not self._check_ready():
            return
        settings = settings or config.Settings.from_dict(self.settings.to_dict())
        selection = selection or Selection(self.sel.roots, self.sel.to_overrides())
        self.runner = Runner(settings, self.events.put)
        self._busy(True)
        self.status_var.set("Scanning...")
        self.worker_thread = threading.Thread(target=self._run_thread, args=(self.runner, selection), daemon=True)
        self.worker_thread.start()

    def _run_thread(self, runner, selection):
        try:
            runner.run(selection)
        except Exception as exc:
            self.events.put({"type": "log", "message": f"ERROR: {type(exc).__name__}: {exc}"})
            self.events.put({"type": "finished", "summary": {}, "cancelled": True})

    def render_selected(self):
        paths = [p for p in self._selected_paths()]
        if not paths:
            return
        sel = Selection(self.sel.roots, {})
        # include only the selected files/folders
        for r in sel.roots:
            sel.set(r, False)
        for p in paths:
            sel.set(p, True)
        s = config.Settings.from_dict(self.settings.to_dict())
        s.skip_existing = False
        self.start(selection=sel, settings=s)

    def stop(self):
        if self.runner:
            self.runner.stop()
            self.status_var.set("Stopping...")

    def scan(self):
        if not self.sel.roots:
            return
        sel = Selection(self.sel.roots, self.sel.to_overrides())
        skip = [self.settings.output_dir] if self.settings.output_mode == "mirror" else []

        def work():
            counts = {}
            n = 0
            for _, p in sel.iter_models(skip_dirs=skip):
                ext = os.path.splitext(p)[1].lower()
                counts[ext] = counts.get(ext, 0) + 1
                n += 1
            detail = ", ".join(f"{k}: {v}" for k, v in sorted(counts.items(), key=lambda kv: -kv[1]))
            self.events.put({"type": "log", "message": f"{n} model file(s) selected  ({detail})"})

        self.log("Counting selected models...")
        threading.Thread(target=work, daemon=True).start()

    def _poll_events(self):
        try:
            for _ in range(200):
                e = self.events.get_nowait()
                t = e["type"]
                if t == "log":
                    self.log(e["message"])
                elif t == "progress":
                    total = max(1, e["total"])
                    self.progress.configure(maximum=total, value=e["done"])
                    name = os.path.basename(e.get("current") or "")
                    self.status_var.set(f"{e['done']} / {e['total']}  {name}")
                elif t == "add_root":
                    self.status_var.set("Ready")
                    self._add_root_path(e["path"])
                elif t == "add_root_failed":
                    self.status_var.set("Ready")
                    messagebox.showerror("Network folder", e["message"])
                elif t == "populated":
                    self._on_populated(e)
                elif t == "result":
                    p = e["path"]
                    self.records[p] = (e["record"], e["json"][: -len(schema.SUFFIX_JSON)])
                    if self.tree.exists(p):
                        self._update_item(p)
                    if p == self._detail_path:
                        self._show_details(p)
                elif t == "finished":
                    self._busy(False)
                    summ = ", ".join(f"{k}: {v}" for k, v in sorted(e["summary"].items()))
                    self.status_var.set(("Cancelled" if e["cancelled"] else "Done") + (f" - {summ}" if summ else ""))
                    self._refresh_states()
        except queue.Empty:
            pass
        self.after(100, self._poll_events)

    # ------------------------------------------------------------------ tools
    def _index_targets(self):
        if self.settings.output_mode == "mirror" and self.settings.output_dir:
            return [self.settings.output_dir]
        return list(self.sel.roots)

    def rebuild_index(self):
        targets = self._index_targets()
        if not targets:
            return

        def work():
            for t in targets:
                index_db.rebuild(t, self.settings.index_path or None,
                                 log=lambda m: self.events.put({"type": "log", "message": m}))

        threading.Thread(target=work, daemon=True).start()

    def open_index_folder(self):
        targets = self._index_targets()
        if targets:
            open_external(os.path.dirname(self.settings.index_path) if self.settings.index_path else targets[0])

    def show_help(self):
        messagebox.showinfo("How it works", (
            "1. Add one or more library folders.\n"
            "2. Click the 'Render' column (or press Space, or right-click) to include / exclude "
            "folders or single models. Excluding a folder excludes everything in it; you can "
            "re-include single items inside. A '.noshowcase' file in a folder excludes it too.\n"
            "3. Pick a preset (quick / standard / high) and press Start.\n\n"
            "Each model gets <name>.preview.jpg, <name>.showcase.jpg, <name>.info.txt and "
            "<name>.info.json. All results are also collected in showcase_index.sqlite and "
            "showcase_index.csv in the library root (or output folder).\n\n"
            "Status: OK / WARNING (usable, check issues) / BROKEN (could not be imported or empty) / "
            "ERROR (timeout or render problem - re-run) / UNSUPPORTED (no reader for this format here).\n\n"
            "Rare formats (OpenFlight .flt, DAE, 3DS, X, LWO, AC, OFF, 3MF, AMF, DXF, ...) are read by "
            "built-in readers; about 40 more work when assimp or OpenSceneGraph (osgconv) is installed.\n\n"
            "Already processed and unchanged files are skipped, so you can stop and continue any time."))


def main():
    if platform.system() == "Windows":
        try:
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except Exception:
            pass
    app = App()
    app.mainloop()


if __name__ == "__main__":
    main()
