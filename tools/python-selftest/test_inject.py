# -*- coding: utf-8 -*-
"""inject.py 的冒烟测试：全部在临时目录里跑，不碰真实 Playnite。"""
import io
import json
import os
import shutil
import sys
import tempfile
import zipfile

# 本文件在 <仓库>/tools/python-selftest/ 下，往上两级就是仓库根
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOLS = os.path.join(ROOT, "tools")
sys.path.insert(0, TOOLS)

from vault_unpacker import inject, playnite  # noqa: E402

ok = fail = 0


def check(cond, label, detail=""):
    global ok, fail
    if cond:
        ok += 1
        print("  [通过] " + label)
    else:
        fail += 1
        print("  [失败] " + label + (" —— " + detail if detail else ""))


print("== 定位 Playnite ==")
roots = inject.find_playnite_roots()
print("  候选：" + ", ".join("%s(%s)" % (r["root"], r["source"]) for r in roots))
root, note = inject.pick_root()
print("  选中：" + str(root) + "  ← " + note)
check(bool(root), "能定位到 Playnite 根目录")
if root:
    exts = inject.extensions_dir_of(root)
    print("  Extensions：" + exts)
    check(inject.looks_like_playnite_root(root), "根目录判定通过")
    info = inject.installed_info(exts)
    print("  已装版本：" + json.dumps(info, ensure_ascii=False))
    check(info is None or info.get("id") is not None, "能读取已装插件信息（或确认未装）")
    legacy = inject.legacy_plugin_dirs(exts)
    print("  改名前的旧目录：" + json.dumps([os.path.basename(p) for p in legacy],
                                            ensure_ascii=False))
    check(isinstance(legacy, list), "能列出改名前的旧目录")

print("")
print("== 进程名（本次修复的点）==")
procs = playnite.playnite_processes()
print("  当前 Playnite 进程：" + json.dumps(procs, ensure_ascii=False))
check(isinstance(procs, list), "playnite_processes() 返回列表（不是 None）")
check(playnite.is_playnite_running() in (True, False),
      "is_playnite_running() 给的是明确答案，不是「查不出来」",
      repr(playnite.is_playnite_running()))
check("Playnite.DesktopApp.exe" in playnite.PLAYNITE_EXE_NAMES,
      "进程名清单里包含桌面版（旧代码只认 Playnite.exe，必错）")

print("")
print("== 注入（临时目录，真解包真拷贝）==")
tmp = tempfile.mkdtemp(prefix="inject-test-")
fake_root = os.path.join(tmp, "Playnite")
exts = os.path.join(fake_root, "Extensions")
os.makedirs(exts)
# 造一个改名前的旧目录
legacy_dir = os.path.join(exts, "VaultDemo_5e76bf50-cb8a-4a87-ad24-1912c746c6f0")
os.makedirs(legacy_dir)
with open(os.path.join(legacy_dir, "extension.yaml"), "w", encoding="utf-8") as f:
    f.write("Id: VaultDemo_old\nVersion: 1.4.0\nModule: VaultDemo.dll\n")


def make_zip(path, version="1.6.0", pid="Playnite-Vault", dll="PlayniteVault.dll"):
    with zipfile.ZipFile(path, "w") as z:
        z.writestr(pid + "/extension.yaml",
                   "Id: %s\nName: Playnite Vault\nVersion: %s\nModule: %s\n"
                   "Type: GameLibrary\n" % (pid, version, dll))
        z.writestr(pid + "/" + dll, b"MZ" + b"\x00" * 200)
        z.writestr(pid + "/icon.png", b"\x89PNG\r\n\x1a\n" + b"\x00" * 16)
        z.writestr(pid + "/使用说明.txt", "中文文件名也要能解出来\n".encode("utf-8"))
    return path


good = make_zip(os.path.join(tmp, "good.zip"))
staging = os.path.join(tmp, "staging")
# 把备份根改到临时目录，别污染真实 %APPDATA%
playnite._STATE_DIR = os.path.join(tmp, "state")

res = inject.install_package(good, exts, staging, log=lambda m: print("    " + m))
check(os.path.isfile(os.path.join(exts, inject.PLUGIN_ID, "PlayniteVault.dll")),
      "dll 落到 Extensions\\Playnite-Vault 下")
check(os.path.isfile(os.path.join(exts, inject.PLUGIN_ID, "使用说明.txt")),
      "中文名文件也解出来了")
check(res["version"] == "1.6.0", "读出了包内版本", str(res["version"]))
check(res["legacy_moved"] == ["VaultDemo_5e76bf50-cb8a-4a87-ad24-1912c746c6f0"],
      "改名前的旧目录被挪走（否则 Playnite 会同时加载两份）",
      json.dumps(res["legacy_moved"], ensure_ascii=False))
check(not os.path.isdir(legacy_dir), "旧目录确实不在 Extensions 里了")
check(res["backup"] and os.path.isdir(res["backup"]), "备份落在解包器自己的目录，不在 Extensions 里")
check(res["backup"] and "Extensions" not in res["backup"],
      "备份路径里没有 Extensions 目录")

# 再装一次：这次要把已装的 1.6.0 备份走
res2 = inject.install_package(good, exts, staging, log=None)
check(res2["version"] == "1.6.0", "重复注入也能成功（旧版本自动备份）")
check(res2["backup"] and os.path.isdir(res2["backup"]), "第二次也给旧版本留了备份")
check(inject.installed_info(exts)["version"] == "1.6.0", "装完能读回版本号")

print("")
print("== 拒绝坏包 ==")


def expect_reject(zip_path, label):
    try:
        inject.install_package(zip_path, exts, staging, log=None)
        check(False, label, "居然通过了")
    except inject.InjectError as ex:
        check(True, label + "（%s）" % str(ex)[:44])


# Id 不对（就是改名前的那个包）
expect_reject(make_zip(os.path.join(tmp, "bad-id.zip"),
                       pid="VaultDemo_5e76bf50-cb8a-4a87-ad24-1912c746c6f0",
                       dll="VaultDemo.dll"), "Id 不对的包必须被拒")

# 缺 dll：只有 yaml + icon
nodll = os.path.join(tmp, "bad-nodll.zip")
with zipfile.ZipFile(nodll, "w") as z:
    z.writestr("Playnite-Vault/extension.yaml",
               "Id: Playnite-Vault\nVersion: 1.0.0\nModule: PlayniteVault.dll\n")
    z.writestr("Playnite-Vault/icon.png", b"\x89PNG\r\n\x1a\n")
expect_reject(nodll, "缺 dll 的包必须被拒")

# dll 不是 PE
badpe = os.path.join(tmp, "bad-pe.zip")
with zipfile.ZipFile(badpe, "w") as z:
    z.writestr("Playnite-Vault/extension.yaml",
               "Id: Playnite-Vault\nVersion: 1.0.0\n")
    z.writestr("Playnite-Vault/PlayniteVault.dll", b"NOTPE")
expect_reject(badpe, "dll 不是 PE 文件必须被拒")

# 缺 extension.yaml
noyaml = os.path.join(tmp, "bad-noyaml.zip")
with zipfile.ZipFile(noyaml, "w") as z:
    z.writestr("Playnite-Vault/PlayniteVault.dll", b"MZ" + b"\x00" * 10)
expect_reject(noyaml, "缺 extension.yaml 的包必须被拒")

print("")
print("== 路径穿越防护 ==")
evil = os.path.join(tmp, "evil.zip")
with zipfile.ZipFile(evil, "w") as z:
    z.writestr("Playnite-Vault/extension.yaml", "Id: Playnite-Vault\nVersion: 1.0.0\n")
    z.writestr("Playnite-Vault/PlayniteVault.dll", b"MZ" + b"\x00" * 10)
    z.writestr("Playnite-Vault/../../evil.txt", b"pwned")
try:
    inject.install_package(evil, exts, staging, log=None)
    check(False, "带 ../ 的包必须被拒")
except inject.InjectError:
    check(True, "带 ../ 的包必须被拒")
check(not os.path.exists(os.path.join(tmp, "evil.txt")), "穿越文件没有被写出去")

print("")
print("== 关闭/重启（不强杀）==")
check(not any("taskkill" in str(v).lower()
              for v in [inject.request_close.__doc__ or ""]),
      "关闭实现里没有强杀逻辑（只用 WM_CLOSE）")
src = open(os.path.join(TOOLS, "vault_unpacker", "inject.py"),
           encoding="utf-8").read()
check("taskkill" not in src and "TerminateProcess" not in src,
      "源码里确实没有强杀（taskkill / TerminateProcess）")

print("")
print("== 挑关闭窗口：托盘模式下也要挑得出来 ==")
# 这组是纯数据运算，不碰 Win32 —— 真实窗口在这层造不出来。
H = inject._is_helper_window
check(H("GDI+ Window (Playnite.DesktopApp.exe)"), "标题前缀是 GDI+ Window 的算辅助窗口")
check(H("Default IME") and H("MSCTFIME UI") and H("CiceroUIWndFrame"),
      "输入法/系统那几个辅助窗口都算辅助窗口")
check(H(".NET-BroadcastEventWindow.4.0.0.0.3b1d6b0.0"), "带后缀的广播窗口也算辅助窗口")
check(H("") and H("   "), "没有标题的窗口算辅助窗口")
check(not H("Playnite"), "标题是 Playnite 的是主窗口，不算辅助窗口")

P = inject._pick_close_targets
check(P([{"hwnd": 1, "visible": True, "title": "Playnite"},
         {"hwnd": 2, "visible": False, "title": "Playnite"}]) == [1],
      "有可见窗口时只发给可见的那个（不给隐藏窗口发第二发）")
tray = [{"hwnd": 7, "visible": False, "title": "Playnite"},
        {"hwnd": 8, "visible": False, "title": "GDI+ Window (Playnite.DesktopApp.exe)"},
        {"hwnd": 9, "visible": False, "title": ".NET-BroadcastEventWindow.4.0.0.0.3b1d6b0.0"},
        {"hwnd": 10, "visible": False, "title": ""}]
check(P(tray) == [7],
      "全部窗口都不可见（托盘模式）时，挑出隐藏的主窗口 —— 修复前这里必然是空")
check(P([{"hwnd": 8, "visible": False, "title": "Default IME"},
         {"hwnd": 10, "visible": False, "title": ""}]) == [],
      "一个主窗口都没有时宁可不发，也不乱给辅助窗口发 WM_CLOSE")
check(P([]) == [], "窗口列表为空时返回空，不炸")

# 真机对照：Playnite 此刻就在跑（而且是托盘/隐藏状态），拿它验一遍上面那条。
live = playnite.playnite_processes() or []
if live:
    wins = inject._enumerate_top_level_windows(live[0]["pid"])
    print("  Playnite 顶层窗口：" + json.dumps(
        [{"visible": w["visible"], "title": w["title"]} for w in wins],
        ensure_ascii=False))
    check(len(wins) >= 1, "能枚举出正在运行的 Playnite 的顶层窗口")
    if not [w for w in wins if w["visible"]]:
        check(bool(inject._top_level_windows(live[0]["pid"])),
              "真机上 Playnite 窗口全不可见，但照样挑出了关闭目标（就是这次修的 bug）")
else:
    print("  （Playnite 没在跑，跳过真机对照）")

print("")
print("== 内置插件包：多个版本并存时挑最新的 ==")
K = inject._payload_version_key
check(K("PlayniteVault-1.10.0.zip") > K("PlayniteVault-1.9.0.zip"),
      "1.10.0 大于 1.9.0（按文件名排会反过来 —— 这就是这个 bug 的根）")
check(K("PlayniteVault-1.7.0.zip") > K("PlayniteVault-1.6.0.zip"), "1.7.0 大于 1.6.0")
check(K("一个说不上版本的包.zip") < K("PlayniteVault-0.0.1.zip"),
      "版本解析不出来的排最后（不会被选中）")

pd = os.path.join(tmp, "payload-dir")
os.makedirs(pd, exist_ok=True)
for n in ("PlayniteVault-1.6.0.zip", "PlayniteVault-1.9.0.zip",
          "PlayniteVault-1.10.0.zip", "note.txt"):
    with open(os.path.join(pd, n), "wb") as fh:
        fh.write(b"")
_saved_payload_dir = inject.payload_dir
inject.payload_dir = lambda: pd
try:
    picked = os.path.basename(inject.bundled_package() or "")
finally:
    inject.payload_dir = _saved_payload_dir
check(picked == "PlayniteVault-1.10.0.zip",
      "1.6 / 1.9 / 1.10 并存时挑出 1.10.0", picked)
check(inject.bundled_package() and
      inject.bundled_package().lower().endswith(".zip"),
      "非 zip 文件不会被当成插件包")

real_payload = inject.bundled_package()
if real_payload:
    bar = os.path.dirname(real_payload)
    present = [n for n in os.listdir(bar) if n.lower().endswith(".zip")]
    best = sorted(present, key=K)[-1]
    print("  仓库里的内置包：" + ", ".join(present) + "  → 选中 " + os.path.basename(real_payload))
    check(os.path.basename(real_payload) == best,
          "真机目录里并存两个 zip 时，选中的确实是版本最高的那个")

shutil.rmtree(tmp, ignore_errors=True)
print("")
print("通过 %d 项，失败 %d 项" % (ok, fail))
sys.exit(1 if fail else 0)
