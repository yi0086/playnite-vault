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


def main():
    argv = sys.argv[1:]
    if "--selftest" in argv or "-selftest" in argv:
        return _run_selftest(argv)

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


if __name__ == "__main__":
    sys.exit(main())
