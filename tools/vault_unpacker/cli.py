# -*- coding: utf-8 -*-
"""命令行入口 —— 与 GUI 共用同一套核心逻辑。

没在命令行里给的参数（仓库地址、账号、密码、目录、插件目录）会从外部配置文件补，
和 GUI 用的是同一份（见 config.py），所以配置一次两边都能用。
"""

import argparse
import json
import os
import sys
import threading

from . import config, core, inject, playnite


class _CliReporter(core.Reporter):
    def __init__(self, quiet=False):
        self.quiet = quiet
        self.last = ""

    def log(self, msg):
        if not self.quiet:
            print(msg, flush=True)

    def progress(self, done, total, label=""):
        if self.quiet or not total:
            return
        pct = 100.0 * done / total
        line = "\r%6.2f%%  %s / %s  %s" % (pct, core.human_size(done),
                                           core.human_size(total), label[:40].ljust(40))
        if line != self.last:
            sys.stdout.write(line)
            sys.stdout.flush()
            self.last = line

    def stage(self, name):
        pass


def _load_cfg(path=None):
    try:
        with open(path or config.config_path(), "r", encoding="utf-8-sig") as f:
            return json.load(f) or {}
    except Exception:
        return {}


def _apply_config(args):
    """命令行没给的，从外部配置补上。绝不在代码里兜默认账号。"""
    c = _load_cfg(args.config)
    if not args.base and not args.dir:
        if c.get("mode") == "local" and c.get("local_dir"):
            args.dir = c["local_dir"]
        elif c.get("webdav_url"):
            args.base = c["webdav_url"]
    if args.user is None:
        args.user = c.get("webdav_user") or ""
    if args.password is None:
        args.password = c.get("webdav_password") or ""
    if not args.insecure:
        args.insecure = bool(c.get("webdav_insecure_tls"))
    if not args.out:
        args.out = c.get("output_parent_dir") or ""
    if not args.plugin_dir:
        args.plugin_dir = c.get("plugin_data_dir") or None
    if not args.playnite_dir:
        args.playnite_dir = c.get("playnite_dir") or None
    return c


def _target_for(parent, folder):
    """<父目录>/<子目录名>。兜底：父目录位置若误填成应用自己的目录，则退回上一级。"""
    parent = os.path.normpath(parent)
    base = os.path.basename(parent)
    if base and base.lower() == str(folder).lower():
        up = os.path.dirname(parent)
        if up:
            parent = up
    return os.path.join(parent, folder)


def _build_source(args):
    if args.base:
        return core.HttpSource(args.base, args.user, args.password,
                               verify_tls=not args.insecure)
    return core.LocalSource(args.dir)


def _run_inject(args, cfg):
    """把插件注入 Playnite（命令行版）。返回退出码。

    顺序是死的：**先让 Playnite 退出 → 再写文件 → 再拉起来**。
    PlayniteVault.dll 被 Playnite 进程锁着，反过来做必然 Permission denied。
    关闭走优雅方式（WM_CLOSE），不强杀。
    """
    rep = _CliReporter(args.quiet)
    log = rep.log

    root, note = inject.pick_root(args.playnite_dir)
    if not root:
        log("失败：" + note)
        return 3
    log("[定位] " + note)

    # 记住这次用的目录，下次不用再 --playnite-dir
    try:
        if (cfg.get("playnite_dir") or "") != root:
            cfg["playnite_dir"] = root
            config.save(cfg)
            log("[配置] 已记住 Playnite 目录：" + config.config_path())
    except Exception as ex:
        log("[配置] 记住目录失败（不影响注入）：%s" % ex)

    exts = inject.extensions_dir_of(root)
    info = inject.installed_info(exts)
    log("[现状] " + ("已装版本 %s（%s）" % (info.get("version"), info["dir"])
                     if info else "尚未安装 " + inject.PLUGIN_ID))

    try:
        # 1) 拿包
        work = os.path.join(playnite.state_dir(), "plugin-download")
        zip_path, pkgnote = inject.fetch_package(
            work, args.plugin_source, args.plugin_source
            if args.plugin_source in ("github", "gitee") else "auto", log)
        log("[插件包] %s → %s" % (pkgnote, zip_path))

        # 2) 记录注入前在跑哪个 exe，一会儿按原样拉起来
        procs = playnite.playnite_processes()
        target_exe = None
        for p in procs or []:
            if p.get("exe"):
                target_exe = p["exe"]
                break

        if procs:
            state, _ = inject.request_close(log)
            if state == "timeout":
                log("失败：Playnite 没有退出（可能开了「关闭到托盘」）。")
                log("      请手动退出 Playnite 后重跑本命令 —— 本工具不会强杀它，")
                log("      强杀会让 Playnite 丢掉还没落盘的库改动。")
                return 4
            if state == "unknown":
                log("失败：查不出 Playnite 是否在运行，为安全起见不注入。")
                return 4
        elif procs == []:
            log("[状态] 注入前 Playnite 没在运行")

        # 3) 注入
        staging = os.path.join(playnite.state_dir(), "plugin-staging")
        res = inject.install_package(zip_path, exts, staging, log,
                                     move_legacy=not args.keep_legacy)
        log("[完成] 插件已装到 %s（版本 %s）" % (res["dir"], res["version"]))
        if res["backup"]:
            log("[完成] 旧版本备份：%s" % res["backup"])

        # 4) 拉起来（原来在跑、且用户没要求别重启）
        if procs and not args.no_restart:
            inject.launch(target_exe, log)
        elif procs:
            log("[重启] 按要求没有重启。")
        else:
            log("[重启] 注入前它没在跑，没有替你启动。")
        return 0

    except inject.InjectError as ex:
        log("失败：" + str(ex))
        return 1


def main(argv=None):
    ap = argparse.ArgumentParser(
        prog="vault-unpack",
        description="独立解包 Vault 分片归档（不需要 Playnite 插件）")
    ap.add_argument("--id", help="应用 Id；多个用逗号分隔，如 a,b,c")
    ap.add_argument("--out", help="输出**父目录**（每个应用解到 <它>/<id>/）")
    ap.add_argument("--base", help="WebDAV 仓库地址，例如 https://host:5006/app/Store")
    ap.add_argument("--dir", help="本地仓库目录（apps/ 或其下的 apps/{id}/）")
    ap.add_argument("--user", default=None, help="WebDAV 用户名")
    ap.add_argument("--password", default=None, help="WebDAV 密码")
    ap.add_argument("--insecure", action="store_true", help="跳过 HTTPS 证书校验")
    ap.add_argument("--config", default=None, help="配置文件路径，默认自动定位")
    ap.add_argument("--list", action="store_true", help="只列出仓库里的应用 / 打印清单")
    ap.add_argument("--keep-parts", action="store_true",
                    help="保留下载下来的区块/分片临时文件（默认下完即删）")
    ap.add_argument("--part-tmp", default=None, help="区块临时目录")
    ap.add_argument("--workers", type=int, default=core.DEFAULT_CHUNK_WORKERS,
                    help="并发下载区块的路数（默认 %d；不能超过 8）"
                         % core.DEFAULT_CHUNK_WORKERS)
    ap.add_argument("--retries", type=int, default=core.DEFAULT_RETRIES,
                    help="单个区块失败后的重试次数（默认 %d）"
                         % core.DEFAULT_RETRIES)
    ap.add_argument("--register", action="store_true",
                    help="解包后登记到 Playnite 的 Vault 插件（需能定位插件数据目录）")
    ap.add_argument("--plugin-dir", default=None, help="手动指定插件数据目录")
    # ---- 注入插件（和「解包」是两件独立的事，给了 --inject-plugin 就只干这一件）----
    ap.add_argument("--inject-plugin", action="store_true",
                    help="把 Playnite-Vault 插件装进 Playnite 的 Extensions 目录")
    ap.add_argument("--playnite-dir", default=None,
                    help="Playnite 安装目录（默认自动定位，也可写在配置里）")
    ap.add_argument("--plugin-source", default="auto",
                    choices=("bundled", "auto", "github", "gitee"),
                    help="插件包来源：bundled = e xe 内置的；auto/github/gitee = 联网取最新")
    ap.add_argument("--no-restart", action="store_true",
                    help="注入前 Playnite 在跑的话，关掉它、注入，但不再拉起来")
    ap.add_argument("--keep-legacy", action="store_true",
                    help="不挪走改名前的旧目录 VaultDemo_*（默认会挪到备份）")
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)

    cfg = _apply_config(args)

    if args.inject_plugin:
        return _run_inject(args, cfg)

    if not args.base and not args.dir:
        ap.error("必须指定 --base 或 --dir（或在配置文件里填好 webdav_url / local_dir）")

    rep = _CliReporter(args.quiet)
    ids = [s.strip() for s in (args.id or "").split(",") if s.strip()]

    try:
        src = _build_source(args)
        rep.log("[来源] %s" % src.describe())

        if args.list:
            apps = core.list_apps(src)
            rep.log("")
            rep.log("%-24s %-36s %10s %6s %8s" % ("Id", "名称", "大小", "文件", "布局"))
            rep.log("-" * 92)
            for a in apps:
                rep.log("%-24s %-36s %10s %6d %8s"
                        % (a["id"], (a["name"] or "")[:36],
                           core.human_size(a["total_bytes"]), a["file_count"],
                           core.layout_label(a)))
            if ids:
                mf = core.fetch_manifest(src, ids[0])
                rep.log("")
                rep.log("清单 %s：Packed=%s  ChunkSize=%.1f MB  Chunks=%d  Files=%d"
                        % (ids[0], mf.get("Packed"),
                           (mf.get("ChunkSize") or 0) / 1048576.0,
                           len(mf.get("Chunks") or []), len(mf.get("Files") or [])))
            return 0

        if not ids:
            ap.error("解包需要 --id")
        if not args.out:
            ap.error("解包需要 --out（或在配置文件里填好 output_parent_dir）")

        if args.register and args.plugin_dir:
            real = playnite.looks_like_plugin_data_dir(args.plugin_dir)
            if real:
                args.plugin_dir = real

        # 子目录名优先用归档里记的原始安装目录名（Brotato），旧归档退回 app id
        folder_of = {}
        try:
            for a in core.list_apps(src):
                folder_of[str(a.get("id"))] = core.folder_for(a)
        except Exception:
            pass

        # 并发路数可以在 unpack 里被钳位，但这里早一点报错，提示更清楚
        workers = getattr(args, "workers", core.DEFAULT_CHUNK_WORKERS)
        if workers < 1 or workers > 8:
            ap.error("--workers 需要在 1~8 之间")

        bad = 0
        for i, app_id in enumerate(ids, 1):
            folder = folder_of.get(app_id) or core.folder_for({"id": app_id})
            out_dir = _target_for(args.out, folder)
            if len(ids) > 1:
                rep.log("")
                rep.log("[%d/%d] %s → %s" % (i, len(ids), app_id, out_dir))

            result = core.unpack(src, app_id, out_dir, rep, threading.Event(),
                                 args.keep_parts, args.part_tmp,
                                 workers, max(1, args.retries))
            if not args.quiet:
                print("", flush=True)
            rep.log("完成：%d 个文件 / %s → %s"
                    % (result["files"], core.human_size(result["bytes"]),
                       result["out_dir"]))

            ok, problems = core.verify_dir(out_dir, result["manifest"], rep)
            if problems:
                rep.log("自检发现问题（%d 处）：" % len(problems))
                for p in problems[:10]:
                    rep.log("  " + p)
                bad += 1
                continue
            rep.log("自检通过：%d 个文件大小一致" % ok)

            if args.register:
                plugin_dir = args.plugin_dir or playnite.find_plugin_data_dir()[0]
                if not plugin_dir:
                    rep.log("登记失败：定位不到插件数据目录，可用 --plugin-dir 指定。")
                    return 3
                mf = result["manifest"]
                entry = playnite.register_install(
                    plugin_dir, app_id, out_dir,
                    mf.get("Version"), mf.get("LaunchExe"))
                rep.log("已登记到 Playnite：" + plugin_dir)
                rep.log("  %s → %s" % (entry["AppId"], entry["InstallDir"]))
                rep.log("  提示：到 Playnite 里点一次「Vault → 从 NAS 刷新库条目」即可看到已安装。")

        return 2 if bad else 0

    except core.Cancelled:
        rep.log("已取消。")
        return 130
    except (core.UnpackError, playnite.PlayniteError) as ex:
        rep.log("")
        rep.log("失败：" + str(ex))
        return 1
    except KeyboardInterrupt:
        rep.log("已中断。")
        return 130


if __name__ == "__main__":
    sys.exit(main())
