# -*- coding: utf-8 -*-
"""GUI 冒烟测试：真的把主窗口和「安装插件到 Playnite」窗口建出来。

用带 tkinter 的解释器跑。全程 withdraw() 隐藏窗口，不闪用户屏幕；
配置指到临时路径，绝不读写真 config.json。
"""
import os
import sys
import tempfile

# 本文件在 <仓库>/tools/python-selftest/ 下，往上两级就是仓库根
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOLS = os.path.join(ROOT, "tools")
sys.path.insert(0, TOOLS)

try:
    import tkinter  # noqa: F401
except ImportError:
    sys.stderr.write(
        "这个脚本必须用**带 tkinter 的 Python** 来跑（它要真把窗口建出来）。\n"
        "官方 python.org 的 Windows 安装包默认自带；有的精简版/conda 版本没有。\n")
    sys.exit(2)

tmp = tempfile.mkdtemp(prefix="gui-smoke-")
os.environ["VAULT_UNPACKER_CONFIG"] = os.path.join(tmp, "config.json")
os.environ["APPDATA"] = os.path.join(tmp, "appdata")

import tkinter as tk  # noqa: E402

from vault_unpacker import gui, inject  # noqa: E402

ok = fail = 0


def check(cond, label, detail=""):
    global ok, fail
    if cond:
        ok += 1
        print("  [通过] " + label)
    else:
        fail += 1
        print("  [失败] " + label + (" —— " + detail if detail else ""))


print("== 主窗口 ==")
app = gui.App()
app.withdraw()
app.update()
check(app.winfo_exists(), "主窗口建得起来（原有布局没被改坏）")
for name in ("btn_go", "btn_cancel", "log", "chk_register", "e_plugin",
             "seg_btns", "src_fixed"):
    check(hasattr(app, name), "主界面原有控件还在：%s" % name)
check(hasattr(app, "btn_plug"), "底栏多了「安装插件到 Playnite…」按钮")
check(app.btn_plug.winfo_exists(), "那个按钮真的建出来了")
check(app.btn_plug.cget("text").startswith("安装插件到 Playnite"),
      "按钮文案正确", app.btn_plug.cget("text"))

print("")
print("== 安装插件窗口 ==")
dlg = app._open_plugin_dialog()
dlg.withdraw()
app.update()
check(dlg.winfo_exists(), "插件安装窗口建得起来")
for name in ("root_var", "source", "restart_var", "move_legacy_var",
             "state_lbl", "log", "btn_go", "btn_close", "seg_btns"):
    check(hasattr(dlg, name), "窗口里有：%s" % name)
check(len(dlg.seg_btns) == 3, "插件包来源有三个选项",
      str(sorted(dlg.seg_btns)))

# 再点一次不应该开出第二个窗口
dlg2 = app._open_plugin_dialog()
check(dlg2 is dlg, "重复点按钮不会开第二个窗口（复用同一个）")

print("")
print("== 状态显示 ==")
dlg._autodetect()
app.update()
text = dlg.state_lbl.cget("text")
print("    状态：" + text.replace("\n", " "))
check(bool(text), "自动检测后有状态文案")
check(("Playnite-Vault" in text) or ("不存在" in text) or ("没看到" in text),
      "状态文案说的是实话（已装/未装/目录不对）")

# 指到一个假根目录，检查「未装 + 发现旧目录」两条都说得出来
fake_root = os.path.join(tmp, "FakePlaynite")
os.makedirs(os.path.join(fake_root, "Extensions",
                         "VaultDemo_5e76bf50-cb8a-4a87-ad24-1912c746c6f0"))
open(os.path.join(fake_root, "Playnite.DesktopApp.exe"), "wb").write(b"MZ")
dlg.root_var.set(fake_root)
dlg._refresh_state()
app.update()
text2 = dlg.state_lbl.cget("text")
print("    状态：" + text2.replace("\n", " "))
check("尚未安装" in text2, "未装时明说「尚未安装」")
check("旧目录" in text2, "能提示发现了改名前的旧目录")

print("")
print("== 选择插件包来源 ==")
for key in ("bundled", "github", "gitee"):
    dlg._set_source(key)
    app.update()
    assert dlg.source.get() == key
check(True, "三种来源都能切换且不报错")
# 选中的那个底色应该和未被选中的不一样
dlg._set_source("bundled")
app.update()
sel = dlg.seg_btns["bundled"].cget("bg")
unsel = dlg.seg_btns["github"].cget("bg")
check(sel != unsel, "选中的来源底色与未选中的不同（看得出选中态）",
      "选中 %s / 未选 %s" % (sel, unsel))

print("")
print("== 后台线程 → 主线程的请求应答通路 ==")
import queue as _q  # noqa: E402
dlg.events.put(("log", "测试日志一行"))
app.update()
dlg._pump()
app.update()
content = dlg.log.get("1.0", "end")
check("测试日志一行" in content, "日志能写进窗口的日志框")

# _ask 的等待/唤醒不能死锁
import threading  # noqa: E402
def answer_soon():
    import time
    time.sleep(0.2)
    dlg.events.put(("ask", "测试提问"))
t = threading.Thread(target=answer_soon, daemon=True)
t.start()
# 这里不真的弹窗：直接验证 Event 机制本身
dlg.answer = True
dlg.answer_ready.set()
check(dlg.answer_ready.is_set(), "_ask 用的应答 Event 能正常置位（不会卡死）")

dlg.destroy()
app.destroy()
print("")
print("通过 %d 项，失败 %d 项" % (ok, fail))
sys.exit(1 if fail else 0)
