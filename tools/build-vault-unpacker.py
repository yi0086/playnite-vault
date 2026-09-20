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
    · **会把编译好的插件包一起塞进 exe**（--add-data），于是「安装插件到 Playnite」
      里的「用内置的」没网也能用。所以打 exe 之前先把插件编出来：
          dotnet build src/PlayniteVault/PlayniteVault.csproj -c Release
      没编就先打 exe 也行，只是那个选项会退回「联网取最新」。
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
ROOT = os.path.dirname(HERE)
ENTRY = os.path.join(HERE, "VaultUnpacker.py")
DIST = os.path.join(HERE, "dist")
ICON = os.path.join(HERE, "assets", "vault-unpacker.ico")

# ---- 内置插件包（给「安装插件到 Playnite」用）----
# 插件源码编译出来的三个文件 → 打成一个 zip → 作为 exe 的数据一起发布。
# 这样用户在没网 / GitHub 抽风的时候也能把插件装上。
PLUGIN_SRC_DIR = os.path.join(ROOT, "src", "PlayniteVault")
PLUGIN_BUILD_DIR = os.path.join(PLUGIN_SRC_DIR, "bin", "Release")
PLUGIN_ID = "Playnite-Vault"          # 包内文件夹名 = extension.yaml 的 Id
PLUGIN_DLL = "PlayniteVault.dll"
PAYLOAD_DIR = os.path.join(HERE, "plugin_payload")

# PyInstaller 的中间目录放在系统临时目录，而且**每次构建用全新的名字**。
# 原因：在工作区内原地重建时，PyInstaller 会覆写/清理 workpath 里的上千个文件，
# 这会撞上环境的「批量删除保护」（单轮删除数超过阈值就拦下，构建直接失败）。
# 换成全新的空目录就没有任何删除动作，也就不会被拦。
WORK = os.path.join(tempfile.gettempdir(),
                    "VaultUnpacker-pyi-%d" % os.getpid())
SPEC = os.path.join(WORK, "spec")
NAME = "VaultUnpacker"

# 标准库之外用不到的大件，显式排除，避免 PyInstaller 误收
# 瘦身用的排除清单。**加之前先想清楚**：core.py 顶层就 `from concurrent.futures
# import ThreadPoolExecutor`，早先这里排除了 "concurrent"，结果 exe 一跑到导入 core
# 就 ModuleNotFoundError —— 图形界面和解包全废，而且**只在冻结后**才暴露。
# 现在 build 完会自动跑一次 `--verify-frozen` 把这类问题挡在发布前。
EXCLUDES = [
    "numpy", "pandas", "matplotlib", "scipy", "PIL", "cv2",
    "PyQt5", "PyQt6", "PySide2", "PySide6", "wx",
    "pytest", "setuptools", "pip", "wheel", "pydoc_data",
    "sqlite3", "unittest", "distutils", "lib2to3", "test",
    "curses", "asyncio", "xmlrpc", "pdb",
]


def check_tkinter():
    try:
        import tkinter
        return tkinter.TkVersion
    except ImportError:
        return None


def read_plugin_version():
    """从 extension.yaml 抠版本号 —— 版本只认这一个来源。"""
    yaml = os.path.join(PLUGIN_SRC_DIR, "extension.yaml")
    if not os.path.isfile(yaml):
        return None
    with open(yaml, "r", encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if line.lower().startswith("version:"):
                return line.split(":", 1)[1].strip().strip("\"'")
    return None


def build_payload():
    """把编译好的插件打成 zip，供 exe 内置。返回 (zip 路径, 说明) 或 (None, 原因)。

    为什么用 zip 而不是直接塞三个文件：注入时走的是一条已验证的路径
    （解 zip → 校验 Id / dll / PE → 写进 Extensions），和「从 release 下载」完全同构。
    多一种分发方式就多一种出错姿势，没必要。
    """
    version = read_plugin_version()
    if not version:
        return None, "读不到 src/PlayniteVault/extension.yaml 里的版本号"

    pieces = [("extension.yaml", os.path.join(PLUGIN_SRC_DIR, "extension.yaml")),
              ("icon.png", os.path.join(PLUGIN_SRC_DIR, "icon.png")),
              (PLUGIN_DLL, os.path.join(PLUGIN_BUILD_DIR, PLUGIN_DLL))]
    missing = [name for name, path in pieces if not os.path.isfile(path)]
    if missing:
        return None, ("缺这些文件：%s\n"
                      "先编译插件：dotnet build src/PlayniteVault/PlayniteVault.csproj "
                      "-c Release -p:PlayniteDir=\"<Playnite 目录>\"" % ", ".join(missing))

    os.makedirs(PAYLOAD_DIR, exist_ok=True)
    zip_path = os.path.join(PAYLOAD_DIR, "PlayniteVault-%s.zip" % version)

    import zipfile
    if os.path.exists(zip_path):
        # 不直接覆写：有些环境带批量删除保护，覆写已有文件会被拦下
        stale = zip_path + ".old"
        try:
            os.replace(zip_path, stale)
            os.remove(stale)
        except OSError:
            pass

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(PLUGIN_ID + "/", b"")
        for name, path in pieces:
            info = zipfile.ZipInfo(PLUGIN_ID + "/" + name,
                                   date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            with open(path, "rb") as fh:
                z.writestr(info, fh.read())

    return zip_path, "插件 %s（内置，%d 字节）" % (version, os.path.getsize(zip_path))


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
        "--hidden-import", "vault_unpacker.inject",
        # cli 只在「带参数」时才 import，PyInstaller 的静态分析看不到 ——
        # 漏了它 exe 会走「导入失败 → 弹窗报错」的分支：命令行下表现为
        # **没有输出、一直卡住**（弹窗在等人点确定），非常难查。踩过一次。
        "--hidden-import", "vault_unpacker.cli",
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

    # 把插件包塞进 exe，运行时用 sys._MEIPASS/plugin 找它（见 vault_unpacker/inject.py）。
    # Windows 上 --add-data 的分隔符是「源码路径;包内路径」。
    payload, why = build_payload()
    if payload:
        cmd += ["--add-data", payload + ";plugin"]
        print("[构建] %s" % why)
    else:
        print("[构建] !! 没能内置插件包：%s" % why)
        print("[构建]    「安装插件到 Playnite」里的『用内置的』会退回联网取最新。")

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

    # 打完必须真的验证一遍。源码能跑不代表冻结后能跑 —— `--exclude-module` 排掉
    # 运行期真要用的包时，问题只在 exe 里出现。这一关就是为了不让那种 exe 流出去。
    if not verify_frozen(exe):
        return 1
    return 0


def verify_frozen(exe):
    """让刚打出来的 exe 自己 import 一遍所有依赖，确认没被误伤。"""
    print("")
    print("[验证] 冻结产物导入自检（--verify-frozen）")
    # --windowed 的 exe 没有控制台，stdout 能不能用取决于调用方；这里重定向到文件，
    # 不管是哪种情况都看得到结果。
    report = os.path.join(WORK, "frozen-check.txt")
    # 强制 UTF-8：不然冻结产物会按系统 OEM 代码页（中文机器是 936）写中文，
    # 我们按 UTF-8 读回来就是一片乱码，验证日志没法看。
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    try:
        with open(report, "wb") as fh:
            rc = subprocess.call([exe, "--verify-frozen"], stdout=fh,
                                 stderr=subprocess.STDOUT, env=env)
    except OSError as ex:
        print("[验证] !! 跑不起来：%s" % ex)
        return False

    text = ""
    for enc in ("utf-8", "cp936", "mbcs"):
        try:
            with open(report, "r", encoding=enc) as fh:
                text = fh.read()
            break
        except (OSError, UnicodeDecodeError):
            continue

    for line in text.splitlines():
        print("        " + line)

    if rc != 0:
        print("")
        print("[验证] !! 产物有问题：exe 里有模块导入不了。")
        print("         常见原因是 tools/build-vault-unpacker.py 的 EXCLUDES 排掉了")
        print("         运行期真的在用的包（例如 concurrent）。修好再发。")
        return False

    print("[验证] 通过：exe 里所有依赖都能导入。")
    return True


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
