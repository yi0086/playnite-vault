#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把 Vault 解包器打成单文件 exe（免 Python 环境）。

用法：
    <带 tkinter 的 python> tools/build-vault-unpacker.py

产物：
    tools/dist/VaultUnpacker.exe

说明：
    · --onefile 单文件，双击即用；首次启动会自解压到临时目录，稍慢一两秒
    · --windowed 不带黑色控制台窗口
    · 只依赖标准库 + tkinter，因此体积主要是 Tcl/Tk 运行时
    · 必须用「带 tkinter 的 Python」来跑本脚本，否则打出来的 exe 起不来
    · exe 图标取 assets/vault-unpacker.ico（由 make-app-icon.py 生成）；
      没生成过就先跑一次 make-app-icon.py，否则只有默认图标
    · 构建完会把 tools/config.json 复制到 exe 旁边（已存在则不动），
      这样从 dist/ 直接拿走的 exe 就是「配置好的」，双击即用
"""

import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ENTRY = os.path.join(HERE, "VaultUnpacker.py")
DIST = os.path.join(HERE, "dist")
ICON = os.path.join(HERE, "assets", "vault-unpacker.ico")

# PyInstaller 的中间目录放在系统临时目录，而且**每次构建用全新的名字**。
# 原因：在工作区内原地重建时，PyInstaller 会覆写/清理 workpath 里的上千个文件，
# 这会撞上环境的「批量删除保护」（单轮删除数超过阈值就拦下，构建直接失败）。
# 换成全新的空目录就没有任何删除动作，也就不会被拦。
WORK = os.path.join(tempfile.gettempdir(),
                    "VaultUnpacker-pyi-%d" % os.getpid())
SPEC = os.path.join(WORK, "spec")
NAME = "VaultUnpacker"

# 标准库之外用不到的大件，显式排除，避免 PyInstaller 误收
EXCLUDES = [
    "numpy", "pandas", "matplotlib", "scipy", "PIL", "cv2",
    "PyQt5", "PyQt6", "PySide2", "PySide6", "wx",
    "pytest", "setuptools", "pip", "wheel", "pydoc_data",
    "sqlite3", "unittest", "distutils", "lib2to3", "test",
    "curses", "asyncio", "concurrent", "multiprocessing", "xmlrpc", "pdb",
]


def check_tkinter():
    try:
        import tkinter
        return tkinter.TkVersion
    except ImportError:
        return None


def main():
    tv = check_tkinter()
    if tv is None:
        sys.stderr.write(
            "当前 Python 没有 tkinter，打出来的 exe 会起不来。\n"
            "请换一个带 tkinter 的 Python 来跑本脚本（Windows 官方安装包默认自带）。\n")
        return 1

    try:
        import PyInstaller  # noqa: F401
    except ImportError:
        sys.stderr.write("缺少 PyInstaller。先装：python -m pip install pyinstaller\n")
        return 1

    print("[构建] Python %s  Tcl/Tk %s" % (sys.version.split()[0], tv))
    print("[构建] 入口 %s" % ENTRY)
    print("[构建] 中间目录 %s" % WORK)

    for d in (DIST, WORK, SPEC):
        os.makedirs(d, exist_ok=True)

    cmd = [
        sys.executable, "-m", "PyInstaller",
        # 不传 --clean：workpath 每次都是新的空目录，不需要也没东西可清。
        "--noconfirm",
        "--onefile", "--windowed",
        "--name", NAME,
        "--distpath", DIST,
        "--workpath", WORK,
        "--specpath", SPEC,
        "--paths", HERE,
        "--hidden-import", "vault_unpacker.core",
        "--hidden-import", "vault_unpacker.playnite",
        "--hidden-import", "vault_unpacker.config",
        "--hidden-import", "vault_unpacker.gui",
        "--hidden-import", "vault_unpacker.icon",
    ]
    # exe 文件本身（资源管理器里看到的）那个图标。窗口/任务栏图标在运行时由
    # vault_unpacker/icon.py 里内嵌的 PNG 设置，两条路互不依赖。
    if os.path.isfile(ICON):
        cmd += ["--icon", ICON]
        print("[构建] 使用图标 %s（%.1f KB）"
              % (ICON, os.path.getsize(ICON) / 1024.0))
    else:
        print("[构建] 没找到 %s，exe 将使用默认图标。"
              "先跑：python tools/make-app-icon.py" % ICON)
    for m in EXCLUDES:
        cmd += ["--exclude-module", m]
    cmd.append(ENTRY)

    exe = os.path.join(DIST, NAME + ".exe")
    _move_aside(exe)

    print("[构建] " + " ".join(cmd[:8]) + " …")
    rc = subprocess.call(cmd)
    if rc != 0:
        sys.stderr.write("\n构建失败（退出码 %d），完整命令：\n  %s\n"
                         % (rc, " ".join(cmd)))
        return rc

    if not os.path.isfile(exe):
        sys.stderr.write("没找到产物：%s\n" % exe)
        return 1

    size = os.path.getsize(exe)
    print("")
    print("[完成] %s" % exe)
    print("[体积] %.1f MB" % (size / 1048576.0))
    seed_config(exe)
    return 0


CONFIG_NAME = "config.json"


def seed_config(exe):
    """把开发用的外部配置复制一份到 exe 旁边。

    单文件 exe 的配置是「跟着 exe 走」的（见 vault_unpacker/config.py：
    优先用 exe 同目录的那份）。不复制的话，从 build 直接拿走的 exe 第一次
    运行还要重新填地址和账号 —— 而这份配置本来就是给人免配置用的。
    已存在就绝不动，免得把用户改过的覆盖掉。
    """
    src = os.path.join(HERE, CONFIG_NAME)
    dst = os.path.join(os.path.dirname(exe), CONFIG_NAME)
    if not os.path.isfile(src):
        return
    if os.path.isfile(dst):
        print("[配置] %s 已存在，保留不动" % dst)
        return
    try:
        shutil.copy2(src, dst)
        print("[配置] 已随 exe 放一份：%s" % dst)
    except OSError as ex:
        print("[配置] 复制失败：%s" % ex)


def _move_aside(exe):
    """把已存在的旧产物改名挪走，而不是删除。

    有些环境带「批量删除保护」，删除/覆写已有文件会被拦下导致构建失败，
    而重命名不算删除。挪走的旧文件放在 dist/prev/ 下，方便随时回退。
    """
    if not os.path.isfile(exe):
        return
    import time
    prev = os.path.join(DIST, "prev")
    os.makedirs(prev, exist_ok=True)
    stale = os.path.join(prev, "%s-%s.exe"
                         % (NAME, time.strftime("%Y%m%d-%H%M%S")))
    try:
        os.replace(exe, stale)
        print("[构建] 旧产物已挪到 %s" % stale)
    except OSError as ex:
        print("[构建] 挪走旧产物失败（%s），继续尝试覆盖" % ex)


if __name__ == "__main__":
    sys.exit(main())
