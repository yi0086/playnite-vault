# -*- coding: utf-8 -*-
r"""注入链路的端到端测试：命令行注入 + 真实 WM_CLOSE 优雅关闭。

全程在临时目录里跑：假的 Playnite 根目录、隔离的配置文件和状态目录，
绝不碰真实的 Playnite 安装目录，也不覆盖用户真实的 config.json。
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

# 本文件在 <仓库>/tools/python-selftest/ 下，往上两级就是仓库根
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOLS = os.path.join(ROOT, "tools")
sys.path.insert(0, TOOLS)


def find_tk_python():
    """找一个带 tkinter 的解释器，用来起那个「假 Playnite 窗口」子进程。

    优先用当前解释器（它有 tkinter 就说明它行）；否则看环境变量 VAULT_TK_PYTHON。
    实在找不到就返回 None，第 3 段跳过 —— 不把「这台机器没装」算成测试失败。
    """
    probe = subprocess.run([sys.executable, "-c", "import tkinter"],
                           capture_output=True)
    if probe.returncode == 0:
        return sys.executable
    cand = os.environ.get("VAULT_TK_PYTHON")
    if cand and os.path.isfile(cand):
        probe = subprocess.run([cand, "-c", "import tkinter"],
                               capture_output=True)
        if probe.returncode == 0:
            return cand
    return None


SYS_PY = find_tk_python()

ok = fail = 0


def check(cond, label, detail=""):
    global ok, fail
    if cond:
        ok += 1
        print("  [通过] " + label)
    else:
        fail += 1
        print("  [失败] " + label + (" —— " + detail if detail else ""))


tmp = tempfile.mkdtemp(prefix="inject-e2e-")
# 隔离：配置/状态/备份都落到临时目录
os.environ["VAULT_UNPACKER_CONFIG"] = os.path.join(tmp, "config.json")
os.environ["APPDATA"] = os.path.join(tmp, "appdata")
os.makedirs(os.environ["APPDATA"], exist_ok=True)

from vault_unpacker import inject, playnite  # noqa: E402

print("== 1. 命令行注入（假 Playnite 根目录）==")
fake_root = os.path.join(tmp, "FakePlaynite")
os.makedirs(os.path.join(fake_root, "Extensions"))
with open(os.path.join(fake_root, "Playnite.DesktopApp.exe"), "wb") as f:
    f.write(b"MZ" + b"\x00" * 64)
# 造一个改名前的旧目录，验证会被挪走
legacy = os.path.join(fake_root, "Extensions",
                      "VaultDemo_5e76bf50-cb8a-4a87-ad24-1912c746c6f0")
os.makedirs(legacy)
open(os.path.join(legacy, "VaultDemo.dll"), "wb").write(b"MZ")

env = dict(os.environ)
env["PYTHONPATH"] = TOOLS
proc = subprocess.run(
    [sys.executable, "-m", "vault_unpacker.cli",
     "--inject-plugin", "--playnite-dir", fake_root,
     "--plugin-source", "bundled"],
    capture_output=True, env=env, cwd=TOOLS)
out = proc.stdout.decode("utf-8", "replace") + proc.stderr.decode("utf-8", "replace")
print("\n".join("    " + l for l in out.strip().splitlines()))
check(proc.returncode == 0, "命令行注入退出码 0", "实际 %d" % proc.returncode)

target = os.path.join(fake_root, "Extensions", "Playnite-Vault")
check(os.path.isfile(os.path.join(target, "PlayniteVault.dll")), "dll 已注入")
check(os.path.isfile(os.path.join(target, "extension.yaml")), "extension.yaml 已注入")
check(not os.path.isdir(legacy), "改名前的旧目录被挪走了")
info = inject.installed_info(os.path.join(fake_root, "Extensions"))
check(info and info["version"] == "1.6.0", "读回版本 1.6.0",
      json.dumps(info, ensure_ascii=False))
backups = []
for root, _dirs, files in os.walk(os.environ["APPDATA"]):
    if "VaultDemo_5e76bf50-cb8a-4a87-ad24-1912c746c6f0" in root:
        backups.append(root)
check(bool(backups), "旧目录被备份到了用户状态目录，而不是 Extensions 里",
      str(backups))

print("")
print("== 2. 幂等：再注一次（已装的要备份走）==")
proc2 = subprocess.run(
    [sys.executable, "-m", "vault_unpacker.cli",
     "--inject-plugin", "--playnite-dir", fake_root,
     "--plugin-source", "bundled"],
    capture_output=True, env=env, cwd=TOOLS)
check(proc2.returncode == 0, "第二次注入也退出码 0", "实际 %d" % proc2.returncode)
check(os.path.isfile(os.path.join(target, "PlayniteVault.dll")), "第二次之后插件仍在位")

print("")
print("== 3. 真实 WM_CLOSE 优雅关闭 ==")
if not SYS_PY:
    print("  [跳过] 找不到带 tkinter 的解释器（设 VAULT_TK_PYTHON 可指定）")
else:
    child_src = os.path.join(tmp, "win.py")
    with open(child_src, "w", encoding="utf-8") as f:
        f.write("import tkinter as tk\n"
                "r = tk.Tk()\n"
                "r.title('fake-playnite')\n"
                "r.geometry('300x120')\n"
                "r.mainloop()\n")
    child = subprocess.Popen([SYS_PY, child_src], cwd=tmp)
    time.sleep(2.5)          # 等窗口真的建出来
    check(child.poll() is None, "假 Playnite 窗口进程起来了")

    # 把「什么算 Playnite 进程」换掉：Windows 上没有 Playnite 可关，
    # 但 WM_CLOSE → 等它自己退 这条逻辑是完全一样的。
    # 注意这个假探测**必须真的检查子进程还活着没有** —— 无条件返回一个 pid 的话，
    # 关闭逻辑会永远看不到「进程已退出」，于是必然等满超时（这个测试自己踩过）。
    real_probe = playnite.playnite_processes
    playnite.playnite_processes = lambda: (
        [] if child.poll() is not None
        else [{"pid": child.pid, "name": "Playnite.DesktopApp.exe", "exe": SYS_PY}])

    logs = []
    t0 = time.time()
    state, _ = inject.request_close(logs.append, timeout=25)
    dt = time.time() - t0
    playnite.playnite_processes = real_probe

    print("\n".join("    " + l for l in logs))
    check(state == "closed", "优雅关闭成功（状态 closed）", state)
    check(dt < 24, "是「对方自己退的」而不是等满超时", "耗时 %.1f 秒" % dt)
    check(child.poll() is not None, "窗口进程确实退出了")

print("")
print("== 4. 配置里记住了 Playnite 目录 ==")
with open(os.environ["VAULT_UNPACKER_CONFIG"], "r", encoding="utf-8") as f:
    cfg = json.load(f)
print("    playnite_dir = %r" % cfg.get("playnite_dir"))
check("playnite_dir" in cfg, "配置文件里有 playnite_dir 这一项")

shutil.rmtree(tmp, ignore_errors=True)
print("")
print("通过 %d 项，失败 %d 项" % (ok, fail))
sys.exit(1 if fail else 0)
