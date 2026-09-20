# -*- coding: utf-8 -*-
"""把 Playnite-Vault 插件注入到 Playnite 的 Extensions 目录，并优雅重启 Playnite。

为什么要有这个功能
------------------
插件**不可能**在运行期替换自己 —— PlayniteVault.dll 被 Playnite 进程锁着
（Windows 文件锁，实测就是 Permission denied）。于是这些场景都需要「从外面把插件装进去」：

  · 第一次装插件（用户不必自己去翻 Extensions 目录）
  · 插件改过名（VaultDemo_<guid> → Playnite-Vault），老目录要换掉
  · 换了台机器 / Playnite 重装过

解包器本来就在管这台机器上的 Vault，让它顺手做这件事最自然。

设计原则（对应「不要弄乱」）
----------------------------
1. **只写 Extensions\\Playnite-Vault 这一个目录**。不碰 Playnite 核心文件、
   不碰主题、不碰别的插件、不碰 User Data。旧版本的目录是**挪去备份**，不是删，
   而且备份放在解包器自己的目录里 —— Playnite 会把 Extensions 下每个文件夹都
   当扩展去加载，往那儿堆 .bak 只会让它报错。
2. **关闭 Playnite 用优雅方式**：给主窗口发 WM_CLOSE，等同于点窗口右上角的 ×，
   Playnite 会走正常退出流程（保存库、关掉插件）。**不强杀** —— 强杀会让它丢掉
   还没落盘的库改动。超时没退出就把决定权交回给用户，绝不擅自 /F。
3. **重启用原来那个 exe**（桌面版 / 全屏版），不替用户改启动模式；
   注入前 Playnite 没在跑，就注入完不动它。
"""

import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile

from . import playnite

# ---- 插件身份：只认这几个常量，别再各处散着写字符串 ----
# 这些值必须和 src/PlayniteVault/extension.yaml 完全一致，否则 Playnite 会
# 把插件装到一个「它不认识的 Id」下，等于装了个孤儿。
PLUGIN_ID = "Playnite-Vault"
MODULE_NAME = "PlayniteVault.dll"
PACKAGE_PREFIX = "PlayniteVault-"

# 旧命名：注入成功后把老目录挪走，否则 Playnite 会同时加载新旧两份插件
LEGACY_DIR_PREFIX = "VaultDemo_"

REPO_OWNER = "yi0086"
REPO_NAME = "playnite-vault"
GITHUB_API = "https://api.github.com/repos/{0}/{1}/releases/latest"
GITEE_API = "https://gitee.com/api/v5/repos/{0}/{1}/releases/latest"

# 优雅关闭时最多等多久（秒）。Playnite 退出要保存库，正常几秒，宽裕一点。
CLOSE_TIMEOUT = 60.0


class InjectError(Exception):
    pass


# --------------------------------------------------------------------------
# 定位 Playnite
# --------------------------------------------------------------------------

def extensions_dir_of(root):
    return os.path.join(root, "Extensions")


def find_playnite_roots():
    """候选 Playnite 根目录，按可信度排序。返回 [{"root":..,"source":..,"exists":bool}]"""
    out = []

    def add(root, source):
        if not root:
            return
        key = os.path.normcase(os.path.normpath(root))
        for item in out:
            if os.path.normcase(os.path.normpath(item["root"])) == key:
                return
        out.append({"root": root, "source": source,
                    "exists": os.path.isdir(root)})

    # 1) 正在跑的进程 —— 最准，绝不会认错
    for d in playnite.playnite_install_dirs_from_processes():
        add(d, "正在运行的 Playnite")

    # 2) 注册表 / 状态文件 / 盘符扫描（复用 playnite.py 那套）
    for i, d in enumerate(playnite._playnite_install_dirs()):
        add(d, "自动检测")

    return out


def looks_like_playnite_root(root):
    """这个目录像不像 Playnite 的安装目录？"""
    if not root or not os.path.isdir(root):
        return False
    for name in ("Playnite.DesktopApp.exe", "Playnite.FullscreenApp.exe",
                 "Playnite.exe", "Toolbox.exe"):
        if os.path.isfile(os.path.join(root, name)):
            return True
    # 没 exe 但有 Extensions/ExtensionsData 也算（用户可能只填了数据目录）
    return os.path.isdir(os.path.join(root, "Extensions"))


def pick_root(preferred=None):
    """返回 (root, note)。preferred 是配置里存过的路径，优先用。"""
    if preferred and looks_like_playnite_root(preferred):
        return preferred, "使用已配置的 Playnite 目录"

    cands = find_playnite_roots()
    for item in cands:
        if looks_like_playnite_root(item["root"]):
            return item["root"], item["source"] + "（%s）" % item["root"]

    tried = "\n  ".join(c["root"] for c in cands) or "(没扫到任何候选)"
    return None, ("没找到 Playnite 安装目录。\n"
                  "请在「Playnite 目录」里手动指定 —— 就是有 Playnite.DesktopApp.exe 的"
                  "那个文件夹（便携版就是整个文件夹所在位置）。\n试过这些：\n  " + tried)


# --------------------------------------------------------------------------
# 读已装插件的版本
# --------------------------------------------------------------------------

def read_yaml_value(text, key):
    """从 extension.yaml 文本里抠出某个键（不引 YAML 库，够用就行）。"""
    if not text:
        return None
    for raw in text.split("\n"):
        line = raw.strip()
        if line.lower().startswith(key.lower() + ":"):
            return line.split(":", 1)[1].strip().strip("\"'")
    return None


def installed_info(extensions_dir):
    """Extensions\\Playnite-Vault 里装的是哪个版本。没装返回 None。"""
    d = os.path.join(extensions_dir, PLUGIN_ID)
    yaml = os.path.join(d, "extension.yaml")
    if not os.path.isfile(yaml):
        return None
    try:
        with open(yaml, "r", encoding="utf-8-sig") as f:
            text = f.read()
    except OSError:
        return None
    return {
        "dir": d,
        "id": read_yaml_value(text, "Id"),
        "version": read_yaml_value(text, "Version"),
        "module": read_yaml_value(text, "Module"),
        "has_dll": os.path.isfile(os.path.join(d, MODULE_NAME)),
    }


def legacy_plugin_dirs(extensions_dir):
    """改名之前的那些安装目录 —— 留着会让 Playnite 同时加载新旧两份插件。"""
    out = []
    try:
        names = sorted(os.listdir(extensions_dir))
    except OSError:
        return out
    for name in names:
        if name.startswith(LEGACY_DIR_PREFIX):
            full = os.path.join(extensions_dir, name)
            if os.path.isdir(full):
                out.append(full)
    return out


# --------------------------------------------------------------------------
# 插件包：内置优先，其次从 GitHub / Gitee 下最新 release
# --------------------------------------------------------------------------

def payload_dir():
    """内置插件包所在目录。

    冻结成单文件 exe 后，PyInstaller 把 --add-data 的内容解到 _MEIPASS；
    源码运行时就找 tools/plugin_payload/。两边都找不到就返回 None。
    """
    meipass = getattr(sys, "_MEIPASS", None)
    if meipass:
        d = os.path.join(meipass, "plugin")
        if os.path.isdir(d):
            return d
    here = os.path.dirname(os.path.abspath(__file__))
    d = os.path.join(os.path.dirname(here), "plugin_payload")
    return d if os.path.isdir(d) else None


def bundled_package():
    """内置插件包路径。没有就 None。"""
    d = payload_dir()
    if not d:
        return None
    try:
        names = sorted(os.listdir(d))
    except OSError:
        return None
    for name in names:
        if name.lower().endswith(".zip"):
            return os.path.join(d, name)
    return None


def _opener(use_system_proxy):
    """urllib opener。内网/直连时用空 ProxyHandler，避免被 Clash 之类截走。"""
    if use_system_proxy:
        return urllib.request.build_opener()
    return urllib.request.build_opener(urllib.request.ProxyHandler({}))


def _get(url, use_system_proxy, timeout=20, accept_json=True):
    req = urllib.request.Request(url)
    req.add_header("User-Agent", "PlayniteVault-Unpacker")
    if accept_json:
        req.add_header("Accept", "application/json")
    return _opener(use_system_proxy).open(req, timeout=timeout)


def parse_release_assets(data):
    """从 release JSON 里挑出插件包。返回 [{"name":..,"url":..,"size":..}]"""
    assets = data.get("assets") or []
    out = []
    for a in assets:
        name = (a.get("name") or "")
        if not name.lower().endswith(".zip"):
            continue
        url = a.get("browser_download_url") or a.get("url") or ""
        out.append({"name": name, "url": url, "size": a.get("size") or 0})
    # 精确前缀排前面
    out.sort(key=lambda a: (0 if a["name"].startswith(PACKAGE_PREFIX) else 1,
                            a["name"]))
    return out


def fetch_latest_asset(mirror="auto", log=None):
    """问 GitHub / Gitee 要最新 release 的插件包信息。

    返回 (asset_dict, note)。两个源都失败就抛 InjectError。
    """
    mirrors = []
    if mirror in ("auto", "github"):
        mirrors.append(("github", GITHUB_API.format(REPO_OWNER, REPO_NAME)))
    if mirror in ("auto", "gitee"):
        mirrors.append(("gitee", GITEE_API.format(REPO_OWNER, REPO_NAME)))

    problems = []
    for name, api in mirrors:
        for proxy in (False, True):
            how = "%s/%s" % (name, "系统代理" if proxy else "直连")
            try:
                with _get(api, proxy) as resp:
                    data = json.loads(resp.read().decode("utf-8", "replace"))
                tag = (data.get("tag_name") or data.get("name") or "").strip()
                assets = parse_release_assets(data)
                pick = None
                for a in assets:
                    if a["name"].startswith(PACKAGE_PREFIX):
                        pick = a
                        break
                if pick is None and assets:
                    pick = assets[0]
                if pick is None:
                    problems.append("%s：release 里没有 .zip 附件" % how)
                    continue
                if log:
                    log("[插件包] %s 上最新是 %s" % (how, tag or "(无 tag)"))
                return pick, "%s（%s）" % (how, tag or "最新")
            except urllib.error.HTTPError as ex:
                problems.append("%s：HTTP %s" % (how, ex.code))
            except urllib.error.URLError as ex:
                problems.append("%s：%s" % (how, ex.reason))
            except Exception as ex:
                problems.append("%s：%s" % (how, ex))

    raise InjectError("两个源都取不到 release：\n  " + "\n  ".join(problems))


def download_asset(asset, dest_path, log=None):
    """下载插件包到 dest_path。直连不行就走系统代理。"""
    last = None
    for proxy in (False, True):
        how = "系统代理" if proxy else "直连"
        try:
            with _get(asset["url"], proxy, timeout=60, accept_json=False) as resp, \
                    open(dest_path, "wb") as f:
                total = 0
                while True:
                    buf = resp.read(262144)
                    if not buf:
                        break
                    f.write(buf)
                    total += len(buf)
            if total <= 0:
                raise InjectError("下载下来是 0 字节")
            if log:
                log("[插件包] 下载完成（%s，%.1f KB）" % (how, total / 1024.0))
            return dest_path
        except Exception as ex:
            last = ex
            if log:
                log("[插件包] %s 下载失败：%s" % (how, ex))
    raise InjectError("插件包下载失败：%s" % last)


def fetch_package(dest_dir, source="bundled", mirror="auto", log=None):
    """拿到一个可用的插件包，返回 (zip_path, note)。

    source: bundled = 用 exe 里带的那份；github/gitee/auto = 去网上取最新。
    """
    os.makedirs(dest_dir, exist_ok=True)

    if source == "bundled":
        z = bundled_package()
        if z:
            return z, "内置插件包（%s）" % os.path.basename(z)
        if log:
            log("[插件包] 这个 exe 里没带内置插件包，改为联网取最新。")
        source = "auto"

    asset, note = fetch_latest_asset(mirror, log)
    dest = os.path.join(dest_dir, asset["name"])
    download_asset(asset, dest, log)
    return dest, note


# --------------------------------------------------------------------------
# 注入
# --------------------------------------------------------------------------

def backup_root():
    """旧版本备份放哪儿。**故意不放 Extensions 里** —— Playnite 会把那儿每个
    文件夹都当扩展去加载，堆 .bak 只会让它报一堆加载错误。"""
    base = playnite.state_dir()
    return os.path.join(base, "plugin-backup")


def _stage_zip(zip_path, staging_dir):
    """把 zip 里的插件文件解到 staging_dir（只取一层目录）。返回文件清单 + 元信息。"""
    if os.path.isdir(staging_dir):
        shutil.rmtree(staging_dir, ignore_errors=True)
    os.makedirs(staging_dir)

    files = []
    yaml_text = None
    with zipfile.ZipFile(zip_path) as z:
        for entry in z.infolist():
            name = entry.filename.replace("\\", "/")
            if name.endswith("/"):
                continue
            parts = [p for p in name.split("/") if p not in ("", ".")]
            if any(p == ".." for p in parts):
                raise InjectError("插件包里有不安全的路径：" + entry.filename)
            if len(parts) > 1:
                # 发布包里是 Playnite-Vault/<文件>，只取一层目录里的文件
                parts = parts[-1:]
            leaf = parts[0]
            target = os.path.join(staging_dir, leaf)
            with z.open(entry) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            files.append(leaf)
            if leaf.lower() == "extension.yaml":
                with open(target, "r", encoding="utf-8-sig") as f:
                    yaml_text = f.read()

    # 校验顺序是刻意的：先认「这是不是本插件」，再认「是不是一个完好的包」。
    # 反过来的话，拿旧版（VaultDemo_*）的包来装只会报「缺 PlayniteVault.dll」，
    # 提示完全指错方向。
    if yaml_text is None:
        raise InjectError("插件包里没有 extension.yaml，这不是一个插件包。")

    pid = read_yaml_value(yaml_text, "Id")
    if pid and pid != PLUGIN_ID:
        raise InjectError("插件包的 Id 是 %s，期望 %s —— 装错了包（改名前的旧包？）。"
                          % (pid, PLUGIN_ID))

    if not os.path.isfile(os.path.join(staging_dir, MODULE_NAME)):
        raise InjectError("插件包里没有 %s，这不是一个插件包。" % MODULE_NAME)

    with open(os.path.join(staging_dir, MODULE_NAME), "rb") as f:
        if f.read(2) != b"MZ":
            raise InjectError("%s 不是有效的 PE 文件。" % MODULE_NAME)

    return {
        "files": sorted(files),
        "version": read_yaml_value(yaml_text, "Version"),
        "id": pid or PLUGIN_ID,
    }


def install_package(zip_path, extensions_dir, staging_dir, log=None,
                    move_legacy=True):
    """把插件包装进 Extensions\\Playnite-Vault。

    返回 dict(version, dir, files, backup, legacy_moved)
    · 旧的同名目录 → 挪到 backup_root()/<时间戳>/
    · 改名前的 VaultDemo_* 目录 → 同样挪走（move_legacy=False 则留着）
    任何时候都不会删东西，最差也是「挪到备份」。
    """
    meta = _stage_zip(zip_path, staging_dir)

    os.makedirs(extensions_dir, exist_ok=True)
    target = os.path.join(extensions_dir, PLUGIN_ID)

    stamp = time.strftime("%Y%m%d-%H%M%S")
    backup_dir = os.path.join(backup_root(), stamp)

    old = None
    if os.path.isdir(target):
        old = installed_info(extensions_dir)
        os.makedirs(backup_dir, exist_ok=True)
        dest = os.path.join(backup_dir, PLUGIN_ID)
        shutil.move(target, dest)
        if log:
            log("[注入] 旧版本已备份：%s（版本 %s）"
                % (dest, (old or {}).get("version") or "未知"))

    os.makedirs(target, exist_ok=True)
    for name in meta["files"]:
        shutil.copy2(os.path.join(staging_dir, name), os.path.join(target, name))
    if log:
        log("[注入] 已写入 %s（%d 个文件，版本 %s）"
            % (target, len(meta["files"]), meta["version"]))

    moved = []
    if move_legacy:
        for legacy in legacy_plugin_dirs(extensions_dir):
            os.makedirs(backup_dir, exist_ok=True)
            dest = os.path.join(backup_dir, os.path.basename(legacy))
            try:
                shutil.move(legacy, dest)
                moved.append(os.path.basename(legacy))
                if log:
                    log("[注入] 改名前的旧目录已挪走：%s → %s"
                        % (os.path.basename(legacy), dest))
            except OSError as ex:
                if log:
                    log("[注入] 旧目录挪不动（%s）：%s" % (os.path.basename(legacy), ex))

    return {
        "version": meta["version"],
        "dir": target,
        "files": meta["files"],
        "backup": backup_dir if os.path.isdir(backup_dir) else None,
        "legacy_moved": moved,
    }


# --------------------------------------------------------------------------
# 关闭 / 重启 Playnite
# --------------------------------------------------------------------------

def _top_level_windows(pid):
    """这个进程的可见顶层窗口句柄。关窗口要发给窗口，不是发给进程。"""
    if not sys.platform.startswith("win"):
        return []
    try:
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.WinDLL("user32", use_last_error=True)
        handles = []
        proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        def callback(hwnd, _lparam):
            wpid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
            if wpid.value == pid and user32.IsWindowVisible(hwnd):
                handles.append(hwnd)
            return True

        user32.EnumWindows(proc(callback), 0)
        return handles
    except Exception:
        return []


def request_close(log=None, timeout=CLOSE_TIMEOUT):
    """优雅关闭 Playnite：给主窗口发 WM_CLOSE（＝点右上角的 ×）。

    返回 (state, procs)：
      "not-running" 本来就没在跑
      "closed"      已经干净退出
      "timeout"     还在（可能设了「关闭到托盘」，需要用户决定）
      "unknown"     查不出来
    """
    if not sys.platform.startswith("win"):
        return "unknown", []

    procs = playnite.playnite_processes()
    if procs is None:
        return "unknown", []
    if not procs:
        return "not-running", []

    if log:
        log("[关闭] 正在请 Playnite 退出（优雅关闭，等同点窗口的 ×）…")

    WM_CLOSE = 0x0010
    try:
        import ctypes
        user32 = ctypes.WinDLL("user32", use_last_error=True)
    except Exception:
        return "unknown", procs

    sent = 0
    for proc in procs:
        for hwnd in _top_level_windows(proc["pid"]):
            user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
            sent += 1
    if log:
        log("[关闭] 已向 %d 个窗口发出关闭请求，等它自己退…" % sent)

    deadline = time.time() + max(5.0, timeout)
    while time.time() < deadline:
        left = playnite.playnite_processes()
        if left is not None and not left:
            if log:
                log("[关闭] Playnite 已退出。")
            return "closed", procs
        time.sleep(0.5)

    if log:
        log("[关闭] 等了 %.0f 秒还没退 —— 可能设了「关闭到托盘」或卡住了。" % timeout)
    return "timeout", procs


def launch(exe_path, log=None):
    """把 Playnite 拉起来。用原来那个 exe，不替用户改启动模式。"""
    if not exe_path or not os.path.isfile(exe_path):
        raise InjectError("要重启的 Playnite 找不到：" + str(exe_path))

    flags = 0
    for name in ("DETACHED_PROCESS", "CREATE_NEW_PROCESS_GROUP"):
        flags |= getattr(subprocess, name, 0)

    subprocess.Popen([exe_path], cwd=os.path.dirname(exe_path),
                     creationflags=flags, close_fds=True)
    if log:
        log("[重启] 已重新拉起 " + os.path.basename(exe_path))
    return exe_path
