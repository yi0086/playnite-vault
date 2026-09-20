# -*- coding: utf-8 -*-
"""
Vault 解包器（图形界面）—— 打包成 exe 的入口。

打包：
    python tools/build-vault-unpacker.py
产物：
    tools/dist/VaultUnpacker.exe   （单文件，双击即用，不需要 Python 环境）

自检（用来确认 exe 在别的机器上能不能用）：
    VaultUnpacker.exe --selftest
    VaultUnpacker.exe --selftest --url https://host:10086/app/Store --user u --pass p
    VaultUnpacker.exe --selftest --src D:/path/to/apps --unpack-one
"""

import os
import sys

# 让源码方式直接双击运行时也能 import 到同目录的 vault_unpacker 包
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)


# 命令行开关（在 vault_unpacker/cli.py 里定义）。带了任何一个就只跑命令行，不开界面。
#
# 血泪教训：**入口必须先判断命令行**。早先这里只处理 --selftest，别的参数一律走 GUI，
# 于是「VaultUnpacker.exe --inject-plugin」会一声不吭地弹出图形界面，
# 脚本里看起来像「命令跑完了但什么都没发生」。
CLI_FLAGS = frozenset((
    "--id", "--out", "--base", "--dir", "--user", "--password", "--insecure",
    "--config", "--list", "--keep-parts", "--part-tmp", "--workers", "--retries",
    "--register", "--plugin-dir", "--inject-plugin", "--playnite-dir",
    "--plugin-source", "--no-restart", "--keep-legacy", "--quiet",
))


def _is_cli_invocation(argv):
    for raw in argv:
        flag = raw.split("=", 1)[0]
        if flag in CLI_FLAGS:
            return True
        # -h/--help 也该看命令行的帮助 —— 走 GUI 分支的话它会一声不吭弹出主界面
        if flag in ("-h", "--help"):
            return True
        # 认不出来的 -开头参数也当命令行：让 argparse 报错，比静默开界面好排查
        if flag.startswith("-") and flag not in ("--selftest", "-selftest"):
            return True
    return False


def _ensure_output():
    """--windowed 打包后没有控制台时 sys.stdout 可能是 None，写它直接 AttributeError。

    有父进程重定向（脚本里 `> log.txt`）时句柄是有效的，照常输出；
    真没有就落到 exe 同目录的日志文件，至少结果不丢。
    """
    log_path = None
    # 落地/管道都统一成 UTF-8：中文 Windows 默认按 936 写，调用方按 UTF-8 读就是乱码。
    # exe 是 --windowed 的，没有控制台，所以不用担心把控制台显示搞乱。
    try:
        if sys.stdout is not None:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        if sys.stderr is not None:
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    if getattr(sys, "frozen", False) and sys.stdout is None:
        try:
            base = os.path.dirname(sys.executable)
        except Exception:
            base = _HERE
        log_path = os.path.join(base, "vault-unpack-cli.log")
        fh = open(log_path, "a", encoding="utf-8", errors="replace")
        sys.stdout = fh
        sys.stderr = fh
    return log_path


def main():
    argv = sys.argv[1:]
    if "--selftest" in argv or "-selftest" in argv:
        return _run_selftest(argv)
    if "--verify-frozen" in argv:
        return _run_frozen_check()

    if _is_cli_invocation(argv):
        log_path = _ensure_output()
        try:
            from vault_unpacker import cli
        except Exception as ex:
            # 命令行下**绝不能**走 _fatal：那个是弹窗、会一直等人点确定，
            # 脚本看起来就是「卡住了没有输出」。老老实实报错并给非零退出码。
            msg = ("启动失败：%r\n"
                   "（如果这是自己打的 exe，检查 PyInstaller 有没有带 "
                   "--hidden-import vault_unpacker.cli）" % (ex,))
            try:
                sys.stderr.write(msg + "\n")
            except Exception:
                pass
            if log_path:
                try:
                    sys.stdout.write(msg + "\n")
                    sys.stdout.flush()
                except Exception:
                    pass
            return 1
        return cli.main(argv)

    try:
        from vault_unpacker.gui import main as gui_main
    except ImportError as ex:
        # 冻结后缺 tkinter 之类的情况，给一个能看懂的提示而不是闪退
        _fatal("启动失败：%s\n\n"
               "如果是自己用源码运行，请确认你的 Python 带 tkinter。" % ex)
        return 1

    try:
        return gui_main()
    except Exception:
        import traceback
        _fatal("运行出错：\n\n" + traceback.format_exc())
        return 1


def _fatal(msg):
    """无控制台也要让用户看见错误。"""
    try:
        import tkinter
        from tkinter import messagebox
        root = tkinter.Tk()
        root.withdraw()
        messagebox.showerror("Vault 解包器", msg)
        root.destroy()
    except Exception:
        sys.stderr.write(msg + "\n")
        try:
            print(msg)
        except Exception:
            pass


# --------------------------------------------------------------------------
# 自检
# --------------------------------------------------------------------------

def _report_path():
    import tempfile
    return os.path.join(tempfile.gettempdir(), "VaultUnpacker-selftest.txt")


def _run_selftest(argv):
    """不打开主界面，逐项验证运行环境；把结果写进文本并（默认）弹窗展示。

    退出码：0 全部通过；1 有失败项。
    """
    import argparse
    import threading
    import traceback

    ap = argparse.ArgumentParser(prog="VaultUnpacker.exe --selftest")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--src", help="本地仓库目录（apps/ 或它下面的 apps/{id}/）")
    ap.add_argument("--url", help="WebDAV 仓库地址，例如 https://host:10086/app/Store")
    ap.add_argument("--user", default=None)
    ap.add_argument("--pass", dest="password", default=None)
    ap.add_argument("--insecure", action="store_true", help="跳过 HTTPS 证书校验")
    ap.add_argument("--unpack-one", action="store_true",
                    help="真的解包一个最小的应用到临时目录并逐文件自检")
    ap.add_argument("--register", action="store_true",
                    help="配合 --unpack-one：把解出来的目录登记进 Playnite 本地索引")
    ap.add_argument("--plugin-dir", default=None,
                    help="指定插件数据目录（不填则自动定位）")
    ap.add_argument("--out", default=None, help="报告文件路径")
    ap.add_argument("--show", action="store_true", help="结束后弹窗展示（默认只写文件）")
    args = ap.parse_args(argv)

    lines = []
    failed = []

    def ok(msg):
        lines.append("[OK]   " + msg)

    def bad(msg):
        lines.append("[FAIL] " + msg)
        failed.append(msg)

    def info(msg):
        lines.append("       " + msg)

    lines.append("Vault 解包器自检报告")
    lines.append("时间      : " + _ts())
    lines.append("=" * 68)

    # --- 1. 运行环境 -------------------------------------------------------
    lines.append("")
    lines.append("【1】运行环境")
    frozen = getattr(sys, "frozen", False)
    info("执行文件  : %s" % sys.executable)
    info("冻结模式  : %s" % ("是（单文件 exe）" if frozen else "否（源码运行）"))
    if frozen:
        info("解包目录  : %s" % getattr(sys, "_MEIPASS", "?"))
    info("Python    : %s" % sys.version.split()[0])
    info("平台      : %s" % sys.platform)

    # --- 2. tkinter --------------------------------------------------------
    lines.append("")
    lines.append("【2】图形界面运行时（tkinter / Tcl / Tk）")
    try:
        import tkinter
        info("Tcl/Tk    : %s" % tkinter.TkVersion)
        root = tkinter.Tk()
        root.withdraw()
        root.update_idletasks()
        root.destroy()
        ok("tkinter 可用，主窗口能创建（GUI 模式没问题）")
    except Exception as ex:
        bad("tkinter 不可用：%r" % (ex,))

    # --- 3. 网络与 HTTPS ---------------------------------------------------
    lines.append("")
    lines.append("【3】网络栈（WebDAV 依赖）")
    try:
        import ssl
        info("OpenSSL   : %s" % ssl.OPENSSL_VERSION)
        ctx = ssl.create_default_context()
        n = len(ctx.get_ca_certs())
        info("系统 CA   : %d 张" % n)
        ok("ssl 模块可用（HTTPS 走得了）")
    except Exception as ex:
        bad("ssl 不可用（只能用 http 仓库）：%r" % (ex,))

    try:
        import urllib.request as ur
        opener = ur.build_opener(ur.ProxyHandler({}))
        info("urllib    : 已装载（内置直连 opener，绕过系统代理）")
        ok("urllib 可用")
    except Exception as ex:
        bad("urllib 不可用：%r" % (ex,))

    # --- 4. Playnite 定位 --------------------------------------------------
    lines.append("")
    lines.append("【4】Playnite 插件数据目录")
    try:
        from vault_unpacker import playnite
        running = playnite.is_playnite_running()
        if running is True:
            info("Playnite  : 正在运行（登记前请先退出，否则它退出时会覆盖我们的登记）")
        elif running is False:
            info("Playnite  : 未运行（可以安全登记）")
        else:
            info("Playnite  : 状态未知（进程查询失败，登记前请自行确认它没开着）")
        pdir, how = playnite.find_plugin_data_dir()
        if pdir:
            info("定位方式  : %s" % how)
            info("插件目录  : %s" % pdir)
            ok("能定位到 Vault 插件数据目录（可以解包后直接登记）")
        else:
            info("定位方式  : 未找到")
            bad("定位不到 Vault 插件数据目录；"
                "登记功能不可用（可以手动填路径），解包本身不受影响")
        st = playnite.load_state()
        info("上次状态  : %s" % (sorted(st.keys()) if st else "（无）"))
        info("旧状态输出: %s（兼容用；配置里设了父目录就以配置为准）"
             % playnite.default_out_root())
        try:
            from vault_unpacker import config as cfg
            cpath, cdata, created = cfg.ensure()
            info("配置文件  : %s%s"
                 % (cpath, "（本次新建）" if created else ""))
            info("  URL     : %s" % (cdata.get("webdav_url") or "(空)"))
            info("  账号    : %s" % (cdata.get("webdav_user") or "(空)"))
            info("  密码    : %s" % ("已填" if cdata.get("webdav_password")
                                    else "（空）"))
            info("  父目录  : %s" % (cdata.get("output_parent_dir")
                                    or "(未设，回退旧状态输出)"))
            info("  生效输出: %s" % (cdata.get("output_parent_dir")
                                    or playnite.default_out_root()))
            if cpath and os.path.isfile(cpath):
                ok("配置文件可读写（个人设置不进代码）")
            else:
                bad("配置文件不可写：%s" % cpath)
        except Exception as ex:
            bad("配置文件模块出错：%r" % (ex,))
    except Exception as ex:
        bad("Playnite 模块出错：%r" % (ex,))

    # --- 5. 仓库读取 -------------------------------------------------------
    src = None
    if args.url or args.src:
        lines.append("")
        lines.append("【5】仓库读取")
        try:
            from vault_unpacker import core
            if args.url:
                src = core.HttpSource(args.url, args.user, args.password,
                                      verify_tls=not args.insecure)
            else:
                src = core.LocalSource(args.src)
            info("来源      : %s" % src.describe())
            apps = core.list_apps(src)
            ok("读到 %d 个应用" % len(apps))
            for a in apps:
                info("  · %-22s %-30s %10s  %d 文件 / %d 分片"
                     % (a["id"], (a["name"] or "")[:30],
                        core.human_size(a["total_bytes"]),
                        a["file_count"], a["part_count"]))
        except Exception as ex:
            bad("读仓库失败：%s" % ex)
            info(traceback.format_exc().rstrip().replace("\n", "\n       "))

    # --- 6. 真实解包 + 自检 -------------------------------------------------
    if args.unpack_one and src is not None:
        lines.append("")
        lines.append("【6】真实解包（挑最小的一个应用，解到临时目录）")
        try:
            from vault_unpacker import core
            apps = core.list_apps(src)
            smallest = sorted(apps, key=lambda a: a["total_bytes"])[0]
            out = os.path.join(_report_dir(), "selftest-out")
            info("目标      : %s (%s)" % (smallest["name"], smallest["id"]))
            info("输出      : %s" % out)
            res = core.unpack(src, smallest["id"], out, core.NullReporter(),
                              threading.Event())
            ok("解包完成：%d 个文件 / %s"
               % (res["files"], core.human_size(res["bytes"])))
            good, problems = core.verify_dir(out, res["manifest"])
            if problems:
                bad("逐文件自检发现 %d 处问题" % len(problems))
                for p in problems[:10]:
                    info("  · " + p)
            else:
                ok("逐文件自检通过：%d 个文件大小全部一致" % good)

            if args.register:
                lines.append("")
                lines.append("【7】登记到 Playnite")
                if playnite.is_playnite_running() is True:
                    bad("Playnite 正在运行，先退出再登记（跳过）")
                else:
                    if playnite.is_playnite_running() is None:
                        info("提示：查不出 Playnite 是否在运行，请自行确认它已退出")
                    if args.plugin_dir:
                        pdir = playnite.looks_like_plugin_data_dir(args.plugin_dir)
                        if not pdir:
                            bad("--plugin-dir 给的不是插件目录：%s" % args.plugin_dir)
                    else:
                        pdir = playnite.find_plugin_data_dir()[0]
                    if not pdir:
                        bad("定位不到插件目录，跳过登记")
                    else:
                        mf = res["manifest"]
                        entry = playnite.register_install(
                            pdir, smallest["id"], out,
                            mf.get("Version"), mf.get("LaunchExe"))
                        ok("已登记：%s → %s" % (entry["AppId"], entry["InstallDir"]))
                        info("插件目录  : %s" % pdir)
                        idx = playnite.read_local_index(pdir)
                        for e in idx.get("Apps") or []:
                            info("  · %s  InstalledAt=%s"
                                 % (e.get("AppId"), e.get("InstalledAt")))
        except Exception as ex:
            bad("解包/自检失败：%s" % ex)
            info(traceback.format_exc().rstrip().replace("\n", "\n       "))
    elif args.unpack_one:
        lines.append("")
        lines.append("【6】真实解包 —— 跳过（需要 --src 或 --url）")

    # --- 收尾 --------------------------------------------------------------
    lines.append("")
    lines.append("=" * 68)
    if failed:
        lines.append("结论：有 %d 项失败" % len(failed))
        for f in failed:
            lines.append("  · " + f)
    else:
        lines.append("结论：全部通过")

    text = "\n".join(lines)
    path = args.out or _report_path()
    try:
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(text + "\n")
    except Exception as ex:
        sys.stderr.write("写报告失败：%r\n" % (ex,))
        path = None

    try:
        print(text)
    except Exception:
        pass

    if args.show and path:
        try:
            import tkinter
            from tkinter import messagebox
            root = tkinter.Tk()
            root.withdraw()
            messagebox.showinfo("Vault 解包器 · 自检",
                                ("自检%s\n\n报告已写到：\n%s\n\n%s"
                                 % ("通过" if not failed else "有失败项", path,
                                    "\n".join(lines[:12]))))
            root.destroy()
        except Exception:
            pass

    return 1 if failed else 0


def _ts():
    import datetime
    return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def _report_dir():
    import tempfile
    return tempfile.gettempdir()


# --------------------------------------------------------------------------
# 冻结后自检（给打包脚本用）
# --------------------------------------------------------------------------

# 这些模块必须真的能在冻结后的 exe 里导入。
#
# 为什么需要这条：
# PyInstaller 的 `--exclude-module` 一旦排掉了运行期真的用到的包，问题**只在打包产物里
# 才出现** —— 源码跑得好好的。踩过一次：`concurrent` 被排除，而 core.py 顶层就
# `from concurrent.futures import ThreadPoolExecutor`，于是 exe 一导入 core 就
# ModuleNotFoundError，图形界面和解包全废，却一直没人发现。
# 所以在 build 之后、发布之前，直接让 exe 自己 import 一遍。
FROZEN_CHECK_MODULES = (
    "vault_unpacker",
    "vault_unpacker.config",
    "vault_unpacker.core",
    "vault_unpacker.playnite",
    "vault_unpacker.inject",
    "vault_unpacker.icon",
    "vault_unpacker.cli",
    "vault_unpacker.gui",
    # 标准库里容易被 --exclude-module 误伤的
    "concurrent.futures",
    "threading",
    "queue",
    "hashlib",
    "tkinter",
    "tkinter.ttk",
    "urllib.request",
    "ssl",
    "zipfile",
    "shutil",
    "tempfile",
    "json",
    "base64",
    "zlib",
)


def _run_frozen_check():
    """在冻结后的 exe 里逐个 import，确认没有模块被 --exclude-module 误伤。

    退出码：0 全过；1 有导入失败。
    """
    import io
    import traceback

    lines = ["Vault 解包器 · 打包产物导入自检", "时间 : " + _ts(),
             "冻结 : %s" % ("是" if getattr(sys, "frozen", False) else "否（源码运行）"),
             "=" * 60, ""]
    failed = []

    for name in FROZEN_CHECK_MODULES:
        try:
            __import__(name)
            lines.append("[OK]   " + name)
        except Exception as ex:
            lines.append("[FAIL] %s —— %r" % (name, ex))
            lines.append("       " + traceback.format_exc().rstrip().replace("\n", "\n       "))
            failed.append(name)

    # 真正把 ThreadPoolExecutor 拿来用一下（只 import 子模块不保证类可用）
    try:
        from concurrent.futures import ThreadPoolExecutor
        with ThreadPoolExecutor(max_workers=2) as pool:
            got = list(pool.map(lambda x: x * 2, (1, 2, 3)))
        if got == [2, 4, 6]:
            lines.append("[OK]   ThreadPoolExecutor 真能跑（解包并发下载靠它）")
        else:
            lines.append("[FAIL] ThreadPoolExecutor 结果不对：%r" % (got,))
            failed.append("ThreadPoolExecutor 运行")
    except Exception as ex:
        lines.append("[FAIL] ThreadPoolExecutor 跑不起来 —— %r" % (ex,))
        failed.append("ThreadPoolExecutor")

    # tkinter 得能真建窗口，光 import 不够
    try:
        import tkinter
        root = tkinter.Tk()
        root.withdraw()
        root.update_idletasks()
        root.destroy()
        lines.append("[OK]   tkinter 能建窗口（图形界面可用）")
    except Exception as ex:
        lines.append("[FAIL] tkinter 建窗口失败 —— %r" % (ex,))
        failed.append("tkinter 建窗口")

    # 内置插件包在不在（「安装插件到 Playnite」里选『用内置的』要用它）
    try:
        from vault_unpacker import inject
        pkg = inject.bundled_package()
        if pkg:
            lines.append("[OK]   内置插件包：%s" % pkg)
        else:
            lines.append("[!]    没有内置插件包（该选项会退回联网取最新，不算失败）")
    except Exception as ex:
        lines.append("[FAIL] 取内置插件包失败 —— %r" % (ex,))
        failed.append("内置插件包")

    lines.append("")
    lines.append("=" * 60)
    lines.append("结论：%s" % ("全部通过" if not failed else "有 %d 项失败：%s"
                              % (len(failed), ", ".join(failed))))
    text = "\n".join(lines)

    path = os.path.join(_report_dir(), "VaultUnpacker-frozen-check.txt")
    try:
        with io.open(path, "w", encoding="utf-8") as fh:
            fh.write(text + "\n")
    except Exception:
        path = None

    try:
        out = sys.stdout
        if out is None:
            out = open(os.path.join(_report_dir(), "vault-unpack-frozen.txt"),
                       "w", encoding="utf-8")
        out.write(text + "\n")
        out.write("报告：%s\n" % path)
        out.flush()
    except Exception:
        pass

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
