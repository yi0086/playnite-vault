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

shutil.rmtree(tmp, ignore_errors=True)
print("")
print("通过 %d 项，失败 %d 项" % (ok, fail))
sys.exit(1 if fail else 0)
