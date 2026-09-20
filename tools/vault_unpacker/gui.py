# -*- coding: utf-8 -*-
"""Vault 解包器 —— tkinter 图形界面。

界面用的都是「自绘」的经典 Tk 控件（tk.Frame / tk.Button / tk.Label / tk.Canvas），
不用 ttk 主题控件。原因：ttk 的按钮、复选框、进度条颜色在 Windows 原生主题下改不动，
在 150% DPI 上既不好看也不统一。自己画反而更稳，也更容易做出硬边色块的观感。

DPI：Tk 的点值字号会按 tk scaling 自动放大，但 padx / width / geometry 这类
像素尺寸不会，所以统一走 self.px() 做等比换算。

配置：仓库地址 / 账号密码 / 目录全部放在外部 JSON 里（见 config.py），
启动时读、改动时写，源码里不留任何个人配置。
"""

import os
import queue
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from . import config, core, icon, inject, playnite
from . import __version__ as APP_VERSION

APP_TITLE = "Vault 解包器"
APP_SUB = "从 NAS 仓库取回游戏，可选用 Playnite 登记"

POLL_MS = 80

# 表单标签列的**最小**像素宽度：所有卡片共用，保证冒号位置对齐。
# 实际宽度取「最宽的那个标签」——写死会出现「输出父目录」这种长标签溢出容器、
# 压到右边输入框上的问题（实测 reqwidth 106px > 容器 93px）。
LABEL_W = 62

# ---------------- 调色板（与图标同源）----------------
INK = "#12131A"        # 主描边 / 深色底
INK_2 = "#2A2F3D"      # 深色底上的悬停
YELLOW = "#FFD447"     # 强调色
BLUE = "#5B9BFF"       # 主操作
BLUE_D = "#3B7BE0"
BLUE_L = "#A8CDFF"
PINK = "#FF7BA9"
GREEN = "#3BC97F"
WHITE = "#FFFFFF"
BG = "#F1F3F8"         # 页面底色
LINE = "#C9CEDA"       # 卡片描边
LINE_SOFT = "#E3E6EC"
MUTED = "#6B7280"
TRACK = "#E3E7F0"
OFF_BG = "#DFE3EB"
OFF_FG = "#9BA1AF"
LOG_BG = "#12141B"
LOG_FG = "#C7D0E0"

BTN_KINDS = {
    #      底色     字色     悬停        禁用底
    "pri": (BLUE, WHITE, BLUE_D, "#C6D6F5"),
    "main": (YELLOW, INK, "#FFDF6B", "#EFE6CC"),
    "ghost": (WHITE, INK, "#EEF1F6", "#E8EBF1"),
    "danger": (PINK, INK, "#FF95B8", "#F3DCE5"),
}

STATUS_STYLE = {
    "idle": (YELLOW, INK, "就绪"),
    "run": (BLUE, WHITE, "运行中"),
    "ok": (GREEN, INK, "完成"),
    "fail": (PINK, INK, "失败"),
    "stop": ("#B9C2D6", INK, "已取消"),
}


def _enable_dpi_awareness():
    try:
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        try:
            import ctypes
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def _px(n, s):
    return max(1, int(round(n * s)))


class NeoCheck(tk.Frame):
    """自绘复选框：硬边黑框，选中时蓝底 + 白勾。

    不用 ttk.Checkbutton 是因为原生主题下指示器又小又改不动颜色。
    """

    def __init__(self, master, text, variable, command=None,
                 scale=1.0, ui_font="Segoe UI"):
        tk.Frame.__init__(self, master, bg=WHITE)
        self.var = variable
        self.command = command
        self.scale = scale
        self._enabled = True

        box = _px(15, scale)
        self.canvas = tk.Canvas(self, width=box, height=box, bg=WHITE, bd=0,
                                highlightthickness=_px(1, scale),
                                highlightbackground=INK, highlightcolor=INK)
        self.canvas.pack(side="left")
        self.label = tk.Label(self, text=text, bg=WHITE, fg=INK,
                              font=(ui_font, 10))
        self.label.pack(side="left", padx=(_px(8, scale), 0))
        for w in (self.canvas, self.label):
            w.bind("<Button-1>", self._toggle)
            w.configure(cursor="hand2")
        self._redraw()

    def _toggle(self, _e=None):
        if not self._enabled:
            return
        self.var.set(not self.var.get())
        self._redraw()
        if self.command:
            self.command()

    def set_enabled(self, on):
        self._enabled = bool(on)
        cur = "hand2" if on else "arrow"
        self.label.configure(fg=INK if on else OFF_FG, cursor=cur)
        self.canvas.configure(highlightbackground=INK if on else "#D6DAE3",
                              cursor=cur)
        self._redraw()

    def set_text(self, text):
        self.label.configure(text=text)

    def _redraw(self):
        c = self.canvas
        c.delete("all")
        s = self.scale
        box = 15.0 * s
        on = bool(self.var.get())
        if not self._enabled:
            fill = "#F0F2F6"
        else:
            fill = BLUE if on else WHITE
        c.create_rectangle(0, 0, box, box, fill=fill, outline="", width=0)
        if on:
            xy = []
            for x, y in ((0.22, 0.52), (0.42, 0.72), (0.78, 0.28)):
                xy += [x * box, y * box]
            c.create_line(*xy, fill=WHITE, width=max(2, int(round(2.2 * s))),
                          capstyle="round", joinstyle="round")


class QueueReporter(core.Reporter):
    """把核心的回调转成队列消息，交给主线程渲染（tkinter 不能跨线程）。"""

    def __init__(self, q):
        self.q = q

    def log(self, msg):
        self.q.put(("log", msg))

    def progress(self, done, total, label=""):
        self.q.put(("progress", done, total, label))

    def stage(self, name):
        self.q.put(("stage", name))


class _BatchReporter(core.Reporter):
    """多应用解包时，把「当前应用内部的进度」折算成整体进度。

    否则进度条会在第一个应用上冲到 100%、下一个应用又跳回 0%，看着像出错了。
    第 idx 个应用（从 1 数）占据整体进度里 [ (idx-1)/n, idx/n ] 这一段。
    """

    def __init__(self, q, done_apps, total_apps):
        self.q = q
        self.base = (float(done_apps) / total_apps) if total_apps else 0.0
        self.span = (1.0 / total_apps) if total_apps else 1.0

    def log(self, msg):
        self.q.put(("log", msg))

    def stage(self, name):
        self.q.put(("stage", name))

    def progress(self, done, total, label=""):
        ratio = 0.0
        if total:
            ratio = max(0.0, min(1.0, float(done) / float(total)))
        self.q.put(("progress", self.base + ratio * self.span, 1.0, label))


class App(tk.Tk):
    def __init__(self):
        tk.Tk.__init__(self)

        # 1) DPI：Tk 的点值字号会自动缩放，像素尺寸得自己算
        try:
            self.s = min(2.0, max(1.0, round(self.winfo_fpixels("1i") / 96.0, 2)))
        except Exception:
            self.s = 1.0
        self._pick_fonts()

        self.title(APP_TITLE)
        self.configure(bg=BG)

        # 窗口 / 任务栏图标。用内嵌 PNG（base64）而不是外部 .ico 文件，
        # 这样源码直跑和单文件 exe 冻结后走的是同一条路，不会出现「找不到图标」。
        # 注意：exe 自己的文件图标由 PyInstaller --icon 决定，和这里无关。
        try:
            self._icon_win = icon.load(icon.WINDOW_SIZE)
            self.iconphoto(True, self._icon_win)
        except Exception:
            self._icon_win = None

        # 外部配置文件：个人设置不写进代码。读不到就建一份模板，
        # 老版本存在 state.json 里的设置会自动搬过来。
        try:
            self.cfg_path, self.cfg, cfg_created = config.ensure()
        except Exception:
            self.cfg_path, self.cfg, cfg_created = "", {}, False

        self.events = queue.Queue()
        self.cancel = threading.Event()
        self.worker = None
        self.apps = []
        self.plugin_dir = None
        self._is_running = False
        self._ratio = 0.0
        self._targets = []          # 本次要解包的目标 [(app_id, name, out_dir), ...]
        self._label_holders = []    # [(容器, 标签)]，搭完界面后统一对齐宽度
        self.label_col_w = self.px(LABEL_W)

        self._init_style()
        self._build_ui()
        self._restore_state()
        self._autodetect_playnite()

        self._fit_window()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.after(POLL_MS, self._pump)

        if self.cfg_path:
            self._append_log("[配置] %s %s"
                             % ("已生成" if cfg_created else "已读取",
                                os.path.basename(self.cfg_path)), "dim")

    # ---------------- 基础设施 ----------------

    def px(self, n):
        return _px(n, self.s)

    def _pick_fonts(self):
        try:
            import tkinter.font as tkfont
            fams = set(tkfont.families(self))
        except Exception:
            fams = set()
        self.ui_font = "Segoe UI"
        for cand in ("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial"):
            if cand in fams:
                self.ui_font = cand
                break
        self.mono_font = "Courier New"
        for cand in ("Cascadia Mono", "Consolas", "Courier New"):
            if cand in fams:
                self.mono_font = cand
                break

    def _init_style(self):
        st = ttk.Style(self)
        try:
            st.theme_use("clam")
        except tk.TclError:
            pass
        # 唯一还在用的 ttk 控件是应用列表（Treeview），其余都是自绘
        st.configure(".", background=BG, foreground=INK,
                     font=(self.ui_font, 10))
        st.configure(
            "Vault.Treeview",
            background=WHITE, fieldbackground=WHITE, foreground=INK,
            borderwidth=0, relief="flat",
            rowheight=self.px(24), font=(self.ui_font, 10))
        st.configure(
            "Vault.Treeview.Heading",
            background=INK, foreground=WHITE, relief="flat", borderwidth=0,
            font=(self.ui_font, 10, "bold"),
            padding=(self.px(6), self.px(6)))
        st.map("Vault.Treeview.Heading",
               background=[("active", INK_2)])
        st.map("Vault.Treeview",
               background=[("selected", YELLOW)],
               foreground=[("selected", INK)])

        # 滚动条走 ttk + clam 主题。经典 tk.Scrollbar 在 Windows 上会忽略
        # troughcolor，画出来的是一条浅灰槽 —— 嵌在深色日志框里非常扎眼。
        # clam 主题下 ttk 的 Scrollbar 老老实实按 style 里的颜色画。
        st.configure(
            "Vault.Vertical.TScrollbar",
            background="#C6CCDA", troughcolor="#EEF1F6",
            bordercolor="#EEF1F6", arrowcolor=INK,
            darkcolor="#C6CCDA", lightcolor="#C6CCDA",
            relief="flat", borderwidth=0,
            arrowsize=self.px(11), width=self.px(11))
        st.map("Vault.Vertical.TScrollbar",
               background=[("active", BLUE), ("pressed", BLUE_D)])

        st.configure(
            "Log.Vertical.TScrollbar",
            background="#3A4050", troughcolor=LOG_BG,
            bordercolor=LOG_BG, arrowcolor="#8A93A8",
            darkcolor="#3A4050", lightcolor="#3A4050",
            relief="flat", borderwidth=0,
            arrowsize=self.px(11), width=self.px(11))
        st.map("Log.Vertical.TScrollbar",
               background=[("active", BLUE), ("pressed", BLUE_D)])

    def _fit_window(self):
        """按屏幕比例给一个起始尺寸，但绝不小于内容实际需要的尺寸。

        同时把尺寸夹在屏幕以内：这个界面卡片多，内容需要的高度偏大，
        如果无条件照搬需要值，在 1080p 之类的小屏上会顶出屏幕外，
        底部的配置栏和日志就点不到了。
        """
        self.update_idletasks()
        need_w = self.winfo_reqwidth()
        need_h = self.winfo_reqheight()
        sw, sh = self.winfo_screenwidth(), self.winfo_screenheight()
        w = max(need_w, min(int(sw * 0.42), self.px(940)))
        h = max(need_h, min(int(sh * 0.64), self.px(700)))
        w = min(w, sw)
        h = min(h, int(sh * 0.94))
        x = max(0, (sw - w) // 2)
        y = max(0, (sh - h) // 3)
        self.geometry("%dx%d+%d+%d" % (w, h, x, y))
        # minsize 给得比「需要值」松一点，这样在矮屏上还能往下缩 ——
        # 缩的时候挤掉的是日志和应用列表（它们本来就会随窗口伸缩），
        # 不会把卡片挤变形。
        self.minsize(min(need_w, sw), min(need_h, int(sh * 0.72)))

    # ---------------- 小组件 ----------------

    def _card(self, parent, num, title, accent=BLUE, fg=WHITE,
              fill="x", expand=False, gap=6, head_extra=None):
        card = tk.Frame(parent, bg=WHITE, bd=0,
                        highlightthickness=self.px(1),
                        highlightbackground=LINE, highlightcolor=LINE)
        card.pack(fill=fill, expand=expand, pady=(0, self.px(gap)))

        head = tk.Frame(card, bg=WHITE)
        head.pack(fill="x", padx=self.px(12), pady=(self.px(6), 0))
        tk.Label(head, text=num, bg=accent, fg=fg, bd=0,
                 font=(self.ui_font, 9, "bold"),
                 padx=self.px(4), pady=self.px(1)).pack(side="left")
        tk.Label(head, text=title, bg=WHITE, fg=INK,
                 font=(self.ui_font, 10, "bold")).pack(
            side="left", padx=(self.px(8), 0))
        if head_extra is not None:
            head_extra(head)

        body = tk.Frame(card, bg=WHITE)
        body.pack(fill="both", expand=expand,
                  padx=self.px(12), pady=(self.px(4), self.px(8)))
        return body

    def _btn(self, parent, text, command, kind="ghost",
             size=10, padx=14, pady=6, enabled=True):
        bg, fg, hot, off = BTN_KINDS[kind]
        b = tk.Button(parent, text=text, command=command, bg=bg, fg=fg,
                      activebackground=hot, activeforeground=fg,
                      disabledforeground=OFF_FG, bd=0, relief="flat",
                      highlightthickness=self.px(1),
                      highlightbackground=INK, highlightcolor=INK,
                      font=(self.ui_font, size, "bold"),
                      padx=self.px(padx), pady=self.px(pady),
                      cursor="hand2", takefocus=0)
        b._k = (bg, fg, hot, off)
        b._enabled = bool(enabled)
        b.bind("<Enter>", lambda _e: self._btn_hover(b, True))
        b.bind("<Leave>", lambda _e: self._btn_hover(b, False))
        self._btn_state(b, bool(enabled))
        return b

    def _btn_hover(self, b, on):
        if not b._enabled:
            return
        bg, _fg, hot, _off = b._k
        b.configure(bg=hot if on else bg)

    def _btn_state(self, b, on):
        bg, fg, _hot, off = b._k
        b._enabled = bool(on)
        b.configure(state="normal" if on else "disabled",
                    bg=bg if on else off,
                    fg=fg if on else OFF_FG,
                    cursor="hand2" if on else "arrow")

    def _mk_entry(self, parent, textvariable, show=None, width=None):
        """返回 (外框, 输入框)。外框负责画 1px 边框并随焦点变色。"""
        holder = tk.Frame(parent, bg=WHITE, bd=0,
                          highlightthickness=self.px(1),
                          highlightbackground=LINE, highlightcolor=LINE)
        kw = dict(textvariable=textvariable, bd=0, relief="flat",
                  bg=WHITE, fg=INK, insertbackground=BLUE, insertwidth=2,
                  selectbackground=BLUE_L, selectforeground=INK,
                  highlightthickness=0, font=(self.ui_font, 10))
        if show:
            kw["show"] = show
        if width:
            kw["width"] = width
        e = tk.Entry(holder, **kw)
        e.pack(fill="both", expand=True,
               padx=self.px(7), pady=self.px(4))
        e.bind("<FocusIn>", lambda _e, h=holder:
               h.configure(highlightbackground=BLUE))
        e.bind("<FocusOut>", lambda _e, h=holder:
               h.configure(highlightbackground=LINE))
        return holder, e

    def _set_entry_enabled(self, holder, entry, on):
        bg = WHITE if on else "#F5F6F9"
        entry.configure(state="normal" if on else "disabled",
                        bg=bg, disabledbackground=bg,
                        disabledforeground=OFF_FG)
        holder.configure(highlightbackground=LINE if on else LINE_SOFT)

    def _row(self, parent, pady=0):
        r = tk.Frame(parent, bg=WHITE)
        r.pack(fill="x", pady=pady if isinstance(pady, tuple) else self.px(pady))
        return r

    def _label(self, parent, text, w=LABEL_W, **pack_kw):
        """表单左侧标签：固定**像素**宽度，让所有卡片的输入框左边线对齐。

        为什么要包一层固定宽度的 Frame：tk.Label 自己的 width 是按「字符数」算的，
        中英文混排时宽度飘忽，冒号对不齐。而容器一旦 pack_propagate(False)，
        它的高度就变成 0 —— 所以必须 pack(fill="y") 让它撑满行高，
        里面那个 Label 再 fill+expand，文字才会垂直居中（不写这行会被压成一条线）。

        宽度先按 w 建，等界面搭完由 _fit_label_column() 统一放大到「最宽的那个标签」，
        否则像「输出父目录」这种 5 个字的标签会溢出容器、压到输入框上。
        """
        holder = tk.Frame(parent, bg=WHITE, width=self.px(w))
        holder.pack_propagate(False)
        kw = dict(side="left", fill="y")
        kw.update(pack_kw)
        holder.pack(**kw)
        cap = tk.Label(holder, text=text, bg=WHITE, fg=MUTED, anchor="w",
                       font=(self.ui_font, 10))
        cap.pack(fill="both", expand=True)
        self._label_holders.append((holder, cap))
        return holder

    def _fit_label_column(self, gap=12):
        """把标签列统一成「最宽的那个标签 + 间距」的像素宽度。

        用字体直接量文字宽度，而不是读 winfo_reqwidth()：没被 pack 出来的那个
        分支（本地目录 / WebDAV 只有一个是可见的）几何没算过，读出来是 0。
        宽度只放大不缩小，所以不会比 LABEL_W 更窄。
        """
        if not self._label_holders:
            return
        import tkinter.font as tkfont
        f = tkfont.Font(font=(self.ui_font, 10))
        widest = max(f.measure(cap.cget("text")) for _h, cap in self._label_holders)
        width = max(self.px(LABEL_W), widest + self.px(gap))
        for holder, _cap in self._label_holders:
            holder.configure(width=width)
        self.label_col_w = width

    def _hint(self, parent, pady=(0, 0)):
        """卡片底部那行小灰字。统一字号与换行宽度，省得到处写。"""
        lab = tk.Label(parent, text="", bg=WHITE, fg=MUTED, anchor="w",
                       justify="left", font=(self.ui_font, 9),
                       wraplength=self.px(640))
        lab.pack(fill="x", pady=pady)
        return lab

    def _set_status(self, kind):
        bg, fg, text = STATUS_STYLE.get(kind, STATUS_STYLE["idle"])
        self.pill.configure(bg=bg, fg=fg, text=text)

    # ---------------- 界面 ----------------

    def _build_ui(self):
        # ---- 页眉 ----
        head = tk.Frame(self, bg=INK)
        head.pack(fill="x")
        inner = tk.Frame(head, bg=INK)
        inner.pack(fill="x", padx=self.px(16), pady=self.px(10))

        try:
            self._icon_badge = icon.load(self.px(46))
        except Exception:
            self._icon_badge = None
        if self._icon_badge is not None:
            tk.Label(inner, image=self._icon_badge, bg=INK, bd=0).pack(side="left")

        txt = tk.Frame(inner, bg=INK)
        txt.pack(side="left", padx=(self.px(14), 0))
        tk.Label(txt, text=APP_TITLE, bg=INK, fg=WHITE,
                 font=(self.ui_font, 14, "bold")).pack(anchor="w")
        tk.Label(txt, text=APP_SUB, bg=INK, fg="#B9C2D6",
                 font=(self.ui_font, 10)).pack(anchor="w", pady=(self.px(2), 0))

        self.pill = tk.Label(inner, text="就绪", bg=YELLOW, fg=INK, bd=0,
                             font=(self.ui_font, 10, "bold"),
                             padx=self.px(12), pady=self.px(4))
        self.pill.pack(side="right", pady=self.px(6))

        tk.Frame(self, bg=YELLOW, height=self.px(4)).pack(fill="x")

        # ---- 正文 ----
        body = tk.Frame(self, bg=BG)
        body.pack(fill="both", expand=True,
                  padx=self.px(14), pady=(self.px(10), self.px(6)))

        self._build_source_card(body)
        self._build_list_card(body)
        self._build_output_card(body)
        self._build_run_card(body)
        self._build_log_card(body)

        self._build_foot()

        # 所有标签都建完了，统一按最宽的那个定列宽（否则「输出父目录」会压到输入框）
        self._fit_label_column()

        # 手动改这两个输入框时，预览也要跟着变
        self.local_var.trace_add("write", lambda *_: self._on_local_changed())
        self.out_var.trace_add("write", lambda *_: self._on_out_changed())

        self._update_seg()
        self._on_mode()
        self._on_register_toggle()
        self._sync_open_btn()
        self._update_target_preview()

    def _build_foot(self):
        """底栏：只放版本号和「打开配置」入口。

        配置固定放在 exe 同目录的 config.json，不在这里把路径打印出来 ——
        要改配置就点「打开配置」，会用资源管理器直接定位到那个文件。
        """
        foot = tk.Frame(self, bg=BG)
        foot.pack(fill="x", padx=self.px(14), pady=(0, self.px(8)))

        tk.Label(foot, text="v" + APP_VERSION, bg=BG, fg="#A7ADBB",
                 font=(self.ui_font, 9)).pack(side="left")
        self.btn_cfg = self._btn(foot, "打开配置", self._open_config,
                                 kind="ghost", size=9, padx=10, pady=3)
        self.btn_cfg.pack(side="right")
        # 「安装插件到 Playnite」单独开一个窗口，不动主界面的布局 ——
        # 主界面那五张卡片是给「解包」这条主线用的，把安装流程塞进去只会把主线挤乱。
        self.btn_plug = self._btn(foot, "安装插件到 Playnite…",
                                  self._open_plugin_dialog,
                                  kind="main", size=9, padx=12, pady=3)
        self.btn_plug.pack(side="right", padx=(0, self.px(8)))

    def _open_plugin_dialog(self):
        """打开「安装插件到 Playnite」窗口（同一个窗口再点就置顶，不重复开）。"""
        dlg = getattr(self, "_plugin_dlg", None)
        if dlg is not None and dlg.winfo_exists():
            dlg.deiconify()
            dlg.lift()
            dlg.focus_set()
            return dlg
        self._plugin_dlg = PluginInstaller(self)
        return self._plugin_dlg

    def _build_source_card(self, parent):
        card = self._card(parent, "1", "选择来源")

        seg = tk.Frame(card, bg=WHITE)
        seg.pack(fill="x")
        self.seg_btns = {}
        for key, label in (("http", "WebDAV 仓库"),
                           ("local", "本地目录（已下载的 apps/）")):
            b = self._btn(seg, label, lambda k=key: self._set_mode(k),
                          kind="ghost", size=10, padx=16, pady=6)
            b.pack(side="left")
            self.seg_btns[key] = b

        self.mode = tk.StringVar(value="http")

        # 两种模式的字段数量不同（WebDAV 比本地多两行）。如果让卡片按内容自适应，
        # 一切换整张卡就忽高忽低、下面所有卡片跟着跳，很难看。
        # 所以两种模式共用一个**固定高度**的容器：高度取两者里面高的那个，
        # 矮的那个靠底部说明行把空间填满，切换时布局纹丝不动。
        self.src_fixed = tk.Frame(card, bg=WHITE)
        self.src_fixed.pack(fill="x", pady=(self.px(8), 0))
        self.src_fixed.pack_propagate(False)

        # --- WebDAV 模式 ---
        self.http_box = tk.Frame(self.src_fixed, bg=WHITE)
        self.url_var = tk.StringVar()
        r = self._row(self.http_box)
        self._label(r, "地址").pack(side="left")
        h_url, self.e_url = self._mk_entry(r, self.url_var)
        h_url.pack(side="left", fill="x", expand=True)

        self.user_var = tk.StringVar()
        self.pw_var = tk.StringVar()
        r = self._row(self.http_box, (self.px(6), 0))
        self._label(r, "账号").pack(side="left")
        h_user, self.e_user = self._mk_entry(r, self.user_var)
        h_user.pack(side="left", fill="x", expand=True)
        tk.Label(r, text="密码", bg=WHITE, fg=MUTED, anchor="w",
                 font=(self.ui_font, 10)).pack(
            side="left", padx=(self.px(10), self.px(6)))
        h_pw, self.e_pw = self._mk_entry(r, self.pw_var, show="*")
        h_pw.pack(side="left", fill="x", expand=True)

        self.insecure_var = tk.BooleanVar(value=False)
        self.chk_insecure = NeoCheck(self.http_box, "跳过证书校验（自签 HTTPS）",
                                     self.insecure_var, scale=self.s,
                                     ui_font=self.ui_font)
        self.chk_insecure.pack(anchor="w", pady=(self.px(8), 0))

        # --- 本地目录模式 ---
        self.local_box = tk.Frame(self.src_fixed, bg=WHITE)
        self.local_var = tk.StringVar()
        r = self._row(self.local_box)
        self._label(r, "目录").pack(side="left")
        h_local, self.e_local = self._mk_entry(r, self.local_var)
        h_local.pack(side="left", fill="x", expand=True)
        self.btn_pick_local = self._btn(r, "浏览…", self._pick_local,
                                        kind="ghost", size=9, padx=10, pady=4)
        self.btn_pick_local.pack(side="left", padx=(self.px(6), 0))

        # 和 WebDAV 那行的复选框对齐：本地模式用它显示自动识别出来的布局
        self.local_kind = tk.Label(self.local_box, text="", bg=WHITE, fg=MUTED,
                                   anchor="w", font=(self.ui_font, 9))
        self.local_kind.pack(anchor="w", pady=(self.px(8), 0))

        # --- 底部公共说明（两种模式共用，顺便吃掉落差）---
        self.src_note = tk.Label(self.src_fixed, text="", bg=WHITE, fg=MUTED,
                                 anchor="w", justify="left",
                                 font=(self.ui_font, 9),
                                 wraplength=self.px(620))
        self.src_note.pack(side="bottom", fill="x")

        # 操作行（在固定容器外面，两种模式共用）
        act = self._row(card, (self.px(10), 0))
        self.btn_load = self._btn(act, "连接并读取应用列表", self._load_apps,
                                  kind="main", size=10, padx=16, pady=6)
        self.btn_load.pack(side="left")
        self.src_hint = tk.Label(act, text="", bg=WHITE, fg=MUTED,
                                 font=(self.ui_font, 10))
        self.src_hint.pack(side="left", padx=(self.px(12), 0))

    def _lock_source_height(self):
        """算出让两种模式完全等高的固定高度。

        取 WebDAV / 本地两边的高度最大值 —— WebDAV 多两行，所以通常就是它。
        winfo_reqheight() 是控件自身按子控件算出来的，不要求它当前被 pack 着。
        """
        self.update_idletasks()
        try:
            h_http = self.http_box.winfo_reqheight()
            h_local = self.local_box.winfo_reqheight()
            h_note = self.src_note.winfo_reqheight()
        except Exception:
            h_http = h_local = h_note = 0
        need = max(h_http, h_local) + h_note + self.px(6)
        if need <= self.px(20):          # 量不出来时给个保底，别把内容裁掉
            need = self.px(92) + h_note
        self.src_fixed.configure(height=need)

    def _build_list_card(self, parent):
        card = self._card(parent, "2", "选择应用（可多选）",
                          fill="both", expand=True,
                          head_extra=self._list_head_extra)

        wrap = tk.Frame(card, bg=WHITE, bd=0,
                        highlightthickness=self.px(1),
                        highlightbackground=LINE, highlightcolor=LINE)
        wrap.pack(side="left", fill="both", expand=True)

        cols = ("id", "dir", "name", "size", "files", "layout", "state")
        self.tree = ttk.Treeview(wrap, columns=cols, show="headings",
                                 height=4, style="Vault.Treeview",
                                 selectmode="extended")
        for c, t, w, anchor in (
            ("id", "Id（仓库）", 150, "w"),
            ("dir", "子目录名", 150, "w"),
            ("name", "名称", 220, "w"),
            ("size", "大小", 90, "e"),
            ("files", "文件", 60, "e"),
            ("layout", "布局", 76, "e"),
            ("state", "状态", 80, "center"),
        ):
            self.tree.heading(c, text=t)
            self.tree.column(c, width=self.px(w), anchor=anchor,
                             minwidth=self.px(48), stretch=(c == "name"))
        self.tree.pack(side="left", fill="both", expand=True)
        self.tree.tag_configure("inst", foreground="#1F8A52")
        self.tree.tag_configure("bad", foreground="#D6455F")
        self.tree.bind("<<TreeviewSelect>>", self._on_select)
        self.tree.bind("<Double-1>", lambda _e: self._start())

        sb = ttk.Scrollbar(wrap, orient="vertical", style="Vault.Vertical.TScrollbar",
                           command=self.tree.yview)
        sb.pack(side="right", fill="y")
        self.tree.configure(yscrollcommand=sb.set)

    def _list_head_extra(self, head):
        """列表卡右上角：全选 / 全不选。多选之后这个很省事。"""
        self.btn_sel_none = self._btn(head, "全不选", self._select_none,
                                      kind="ghost", size=9, padx=10, pady=3)
        self.btn_sel_none.pack(side="right")
        self.btn_sel_all = self._btn(head, "全选", self._select_all,
                                     kind="ghost", size=9, padx=10, pady=3)
        self.btn_sel_all.pack(side="right", padx=(0, self.px(6)))

    def _build_output_card(self, parent):
        card = self._card(parent, "3", "输出与登记")

        # 这里填的是**父目录**：每个应用会各自解到 <父目录>/<子目录名>/ 里。
        # 子目录名优先取「归档时该应用原来的安装目录名」（Brotato），
        # 旧归档没记就退回仓库里的 id（slug）。
        self.out_var = tk.StringVar()
        r = self._row(card)
        self._label(r, "输出父目录").pack(side="left")
        h_out, self.e_out = self._mk_entry(r, self.out_var)
        h_out.pack(side="left", fill="x", expand=True)
        self.btn_pick_out = self._btn(r, "浏览…", self._pick_out,
                                      kind="ghost", size=9, padx=10, pady=4)
        self.btn_pick_out.pack(side="left", padx=(self.px(6), 0))

        self.out_preview = tk.Label(card, text="", bg=WHITE, fg="#4A5060",
                                    anchor="w", justify="left",
                                    font=(self.mono_font, 9),
                                    wraplength=self.px(640))
        self.out_preview.pack(fill="x", pady=(self.px(4), 0))

        self.register_var = tk.BooleanVar(value=True)
        self.chk_register = NeoCheck(
            card, "解包后登记到 Playnite（让库里的条目变成「已安装」）",
            self.register_var, command=self._on_register_toggle,
            scale=self.s, ui_font=self.ui_font)
        self.chk_register.pack(anchor="w", pady=(self.px(10), 0))

        pl = self._row(card, (self.px(8), 0))
        self._label(pl, "插件数据").pack(side="left")
        self.plugin_var = tk.StringVar()
        self.h_plugin, self.e_plugin = self._mk_entry(pl, self.plugin_var)
        self.h_plugin.pack(side="left", fill="x", expand=True)
        self.btn_pick_plugin = self._btn(pl, "浏览…", self._pick_plugin,
                                         kind="ghost", size=9, padx=10, pady=4)
        self.btn_pick_plugin.pack(side="left", padx=(self.px(6), 0))
        self.btn_auto_plugin = self._btn(pl, "自动检测",
                                         lambda: self._autodetect_playnite(True),
                                         kind="ghost", size=9, padx=10, pady=4)
        self.btn_auto_plugin.pack(side="left", padx=(self.px(6), 0))

        self.pl_hint = self._hint(card, pady=(self.px(6), 0))

    def _build_run_card(self, parent):
        card = self._card(parent, "4", "开始解包")

        run = tk.Frame(card, bg=WHITE)
        run.pack(fill="x")
        self.btn_go = self._btn(run, "开始解包", self._start,
                                kind="pri", size=11, padx=22, pady=8)
        self.btn_go.pack(side="left")
        self.btn_cancel = self._btn(run, "取消", self._cancel,
                                    kind="danger", size=10, padx=16, pady=7,
                                    enabled=False)
        self.btn_cancel.pack(side="left", padx=(self.px(8), 0))
        self.btn_open = self._btn(run, "打开输出目录", self._open_out,
                                  kind="ghost", size=10, padx=16, pady=7,
                                  enabled=False)
        self.btn_open.pack(side="right")
        self.btn_cfg2 = self._btn(run, "编辑配置", self._open_config,
                                  kind="ghost", size=10, padx=16, pady=7)
        self.btn_cfg2.pack(side="right", padx=(0, self.px(8)))

        self.bar = tk.Canvas(card, height=self.px(16), bg=TRACK, bd=0,
                             highlightthickness=0)
        self.bar.pack(fill="x", pady=(self.px(10), self.px(6)))
        self.bar.bind("<Configure>", lambda _e: self._draw_bar())

        info = tk.Frame(card, bg=WHITE)
        info.pack(fill="x")
        self.pct = tk.Label(info, text="0.0%", bg=WHITE, fg=INK, anchor="e",
                            width=7, font=(self.mono_font, 10, "bold"))
        self.pct.pack(side="left")
        self.stage_lbl = tk.Label(info, text="就绪", bg=WHITE, fg=MUTED,
                                  anchor="w", font=(self.ui_font, 10))
        self.stage_lbl.pack(side="left", fill="x", expand=True,
                            padx=(self.px(10), 0))
        self.plan_lbl = tk.Label(info, text="", bg=WHITE, fg=MUTED, anchor="e",
                                 font=(self.ui_font, 9))
        self.plan_lbl.pack(side="right")

    def _build_log_card(self, parent):
        card = tk.Frame(parent, bg=WHITE, bd=0,
                        highlightthickness=self.px(1),
                        highlightbackground=LINE, highlightcolor=LINE)
        card.pack(fill="both", expand=True)

        head = tk.Frame(card, bg=WHITE)
        head.pack(fill="x", padx=self.px(12), pady=(self.px(10), 0))
        tk.Label(head, text="日志", bg=WHITE, fg=INK,
                 font=(self.ui_font, 10, "bold")).pack(side="left")

        box = tk.Frame(card, bg=WHITE, bd=0,
                       highlightthickness=self.px(1),
                       highlightbackground=INK, highlightcolor=INK)
        box.pack(fill="both", expand=True, padx=self.px(12),
                 pady=(self.px(8), self.px(12)))

        self.log = tk.Text(box, height=3, wrap="none", bd=0, relief="flat",
                           font=(self.mono_font, 9), bg=LOG_BG, fg=LOG_FG,
                           insertbackground=WHITE, highlightthickness=0,
                           selectbackground=BLUE, selectforeground=INK,
                           padx=self.px(8), pady=self.px(6))
        self.log.pack(side="left", fill="both", expand=True)
        lsb = ttk.Scrollbar(box, orient="vertical", style="Log.Vertical.TScrollbar",
                            command=self.log.yview)
        lsb.pack(side="right", fill="y")
        self.log.configure(yscrollcommand=lsb.set, state="disabled")

        self.log.tag_configure("err", foreground="#FF8296")
        self.log.tag_configure("ok", foreground="#7BE3A8")
        self.log.tag_configure("warn", foreground=YELLOW)
        self.log.tag_configure("info", foreground="#8AB4FF")
        self.log.tag_configure("dim", foreground="#6E7688")
        self.log.tag_configure("hi", foreground=WHITE)

    # ---------------- 状态恢复（读写外部配置） ----------------

    def _restore_state(self):
        c = self.cfg or {}
        self.url_var.set(c.get("webdav_url") or "")
        self.user_var.set(c.get("webdav_user") or "")
        self.pw_var.set(c.get("webdav_password") or "")
        self.insecure_var.set(bool(c.get("webdav_insecure_tls")))
        self.local_var.set(c.get("local_dir") or "")
        self.out_var.set(c.get("output_parent_dir") or "")
        if c.get("mode") in ("http", "local"):
            self.mode.set(c["mode"])
        self.register_var.set(bool(c.get("register_to_playnite", True)))
        if c.get("plugin_data_dir"):
            self.plugin_var.set(c["plugin_data_dir"])
        self._update_seg()
        self._on_mode()
        self._on_register_toggle()

    def _save_state(self):
        """把当前界面上的设置写回外部配置文件。

        注意 out_dir 存的是**父目录**（键名 output_parent_dir）。老版本它存的是
        单个应用的目录，升级后如果原样读进来会变成 <旧目录>/<app_id>/ 多套一层；
        _target_for() 里做了兜底识别，见那里的注释。
        """
        self.cfg["mode"] = self.mode.get()
        self.cfg["webdav_url"] = self.url_var.get().strip()
        self.cfg["webdav_user"] = self.user_var.get().strip()
        self.cfg["webdav_password"] = self.pw_var.get()
        self.cfg["webdav_insecure_tls"] = bool(self.insecure_var.get())
        self.cfg["local_dir"] = self.local_var.get().strip()
        self.cfg["output_parent_dir"] = self.out_var.get().strip()
        self.cfg["register_to_playnite"] = bool(self.register_var.get())
        self.cfg["plugin_data_dir"] = self.plugin_var.get().strip()
        try:
            config.save(self.cfg, self.cfg_path or None)
        except OSError as ex:
            self._append_log("[配置] 写入失败：%s" % ex, "warn")

    def _open_config(self):
        """打开配置文件（存在就直接定位到它，方便编辑）。"""
        if not self.cfg_path:
            messagebox.showwarning(APP_TITLE, "配置文件路径不可用。", parent=self)
            return
        self._save_state()          # 先把当前值落一次盘，免得改了半天发现没保存
        if config.open_in_explorer(self.cfg_path):
            self._append_log("[配置] " + self.cfg_path, "dim")
        else:
            messagebox.showinfo(APP_TITLE, "配置文件：\n%s" % self.cfg_path,
                               parent=self)

    # ---------------- 交互 ----------------

    def _set_mode(self, key):
        if self.mode.get() == key:
            return
        self.mode.set(key)
        self._update_seg()
        self._on_mode()

    def _update_seg(self):
        cur = self.mode.get()
        for key, b in self.seg_btns.items():
            bg, fg, hot, _off = b._k
            if key == cur:
                b._k = (INK, WHITE, INK_2, bg)
                b.configure(bg=INK, fg=WHITE, activebackground=INK_2)
            else:
                b._k = (WHITE, INK, "#EEF1F6", bg)
                b.configure(bg=WHITE, fg=INK, activebackground="#EEF1F6")
            if not b._enabled:
                self._btn_state(b, False)

    def _on_mode(self):
        if self.mode.get() == "http":
            self.local_box.pack_forget()
            self.http_box.pack(side="top", fill="x")
            self.src_note.configure(
                text="WebDAV 地址不用带结尾斜杠，例：https://host:5006/app/Store ；"
                     "自签证书勾上「跳过证书校验」。")
        else:
            self.http_box.pack_forget()
            self.local_box.pack(side="top", fill="x")
            self.src_note.configure(
                text="选仓库根 / apps/ / 某个 apps/{id}/ 都行，会自动识别；"
                     "读本地磁盘，不走网络。")
        self._sync_local_kind()
        self._lock_source_height()

    def _sync_local_kind(self):
        """本地模式下顺手告诉用户「这个目录被识别成了什么」，选错了一看就知道。"""
        if not hasattr(self, "local_kind") or self.mode.get() != "local":
            return
        d = self.local_var.get().strip()
        if not d:
            self.local_kind.configure(text="", fg=MUTED)
            return
        if not os.path.isdir(d):
            self.local_kind.configure(text="目录不存在", fg="#D6455F")
            return
        try:
            src = core.LocalSource(d)
        except Exception:
            self.local_kind.configure(text="认不出来，将按仓库根处理", fg=MUTED)
            return
        if src.app_dir:
            what, color, n = "单个应用目录（%s）" % src.app_id, "#1F8A52", 1
        elif os.path.isfile(os.path.join(src.root, "index.json")):
            what, color = "仓库根（含 index.json）", "#1F8A52"
            n = self._count_apps(src)
        else:
            what, color = "仓库根（无 index.json，将扫描 apps/）", MUTED
            n = self._count_apps(src)

        text = "识别为：%s" % what
        if n is not None:
            text += "　·　发现 %d 个应用" % n
        self.local_kind.configure(text=text, fg=color)

    def _count_apps(self, src):
        """数一下来源里有多少个应用。UNC 路径（网络盘）不数，避免卡住界面。"""
        try:
            if src.root.startswith("\\\\") or src.root.startswith("//"):
                return None
            return len(src.candidate_dirs())
        except Exception:
            return None

    def _on_local_changed(self):
        """本地目录输入框改动时刷新识别结果。

        加 350ms 防抖：不然每敲一个字符都要去列一次目录，网络路径上会明显卡顿。
        """
        prev = getattr(self, "_local_after", None)
        if prev:
            try:
                self.after_cancel(prev)
            except Exception:
                pass
        self._local_after = self.after(350, self._sync_local_kind)

    def _on_out_changed(self):
        """输出目录被手动改动时刷新预览（选中变化走 _on_select）。"""
        if not hasattr(self, "out_preview"):
            return
        self._update_target_preview()
        self._sync_open_btn()

    def _on_register_toggle(self):
        on = self.register_var.get()
        self.chk_register.set_enabled(True)
        self._set_entry_enabled(self.h_plugin, self.e_plugin, on)
        self._btn_state(self.btn_pick_plugin, on)
        self._btn_state(self.btn_auto_plugin, on)
        if not on:
            self.pl_hint.configure(text="已关闭登记：只把文件解到本地，不写 Playnite。")

    def _autodetect_playnite(self, force=False):
        """定位插件数据目录。

        force=False（启动时）：已经配好的路径不动，只补日志，免得把用户在
        配置文件里手填的目录覆盖掉。
        force=True（点「自动检测」）：以检测结果为准。
        """
        d, note = playnite.find_plugin_data_dir()
        have = (self.plugin_var.get() or "").strip()

        if d and have and not force:
            if playnite.looks_like_plugin_data_dir(have):
                self.plugin_dir = playnite.looks_like_plugin_data_dir(have)
                self.pl_hint.configure(text="使用配置文件里的插件数据目录："
                                            + self.plugin_dir)
                return
        if d:
            self.plugin_dir = d
            self.plugin_var.set(d)
            running = playnite.is_playnite_running()
            extra = ""
            if running is True:
                extra = "  ⚠ Playnite 正在运行，登记后需在库里刷新一次。"
            elif running is None:
                extra = "  （查不出 Playnite 是否在运行）"
            self.pl_hint.configure(text="已识别插件数据目录。" + extra)
            self._append_log("[Playnite] 插件数据目录：" + d)
            self._save_state()
            apps = playnite.show_windows_paths()
            if apps:
                self._append_log("[Playnite] 当前已登记 %d 个应用" % len(apps))
        elif not have:
            self.plugin_var.set("")
            self.pl_hint.configure(text=note.replace("\n", " "))

    def _pick_out(self):
        init = self.out_var.get().strip() or playnite.default_out_root()
        d = filedialog.askdirectory(
            title="选择解包输出父目录（每个应用会解到它下面的子目录里）",
            initialdir=init, parent=self)
        if d:
            self.out_var.set(d)
            self._sync_open_btn()

    def _pick_local(self):
        d = filedialog.askdirectory(
            title="选择仓库目录（仓库根 / apps/ / 某个 apps/{id}/ 都行）",
            parent=self)
        if d:
            self.local_var.set(d)

    def _pick_plugin(self):
        d = filedialog.askdirectory(
            title="选择插件数据目录（ExtensionsData 下那个纯 GUID 目录）",
            parent=self)
        if not d:
            return
        real = playnite.looks_like_plugin_data_dir(d)
        if real:
            self.plugin_var.set(real)
            self.pl_hint.configure(text="已确认插件数据目录：" + real)
        else:
            self.plugin_var.set(d)
            self.pl_hint.configure(
                text="⚠ 这个目录里没找到 settings.json / local-index.json，"
                     "登记可能不会生效，请再确认一下。")

    def _selected_many(self):
        """按列表显示顺序返回所有选中的应用（多选）。

        返回的是 self.apps 里的**完整记录**（带 install_dir_name），
        算子目录名要用它；找不到记录时退回只剩 id / name 的最小字典。
        """
        sel = set(self.tree.selection())
        by_id = {str(a.get("id")): a for a in self.apps}
        out = []
        for item in self.tree.get_children():
            if item not in sel:
                continue
            vals = self.tree.item(item, "values")
            if not vals:
                continue
            out.append(by_id.get(str(vals[0])) or {"id": vals[0], "name": vals[2]})
        return out

    def _selected(self):
        apps = self._selected_many()
        return apps[0] if apps else None

    def _select_all(self):
        kids = self.tree.get_children()
        if kids:
            self.tree.selection_set(*kids)
            self.tree.focus(kids[0])

    def _select_none(self):
        self.tree.selection_remove(*self.tree.selection())

    def _bytes_of(self, app_id):
        for a in self.apps:
            if str(a.get("id")) == str(app_id):
                return int(a.get("total_bytes") or 0)
        return 0

    def _target_for(self, parent, app):
        """算出某个应用落盘到哪个目录：<父目录>/<子目录名>。

        子目录名的规则：**优先用归档时记下的原始安装目录名**
        （元数据 InstallDirName：上传前装在 D:\\Games\\Brotato → "Brotato"），
        这样解出来的目录和上传前一致；旧归档没有这个字段就退回 app_id。

        兜底：老版本把「某个应用自己的目录」当成输出目录存过，读进来后
        直接用会多套一层同名目录。这里认出来就退回它的上一级。

        app 既可以是完整记录（dict），也可以是纯 id 字符串。
        """
        folder = (core.folder_for(app) if isinstance(app, dict)
                  else (core.sanitize_folder_name(app) or str(app)))
        parent = os.path.normpath(parent)
        base = os.path.basename(parent)
        if base and base.lower() == folder.lower():
            up = os.path.dirname(parent)
            if up:
                parent = up
        return os.path.join(parent, folder)

    def _on_select(self, _e=None):
        """选中变化时，把输出父目录补成默认值并刷新预览。

        输出目录现在是**父目录**语义：这里只填父目录本身，不再往里拼 app_id，
        每个应用的最终落点交给 _target_for() 现算。
        """
        apps = self._selected_many()
        root_hint = playnite.default_out_root()
        cur = self.out_var.get().strip()

        if not cur:
            self.out_var.set(root_hint)
        else:
            def norm(p):
                return os.path.normcase(os.path.normpath(p))

            base = os.path.basename(os.path.normpath(cur))
            # 只有当前值「明显是自动填的」才改写，别覆盖用户手选的路径。
            # 第二种情况是在兜老版本的格式（父目录位置填的是 <root>/<子目录名>）。
            names = set()
            for a in apps:
                names.add(str(a["id"]).lower())
                names.add(core.folder_for(a).lower())
            auto = (norm(cur) in (norm(root_hint), norm(os.path.dirname(root_hint)))
                    or base.lower() in names)
            if auto:
                self.out_var.set(root_hint)

        self._update_target_preview()
        self._sync_open_btn()

    def _update_target_preview(self):
        """在卡片 3 里把「这次会解到哪些目录」直接列出来。"""
        apps = self._selected_many()
        parent = self.out_var.get().strip()
        if not apps or not parent:
            self.out_preview.configure(text="")
            self.plan_lbl.configure(text="")
            self.btn_go.configure(text="开始解包")
            return

        shown = apps[:4]
        lines = ["  " + self._target_for(parent, a) for a in shown]
        if len(apps) > len(shown):
            lines.append("  …另外 %d 个" % (len(apps) - len(shown)))
        head = ("将分别解到 %d 个子目录：" % len(apps) if len(apps) > 1
                else "将解到：")
        self.out_preview.configure(text=head + "\n" + "\n".join(lines))

        total = sum(self._bytes_of(a["id"]) for a in apps)
        self.plan_lbl.configure(
            text="已选 %d 个 / %s" % (len(apps), core.human_size(total)))
        self.btn_go.configure(
            text="开始解包（%d 个）" % len(apps) if len(apps) > 1 else "开始解包")

    # ---------------- 列表加载 ----------------

    def _build_source(self):
        if self.mode.get() == "http":
            if not self.url_var.get().strip():
                raise core.UnpackError("请填写 WebDAV 地址。")
            return core.HttpSource(self.url_var.get().strip(),
                                   self.user_var.get().strip(),
                                   self.pw_var.get(),
                                   verify_tls=not self.insecure_var.get())
        d = self.local_var.get().strip()
        if not d:
            raise core.UnpackError("请选择本地目录。")
        return core.LocalSource(d)

    def _load_apps(self):
        try:
            src = self._build_source()
        except core.UnpackError as ex:
            messagebox.showwarning(APP_TITLE, str(ex), parent=self)
            return

        self._btn_state(self.btn_load, False)
        self.src_hint.configure(text="读取中…")
        self._save_state()

        def work():
            try:
                apps = core.list_apps(src)
                self.events.put(("apps", apps))
            except core.UnpackError as ex:
                self.events.put(("loadfail", str(ex)))
            except Exception as ex:
                self.events.put(("loadfail", "%s: %s" % (type(ex).__name__, ex)))

        threading.Thread(target=work, daemon=True).start()

    def _fill_apps(self, apps):
        self.apps = apps
        self.tree.delete(*self.tree.get_children())

        registered = {}
        try:
            for e in playnite.show_windows_paths():
                registered[str(e["app_id"]).lower()] = e["exists"]
        except Exception:
            pass

        for a in apps:
            low = str(a["id"]).lower()
            state = ""
            tag = ()
            if low in registered:
                state = "已安装" if registered[low] else "记录无效"
                tag = ("inst",) if registered[low] else ("bad",)
            self.tree.insert("", "end", values=(
                a["id"], core.folder_for(a), a["name"],
                core.human_size(a["total_bytes"]),
                a["file_count"], core.layout_label(a), state), tags=tag)

        self.src_hint.configure(text="共 %d 个应用" % len(apps))
        self._append_log("[列表] 读取到 %d 个应用" % len(apps))
        if apps:
            first = self.tree.get_children()[0]
            self.tree.selection_set(first)
            self.tree.focus(first)

    # ---------------- 执行 ----------------

    def _start(self):
        if self.worker and self.worker.is_alive():
            return
        apps = self._selected_many()
        if not apps:
            messagebox.showwarning(APP_TITLE, "请先在上面的列表里选一个（或几个）应用。",
                                   parent=self)
            return
        parent = self.out_var.get().strip()
        if not parent:
            messagebox.showwarning(APP_TITLE, "请填写输出父目录。", parent=self)
            return

        try:
            src = self._build_source()
        except core.UnpackError as ex:
            messagebox.showwarning(APP_TITLE, str(ex), parent=self)
            return

        # 每个应用各自解到 <父目录>/<子目录名>/（子目录名优先取原始安装目录名）
        targets = [(a, self._target_for(parent, a)) for a in apps]

        # 已经存在且不为空的子目录先问一声（解包只覆盖清单里出现的同名文件）
        exists = [d for _a, d in targets if os.path.isdir(d) and os.listdir(d)]
        if exists:
            head = "\n".join(exists[:5])
            more = "" if len(exists) <= 5 else "\n…另外 %d 个" % (len(exists) - 5)
            if not messagebox.askyesno(
                    APP_TITLE,
                    "以下目录已存在且不为空：\n%s%s\n\n"
                    "解包会覆盖清单里出现的同名文件（不动其它文件）。\n\n继续吗？"
                    % (head, more), parent=self):
                return

        plugin_dir = self.plugin_var.get().strip()
        do_register = self.register_var.get()
        if do_register:
            real = playnite.looks_like_plugin_data_dir(plugin_dir) if plugin_dir else None
            if not real:
                real = playnite.find_plugin_data_dir()[0]
            if not real:
                messagebox.showwarning(
                    APP_TITLE,
                    "勾了「登记到 Playnite」但定位不到插件数据目录。\n"
                    "请点「自动检测」或手动指定，或者取消勾选。", parent=self)
                return
            plugin_dir = real
            self.plugin_var.set(real)

            running = playnite.is_playnite_running()
            if running is not False:
                # True = 确实在运行；None = 查不出来，同样按「可能开着」处理
                tip = ("Playnite 正在运行。" if running is True
                       else "查不出 Playnite 是否在运行（可能正开着）。")
                if not messagebox.askyesno(
                        APP_TITLE,
                        tip + "\n\n"
                        "登记只是写一个 JSON 文件，一般没问题；但如果你同时在插件里\n"
                        "装/卸应用，两边可能互相覆盖。\n\n"
                        "建议先退出 Playnite。要继续吗？", parent=self):
                    return

        self._save_state()
        self.cancel.clear()
        self._set_running(True)
        self._targets = targets
        n_apps = len(targets)

        self._append_log("")
        self._append_log("=" * 62, "dim")
        if n_apps == 1:
            self._append_log("开始解包：%s → %s"
                             % (core.folder_for(targets[0][0]), targets[0][1]), "hi")
        else:
            self._append_log("开始解包 %d 个应用，输出父目录：%s" % (n_apps, parent), "hi")

        def work():
            grand_files = 0
            grand_bytes = 0
            bad_apps = 0
            done_apps = 0
            try:
                for i, (app, out_dir) in enumerate(targets, 1):
                    if self.cancel.is_set():
                        raise core.Cancelled()
                    self.events.put(("plan", i, n_apps, app["id"], out_dir))

                    # 单个应用时直接透传字节进度；多个应用时把「当前应用内部的
                    # 百分比」折算进整体进度，否则进度条会在每个应用上冲到 100% 再归零。
                    if n_apps == 1:
                        rep = QueueReporter(self.events)
                    else:
                        rep = _BatchReporter(self.events, i - 1, n_apps)

                    result = core.unpack(src, app["id"], out_dir, rep, self.cancel)
                    ok, problems = core.verify_dir(out_dir, result["manifest"], rep)
                    self.events.put(("verified", app["id"], ok, problems))
                    if problems:
                        bad_apps += 1

                    if do_register:
                        entry = playnite.register_install(
                            plugin_dir, app["id"], out_dir,
                            result["manifest"].get("Version"),
                            result["manifest"].get("LaunchExe"))
                        self.events.put(("registered", plugin_dir, entry))

                    grand_files += result["files"]
                    grand_bytes += result["bytes"]
                    done_apps += 1
                    self.events.put(("app_done", i, n_apps, app["id"],
                                     core.folder_for(app),
                                     result["files"], result["bytes"]))

                self.events.put(("done", {
                    "files": grand_files,
                    "bytes": grand_bytes,
                    "count": done_apps,
                    "parent": parent,
                    "out_dir": targets[0][1] if n_apps == 1 else parent,
                    "bad_apps": bad_apps,
                }))
            except core.Cancelled:
                self.events.put(("cancelled", done_apps, n_apps))
            except (core.UnpackError, playnite.PlayniteError) as ex:
                self.events.put(("failed", str(ex)))
            except Exception as ex:
                import traceback
                self.events.put(("failed", "%s: %s\n%s"
                                 % (type(ex).__name__, ex, traceback.format_exc())))

        self.worker = threading.Thread(target=work, daemon=True)
        self.worker.start()

    def _cancel(self):
        if self.worker and self.worker.is_alive():
            self.cancel.set()
            self._append_log("已请求取消，等待当前数据块结束…", "warn")
            self._btn_state(self.btn_cancel, False)

    def _open_out(self):
        d = self.out_var.get().strip()
        if d and os.path.isdir(d):
            try:
                os.startfile(d)
            except Exception:
                pass

    def _sync_open_btn(self):
        d = self.out_var.get().strip()
        ok = (not self._is_running) and bool(d) and os.path.isdir(d)
        self._btn_state(self.btn_open, ok)

    def _set_running(self, running):
        self._is_running = bool(running)
        self._btn_state(self.btn_go, not running)
        self._btn_state(self.btn_load, not running)
        self._btn_state(self.btn_cancel, running)
        for key, b in self.seg_btns.items():
            self._btn_state(b, not running)
        self._sync_open_btn()
        self._set_status("run" if running else "idle")

    # ---------------- 事件泵 ----------------

    def _pump(self):
        try:
            while True:
                ev = self.events.get_nowait()
                self._handle(ev)
        except queue.Empty:
            pass
        self.after(POLL_MS, self._pump)

    def _handle(self, ev):
        kind = ev[0]

        if kind == "log":
            self._append_log(ev[1])

        elif kind == "stage":
            self.stage_lbl.configure(text=ev[1])

        elif kind == "progress":
            done, total, label = ev[1], ev[2], ev[3]
            if total:
                self._ratio = max(0.0, min(1.0, float(done) / total))
                self._draw_bar()
                self.pct.configure(text="%.1f%%" % (self._ratio * 100))
            if label:
                self.stage_lbl.configure(text=label[:48])

        elif kind == "apps":
            self._btn_state(self.btn_load, True)
            self._fill_apps(ev[1])

        elif kind == "loadfail":
            self._btn_state(self.btn_load, True)
            self.src_hint.configure(text="读取失败")
            self._append_log("[失败] " + ev[1], "err")
            messagebox.showerror(APP_TITLE, ev[1], parent=self)

        elif kind == "plan":
            i, n, app_id, out_dir = ev[1], ev[2], ev[3], ev[4]
            if n > 1:
                self._append_log("")
                self._append_log("[应用 %d/%d] %s" % (i, n, app_id), "info")
            self.plan_lbl.configure(text="应用 %d/%d" % (i, n))

        elif kind == "app_done":
            i, n, app_id, folder, files, nbytes = (ev[1], ev[2], ev[3], ev[4],
                                                   ev[5], ev[6])
            # 子目录名和 id 不一致时把 id 也带上，免得对不上是哪个应用
            label = folder if folder == app_id else "%s（%s）" % (folder, app_id)
            self._append_log("           ✓ %s：%d 个文件 / %s"
                             % (label, files, core.human_size(nbytes)), "ok")

        elif kind == "verified":
            app_id, ok, problems = ev[1], ev[2], ev[3]
            if problems:
                self._append_log("[自检] %s 有 %d 处问题：" % (app_id, len(problems)))
                for p in problems[:10]:
                    self._append_log("    " + p)
            else:
                self._append_log("[自检] %s 通过：%d 个文件大小一致" % (app_id, ok))

        elif kind == "registered":
            plugin_dir, entry = ev[1], ev[2]
            self._append_log("[Playnite] 已登记：%s  →  %s"
                             % (entry["AppId"], entry["InstallDir"]))
            self._append_log("           记录文件 "
                             + os.path.join(plugin_dir, "local-index.json"))
            if entry.get("_replaced"):
                self._append_log("           （覆盖了原有记录，旧文件已备份为 .bak）")

        elif kind == "done":
            result = ev[1]
            self._set_running(False)
            self._ratio = 1.0
            self._draw_bar()
            self.pct.configure(text="100.0%")
            self.stage_lbl.configure(text="完成")
            self.plan_lbl.configure(text="")
            self._set_status("ok")
            self._append_log("")
            self._append_log("完成：%d 个应用 / %d 个文件 / %s"
                             % (result["count"], result["files"],
                                core.human_size(result["bytes"])), "ok")
            if result.get("bad_apps"):
                self._append_log("[自检] 有 %d 个应用存在问题，见上面的明细。"
                                 % result["bad_apps"], "warn")
            self._refresh_states()
            self._update_target_preview()
            self._sync_open_btn()

            if result["count"] == 1:
                head = ("解包完成。\n\n位置：%s\n文件：%d 个 / %s"
                        % (result["out_dir"], result["files"],
                           core.human_size(result["bytes"])))
            else:
                head = ("解包完成 %d 个应用。\n\n父目录：%s\n合计：%d 个文件 / %s"
                        % (result["count"], result["parent"], result["files"],
                           core.human_size(result["bytes"])))
            if self.register_var.get() and self.plugin_var.get().strip():
                head += ("\n\n已登记到 Playnite。到 Playnite 里点一次\n"
                         "右键游戏 → Vault → 从 NAS 刷新库条目，\n"
                         "这些条目就会变成「已安装」。")
            messagebox.showinfo(APP_TITLE, head, parent=self)

        elif kind == "cancelled":
            done_apps, total_apps = ev[1], ev[2]
            self._set_running(False)
            self._set_status("stop")
            self.stage_lbl.configure(text="已取消")
            self.plan_lbl.configure(text="")
            self._append_log("已取消（已完成 %d/%d 个应用；已解出的文件保留在输出目录，"
                             "可重跑续上）。" % (done_apps, total_apps), "warn")

        elif kind == "failed":
            self._set_running(False)
            self._set_status("fail")
            self.stage_lbl.configure(text="失败")
            self.plan_lbl.configure(text="")
            self._append_log("")
            self._append_log("[失败] " + ev[1], "err")
            messagebox.showerror(APP_TITLE, ev[1], parent=self)

    def _refresh_states(self):
        if not self.apps:
            return
        registered = {}
        try:
            for e in playnite.show_windows_paths():
                registered[str(e["app_id"]).lower()] = e["exists"]
        except Exception:
            pass
        for item in self.tree.get_children():
            vals = list(self.tree.item(item, "values"))
            low = str(vals[0]).lower()
            tag = ()
            if low in registered:
                # 状态列永远是最后一列（前面加了「子目录名」也用末尾下标取，
                # 免得下次再加列又忘了跟着改）
                vals[-1] = "已安装" if registered[low] else "记录无效"
                tag = ("inst",) if registered[low] else ("bad",)
            self.tree.item(item, values=vals, tags=tag)

    # ---------------- 进度条 / 日志 ----------------

    def _draw_bar(self):
        c = self.bar
        c.delete("all")
        w = c.winfo_width()
        h = c.winfo_height()
        if w <= 2 or h <= 2:
            return
        lw = self.px(1)
        c.create_rectangle(lw / 2.0, lw / 2.0, w - lw / 2.0, h - lw / 2.0,
                           outline=INK, width=lw, fill=TRACK)
        inner = w - 2.0 * lw
        fw = inner * self._ratio
        if fw > 0.5:
            c.create_rectangle(lw, lw, lw + fw, h - lw,
                               fill=(GREEN if self._ratio >= 1.0 else BLUE),
                               outline="")

    def _append_log(self, text, tag=None):
        if tag is None:
            tag = _auto_tag(text)
        self.log.configure(state="normal")
        self.log.insert("end", text + "\n", tag or ())
        self.log.see("end")
        self.log.configure(state="disabled")

    def _on_close(self):
        if self.worker and self.worker.is_alive():
            if not messagebox.askyesno(
                    APP_TITLE,
                    "正在解包。现在关闭会中止任务（已经解出来的文件会保留）。\n\n"
                    "确定关闭吗？", parent=self):
                return
            self.cancel.set()
        self.destroy()


class PluginInstaller(tk.Toplevel):
    """「安装插件到 Playnite」窗口。

    单独开窗口、不动主界面布局。流程固定为四步，每一步都记进窗口自带的日志：

        定位 Playnite → 拿插件包（内置 / 最新 release）
        → 优雅关闭 Playnite（发 WM_CLOSE，等同点 ×，不强杀）
        → 写进 Extensions\\Playnite-Vault → 重新拉起原来那个 Playnite

    为什么必须「先关再写」：PlayniteVault.dll 被 Playnite 进程锁着，运行期覆盖不了。
    为什么不强杀：强杀会让 Playnite 丢掉还没落盘的库改动。
    """

    def __init__(self, app):
        tk.Toplevel.__init__(self, app)
        self.app = app
        self.title("安装插件到 Playnite")
        self.configure(bg=BG)
        self.transient(app)
        try:
            self.iconphoto(False, app.iconphoto(False))  # 有则设，没有就算了
        except Exception:
            pass

        self.events = queue.Queue()
        self.answer = None
        self.answer_ready = threading.Event()
        self.worker = None
        self.cancel = threading.Event()

        self.plugin_root = ""
        self.source = tk.StringVar(value="bundled")
        self.restart_var = tk.BooleanVar(value=True)
        self.move_legacy_var = tk.BooleanVar(value=True)

        self._build()
        self._autodetect()
        self.after(POLL_MS, self._pump)
        self.protocol("WM_DELETE_WINDOW", self._on_close)

    # ---------------- 布局 ----------------

    def _build(self):
        a = self.app
        head = tk.Frame(self, bg=INK)
        head.pack(fill="x")
        tk.Label(head, text="安装插件到 Playnite", bg=INK, fg=WHITE,
                 font=(a.ui_font, 12, "bold")).pack(
            anchor="w", padx=a.px(14), pady=(a.px(10), 0))
        tk.Label(head, text="把 Playnite-Vault 插件写进 Playnite 的 Extensions 目录，"
                            "然后优雅重启它",
                 bg=INK, fg="#B9C2D6", font=(a.ui_font, 9)).pack(
            anchor="w", padx=a.px(14), pady=(a.px(2), a.px(10)))
        tk.Frame(self, bg=YELLOW, height=a.px(4)).pack(fill="x")

        body = tk.Frame(self, bg=BG)
        body.pack(fill="both", expand=True, padx=a.px(14),
                  pady=(a.px(10), a.px(8)))

        # ---- 1. Playnite 目录 ----
        card = tk.Frame(body, bg=WHITE, bd=0, highlightthickness=a.px(1),
                        highlightbackground=LINE, highlightcolor=LINE)
        card.pack(fill="x")
        inner = tk.Frame(card, bg=WHITE)
        inner.pack(fill="x", padx=a.px(12), pady=a.px(10))

        r = tk.Frame(inner, bg=WHITE)
        r.pack(fill="x")
        tk.Label(r, text="Playnite 目录", bg=WHITE, fg=INK, anchor="w",
                 width=12, font=(a.ui_font, 10)).pack(side="left")
        self.root_var = tk.StringVar()
        self.e_root = tk.Entry(r, textvariable=self.root_var, bd=0,
                               relief="flat", font=(a.mono_font, 9),
                               bg="#F7F8FB", fg=INK,
                               highlightthickness=a.px(1),
                               highlightbackground=LINE_SOFT,
                               highlightcolor=INK)
        self.e_root.pack(side="left", fill="x", expand=True, ipady=a.px(4))
        b = a._btn(r, "浏览…", self._pick_root, kind="ghost", size=9,
                   padx=10, pady=4)
        b.pack(side="left", padx=(a.px(6), 0))
        b2 = a._btn(r, "自动检测", self._autodetect, kind="ghost", size=9,
                    padx=10, pady=4)
        b2.pack(side="left", padx=(a.px(6), 0))

        self.state_lbl = tk.Label(inner, text="", bg=WHITE, fg=MUTED, anchor="w",
                                  justify="left", font=(a.ui_font, 9),
                                  wraplength=a.px(560))
        self.state_lbl.pack(fill="x", pady=(a.px(8), 0))

        # ---- 2. 插件包来源 ----
        card2 = tk.Frame(body, bg=WHITE, bd=0, highlightthickness=a.px(1),
                         highlightbackground=LINE, highlightcolor=LINE)
        card2.pack(fill="x", pady=(a.px(8), 0))
        inner2 = tk.Frame(card2, bg=WHITE)
        inner2.pack(fill="x", padx=a.px(12), pady=a.px(10))

        tk.Label(inner2, text="插件包", bg=WHITE, fg=INK, anchor="w",
                 font=(a.ui_font, 10, "bold")).pack(anchor="w")
        seg = tk.Frame(inner2, bg=WHITE)
        seg.pack(fill="x", pady=(a.px(6), 0))
        self.seg_btns = {}
        for key, label in (("bundled", "用内置的（最快）"),
                           ("github", "GitHub 最新"),
                           ("gitee", "Gitee 最新")):
            b = a._btn(seg, label, lambda k=key: self._set_source(k),
                       kind="ghost", size=9, padx=14, pady=5)
            b.pack(side="left", padx=(0, a.px(6)))
            self.seg_btns[key] = b

        tk.Label(inner2, text="内置的那份和解包器一起发布，不用联网；"
                              "「最新」会去 release 里取 PlayniteVault-<版本>.zip。",
                 bg=WHITE, fg=MUTED, anchor="w", justify="left",
                 font=(a.ui_font, 9),
                 wraplength=a.px(560)).pack(fill="x", pady=(a.px(8), 0))

        # ---- 3. 选项 + 执行 ----
        card3 = tk.Frame(body, bg=WHITE, bd=0, highlightthickness=a.px(1),
                         highlightbackground=LINE, highlightcolor=LINE)
        card3.pack(fill="x", pady=(a.px(8), 0))
        inner3 = tk.Frame(card3, bg=WHITE)
        inner3.pack(fill="x", padx=a.px(12), pady=a.px(10))

        self.chk_restart = NeoCheck(
            inner3, "注入完成后自动重启 Playnite（用原来那个桌面版/全屏版）",
            self.restart_var, scale=a.s, ui_font=a.ui_font)
        self.chk_restart.pack(anchor="w")
        self.chk_legacy = NeoCheck(
            inner3, "把改名前的旧目录 VaultDemo_* 挪到备份（否则会同时加载两份插件）",
            self.move_legacy_var, scale=a.s, ui_font=a.ui_font)
        self.chk_legacy.pack(anchor="w", pady=(a.px(6), 0))

        run = tk.Frame(inner3, bg=WHITE)
        run.pack(fill="x", pady=(a.px(10), 0))
        self.btn_go = a._btn(run, "注入并重启", self._start,
                             kind="pri", size=10, padx=20, pady=7)
        self.btn_go.pack(side="left")
        self.btn_close = a._btn(run, "关闭", self._on_close,
                                kind="ghost", size=10, padx=16, pady=7)
        self.btn_close.pack(side="right")

        # ---- 4. 日志 ----
        box = tk.Frame(body, bg=WHITE, bd=0, highlightthickness=a.px(1),
                       highlightbackground=INK, highlightcolor=INK)
        box.pack(fill="both", expand=True, pady=(a.px(8), 0))
        self.log = tk.Text(box, height=12, wrap="word", bd=0, relief="flat",
                           font=(a.mono_font, 9), bg=LOG_BG, fg=LOG_FG,
                           insertbackground=WHITE, highlightthickness=0,
                           selectbackground=BLUE, selectforeground=INK,
                           padx=a.px(8), pady=a.px(6))
        self.log.pack(side="left", fill="both", expand=True)
        lsb = ttk.Scrollbar(box, orient="vertical",
                            style="Log.Vertical.TScrollbar", command=self.log.yview)
        lsb.pack(side="right", fill="y")
        self.log.configure(yscrollcommand=lsb.set, state="disabled")
        self.log.tag_configure("err", foreground="#FF8296")
        self.log.tag_configure("ok", foreground="#7BE3A8")
        self.log.tag_configure("warn", foreground=YELLOW)
        self.log.tag_configure("dim", foreground="#6E7688")

        self._set_source("bundled")
        self.update_idletasks()
        w = max(self.winfo_reqwidth(), a.px(640))
        h = max(self.winfo_reqheight(), a.px(560))
        self.geometry("%dx%d" % (w, h))

    # ---------------- 交互 ----------------

    def _set_source(self, key):
        """三段式选择：选中的那个刷成强调色。

        走 _btn_state 而不是直接 configure(bg=...)：_btn 的悬停处理会按按钮自己
        记着的 _k 把颜色刷回去，绕过它改色会在鼠标一进一出之后被打回原形。
        """
        self.source.set(key)
        for k, btn in self.seg_btns.items():
            on = (k == key)
            btn._k = ((YELLOW, INK, "#FFDF6B", "#EFE6CC") if on
                      else (WHITE, INK, "#EEF1F6", "#E8EBF1"))
            self.app._btn_state(btn, True)

    def _pick_root(self):
        d = filedialog.askdirectory(
            title="选择 Playnite 安装目录（有 Playnite.DesktopApp.exe 的那个）",
            parent=self)
        if d:
            self.root_var.set(d)
            self._refresh_state()

    def _autodetect(self):
        """开机自动检测：配置里存过的优先，没有就扫一遍。"""
        cfg = self.app.cfg or {}
        saved = (cfg.get("playnite_dir") or "").strip()
        root, note = inject.pick_root(saved or None)
        if root:
            self.root_var.set(root)
            self._log("[定位] %s" % note)
        else:
            self._log("[定位] " + note.replace("\n", " "))
        self._refresh_state()

    def _refresh_state(self):
        root = self.root_var.get().strip()
        if not root or not os.path.isdir(root):
            self.state_lbl.configure(text="⚠ 目录不存在。", fg=PINK)
            self.plugin_root = ""
            return
        if not inject.looks_like_playnite_root(root):
            self.state_lbl.configure(
                text="⚠ 这个目录里没看到 Playnite.DesktopApp.exe / Extensions，"
                     "请确认选的是 Playnite 安装目录。", fg=PINK)
            self.plugin_root = root
            return

        self.plugin_root = root
        exts = inject.extensions_dir_of(root)
        info = inject.installed_info(exts)
        legacy = inject.legacy_plugin_dirs(exts)
        running = playnite.is_playnite_running()

        bits = []
        if info:
            bits.append("已装 %s：版本 %s%s"
                        % (inject.PLUGIN_ID, info.get("version") or "?",
                           "" if info.get("has_dll") else "（**缺 dll**，建议重装）"))
        else:
            bits.append("尚未安装 %s" % inject.PLUGIN_ID)
        if legacy:
            bits.append("发现改名前的旧目录 %d 个" % len(legacy))
        if running is True:
            bits.append("Playnite 正在运行（注入前会自动请它退出）")
        elif running is None:
            bits.append("查不出 Playnite 是否在运行")
        self.state_lbl.configure(text="  ·  ".join(bits), fg=MUTED)

    def _start(self):
        if self.worker and self.worker.is_alive():
            return
        root = self.root_var.get().strip()
        if not root or not os.path.isdir(root):
            messagebox.showerror(APP_TITLE, "Playnite 目录不对，没法注入。", parent=self)
            return
        self.cancel.clear()
        self._set_running(True)
        self.worker = threading.Thread(target=self._run, args=(root,), daemon=True)
        self.worker.start()

    def _set_running(self, running):
        self.btn_go.configure(state="disabled" if running else "normal",
                              text="正在处理…" if running else "注入并重启")
        self.app._btn_state(self.btn_close, not running)
        self.chk_restart.set_enabled(not running)
        self.chk_legacy.set_enabled(not running)

    # ---------------- 干活（后台线程）----------------

    def _run(self, root):
        try:
            self._work(root)
        except inject.InjectError as ex:
            self.events.put(("fail", str(ex)))
        except Exception as ex:
            self.events.put(("fail", "%s: %s" % (type(ex).__name__, ex)))
        finally:
            self.events.put(("done", None))

    def _work(self, root):
        log = lambda m: self.events.put(("log", m))  # noqa: E731

        exts = inject.extensions_dir_of(root)
        log("[目标] %s" % exts)

        # 1) 拿插件包
        source = self.source.get()
        mirror = source if source in ("github", "gitee") else "auto"
        if source == "bundled":
            log("[插件包] 使用内置的那份")
        else:
            log("[插件包] 去 %s 取最新 release" % source)
        download_dir = os.path.join(playnite.state_dir(), "plugin-download")
        zip_path, note = inject.fetch_package(download_dir, source, mirror, log)
        log("[插件包] %s → %s" % (note, zip_path))

        # 2) 要不要关、关完重启成什么
        procs = playnite.playnite_processes()
        was_running = bool(procs)
        target_exe = None
        for p in procs or []:
            if p.get("exe"):
                target_exe = p["exe"]
                break
        if was_running:
            log("[状态] 注入前 Playnite 在运行：%s"
                % ", ".join(sorted({p["name"] for p in procs})))
        else:
            log("[状态] 注入前 Playnite 没在运行")

        if was_running:
            state, _ = inject.request_close(log)
            if state == "timeout":
                # 为什么还在，只有用户知道（关闭到托盘？卡住了？）——
                # 决定权交回去，绝不擅自强杀。
                if self._ask(
                        "Playnite 没有退出。\n\n"
                        "常见原因：设置里开了「关闭到托盘」，点 × 只是收进托盘。\n\n"
                        "· 选「是」：我再试一次（请先去托盘图标上点真正的退出）\n"
                        "· 选「否」：放弃这次注入\n\n"
                        "（本工具不会强杀 Playnite —— 强杀会让它丢掉还没落盘的库改动）"):
                    state2, _ = inject.request_close(log, timeout=90)
                    if state2 != "closed":
                        raise inject.InjectError(
                            "Playnite 仍然没有退出。请手动退出 Playnite 后重试。")
                else:
                    raise inject.InjectError("已取消：Playnite 还在运行，没法覆盖插件文件。")
            elif state == "unknown":
                raise inject.InjectError(
                    "查不出 Playnite 是否在运行，为安全起见不注入。\n"
                    "请先手动确认 Playnite 已退出。")

        # 3) 注入
        staging = os.path.join(playnite.state_dir(), "plugin-staging")
        res = inject.install_package(zip_path, exts, staging, log,
                                     move_legacy=self.move_legacy_var.get())
        log("[完成] 插件已装到 %s（版本 %s）" % (res["dir"], res["version"]))
        if res["backup"]:
            log("[完成] 旧版本备份在 %s" % res["backup"])

        # 4) 重启
        if was_running and self.restart_var.get():
            inject.launch(target_exe, log)
        elif was_running:
            log("[重启] 按你的选择没有重启，Playnite 现在没在跑。")
        else:
            log("[重启] 注入前它本来就没在跑，所以没有替你启动。")
        self.events.put(("ok", res))

    # ---------------- 主线程侧 ----------------

    def _ask(self, text):
        """让后台线程等到用户在界面上做出选择。"""
        self.answer = None
        self.answer_ready.clear()
        self.events.put(("ask", text))
        self.answer_ready.wait(timeout=300)
        return bool(self.answer)

    def _pump(self):
        try:
            while True:
                ev = self.events.get_nowait()
                kind = ev[0]
                if kind == "log":
                    self._log(ev[1])
                elif kind == "ask":
                    self.answer = messagebox.askyesno(APP_TITLE, ev[1], parent=self)
                    self.answer_ready.set()
                elif kind == "fail":
                    self._log("[失败] " + ev[1], "err")
                    messagebox.showerror(APP_TITLE, ev[1], parent=self)
                elif kind == "ok":
                    self._log("[完成] 注入成功。", "ok")
                    self._remember()
                    messagebox.showinfo(
                        APP_TITLE,
                        "插件已装好。\n\n"
                        "首次使用请到 Playnite 的 设置 → 扩展 → Playnite Vault "
                        "里填 WebDAV 地址。", parent=self)
                elif kind == "done":
                    self._set_running(False)
                    self._refresh_state()
                    self.app._autodetect_playnite(False)
        except queue.Empty:
            pass
        if self.winfo_exists():
            self.after(POLL_MS, self._pump)

    def _remember(self):
        """把这次用的 Playnite 目录记到配置里，下次开窗直接填好。"""
        try:
            cfg = dict(self.app.cfg or {})
            cfg["playnite_dir"] = self.root_var.get().strip()
            self.app.cfg = cfg
            config.save(cfg)
            self._log("[配置] 已记住 Playnite 目录", "dim")
        except Exception as ex:
            self._log("[配置] 记住目录失败：%s" % ex, "warn")

    def _log(self, text, tag=None):
        if tag is None:
            tag = _auto_tag(text)
        self.log.configure(state="normal")
        self.log.insert("end", text + "\n", tag or ())
        self.log.see("end")
        self.log.configure(state="disabled")

    def _on_close(self):
        if self.worker and self.worker.is_alive():
            if not messagebox.askyesno(
                    APP_TITLE,
                    "正在注入插件。中途关窗不会回滚（已经写进去的文件会留着）。\n\n"
                    "确定关闭吗？", parent=self):
                return
        self.destroy()


def _auto_tag(text):
    """按日志前缀挑个颜色，纯观感用途。"""
    head = text.lstrip()
    if head.startswith("=") or not head:
        return "dim"
    if head.startswith("["):
        end = head.find("]")
        if end > 0:
            word = head[1:end]
            if word in ("失败", "错误"):
                return "err"
            if word in ("完成", "自检"):
                return "ok" if word == "完成" else "info"
            if word in ("列表", "Playnite", "仓库", "构建"):
                return "info"
    if ("失败" in head) or ("错误" in head) or ("异常" in head):
        return "err"
    if "完成" in head:
        return "ok"
    return None


def main():
    _enable_dpi_awareness()
    app = App()
    app.mainloop()
    return 0
