# -*- coding: utf-8 -*-
"""
把解包结果登记到 Playnite 的 Vault 插件数据目录。

原理（读插件源码得来，见 VaultService / VaultPlugin）：
Playnite 里那条游戏「是否已安装」由插件数据目录下的 local-index.json 决定 ——
VaultPlugin.BuildGameMetadata() 里：

    var entry = local.Get(app.Id);
    var installed = entry != null && Directory.Exists(entry.InstallDir);

所以只要把 LocalEntry 写进 local-index.json，并且 InstallDir 真实存在，
下次库刷新时这条游戏就会显示成已安装、启动按钮直接指向我们解出来的 exe。

要注意的：
  · 这只登记「已安装」。游戏条目本身要由 Playnite 的库更新（GetGames）来导入 ——
    需要你在 Playnite 里点一次「右键游戏 → Vault → 从 NAS 刷新库条目」，
    或者打开设置里的「启动时更新游戏库」后重启一次。
  · local-index.json 的格式必须与 Newtonsoft 输出一致（PascalCase、缩进 2 空格、
    InstalledAt 用 UTC 带 7 位小数 + Z），否则插件反序列化会拿到空对象。
"""

import glob
import json
import os
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone

PLUGIN_DIR_PREFIX = "VaultDemo_"
SETTINGS_NAME = "settings.json"
LOCAL_INDEX_NAME = "local-index.json"
CACHE_INDEX_NAME = "cache-index.json"
DEFAULT_GUID = "5e76bf50-cb8a-4a87-ad24-1912c746c6f0"

# 记住上次用过的插件数据目录，方便重复使用
_STATE_DIR = os.path.join(
    os.environ.get("APPDATA") or os.path.expanduser("~"), "VaultUnpacker")


class PlayniteError(Exception):
    pass


# --------------------------------------------------------------------------
# 定位插件数据目录
# --------------------------------------------------------------------------

def _dirs_in(path):
    try:
        return sorted(e.name for e in os.scandir(path) if e.is_dir())
    except OSError:
        return []


def _dir_of(value):
    """把注册表里那种 InstallLocation / UninstallString 的值收拾成目录。"""
    if not value:
        return None
    v = value.strip().strip('"')
    if v.lower().endswith(".exe"):
        v = os.path.dirname(v)
    if os.path.isdir(v):
        return v
    return None


# 盘符扫描时跳过的大目录：Playnite 不会装在里面（安装版有注册表覆盖）
_SCAN_SKIP = {
    "$recycle.bin", "windows", "system volume information", "programdata",
    "program files", "program files (x86)", "users", "appdata",
    "$windows.~bt", "$windows.~ws", "recovery", "msocache", "perflogs",
    "node_modules", ".git", "temp", "tmp",
}

_scan_cache = None

# 扫描总时间预算（秒）。盘符再多也不能让界面卡住。
_SCAN_BUDGET = 3.0


def _scannable_roots():
    """只返回「本地盘」的根 —— 关键是把映射网络驱动器排除掉。

    实测：一台接了多个映射网络盘的机器上，挨个 os.scandir 要跑十几秒，
    而 Playnite 基本不会装在网络盘上（真要装也能手填路径）。
    """
    try:
        import ctypes
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.GetLogicalDrives.restype = ctypes.c_uint
        mask = k32.GetLogicalDrives()
        roots = []
        for i in range(26):
            if not (mask >> i) & 1:
                continue
            base = chr(ord("A") + i) + ":\\"
            # 2 = 可移动盘，3 = 固定盘；4 网络盘、5 光驱一律跳过
            if k32.GetDriveTypeW(ctypes.c_wchar_p(base)) in (2, 3):
                roots.append(base)
        if roots:
            return roots
    except Exception:
        pass

    # 兜底：常见的本地盘符
    return [d + ":\\" for d in "CDEFGH" if os.path.isdir(d + ":\\")]


def _scan_drives_for_playnite():
    """便携版 Playnite 就是解压到某个文件夹，ExtensionsData 就躺在它旁边。

    逐盘扫到「盘根 + 一级子目录」，找名叫 Playnite（或 Playnite*）的目录。
    精确名排在前面，免得先撞上 Playnite.bak / Playnite_旧版 之类的备份。
    """
    global _scan_cache
    if _scan_cache is not None:
        return _scan_cache

    deadline = time.time() + _SCAN_BUDGET
    exact, fuzzy = [], []

    for base in _scannable_roots():
        if time.time() > deadline:
            break
        for name in _dirs_in(base):
            if time.time() > deadline:
                break
            low = name.lower()
            if low in _SCAN_SKIP:
                continue
            if low == "playnite":
                exact.append(os.path.join(base, name))
            elif low.startswith("playnite"):
                fuzzy.append(os.path.join(base, name))
            # 盘根下一级，例如 D:\Games\Playnite
            sub = os.path.join(base, name)
            for s2 in _dirs_in(sub):
                l2 = s2.lower()
                if l2 == "playnite":
                    exact.append(os.path.join(sub, s2))
                elif l2.startswith("playnite"):
                    fuzzy.append(os.path.join(sub, s2))

    _scan_cache = exact + fuzzy
    return _scan_cache


def _pids_by_name(exe_name):
    """按进程名拿 PID，用 Win32 进程快照。

    为什么不直接用 tasklist：本机是中文 Windows，tasklist 输出是 GBK 而不是
    UTF-8，`text=True` 会直接抛 UnicodeDecodeError —— 那个异常被 try 吞掉后，
    函数就会把「Playnite 正在运行」误报成「未运行」，进而允许你在 Playnite
    开着的时候写 local-index.json，等它退出时又把我们的条目覆盖掉。

    返回：None = 查不出来；[] = 没有这个进程；[pid, ...] = 找到了。
    """
    if not sys.platform.startswith("win"):
        return None
    try:
        import ctypes
        from ctypes import wintypes

        TH32CS_SNAPPROCESS = 0x00000002
        INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

        class PROCESSENTRY32W(ctypes.Structure):
            _fields_ = [
                ("dwSize", wintypes.DWORD),
                ("cntUsage", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                ("th32ModuleID", wintypes.DWORD),
                ("cntThreads", wintypes.DWORD),
                ("th32ParentProcessID", wintypes.DWORD),
                ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", wintypes.DWORD),
                ("szExeFile", wintypes.WCHAR * 260),
            ]

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
        k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
        k32.Process32FirstW.argtypes = [wintypes.HANDLE,
                                        ctypes.POINTER(PROCESSENTRY32W)]
        k32.Process32NextW.argtypes = [wintypes.HANDLE,
                                       ctypes.POINTER(PROCESSENTRY32W)]

        snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
        if not snap or snap == INVALID_HANDLE_VALUE:
            return None
        pids = []
        try:
            entry = PROCESSENTRY32W()
            entry.dwSize = ctypes.sizeof(PROCESSENTRY32W)
            if not k32.Process32FirstW(snap, ctypes.byref(entry)):
                return None
            target = exe_name.lower()
            while True:
                if entry.szExeFile.lower() == target:
                    pids.append(int(entry.th32ProcessID))
                if not k32.Process32NextW(snap, ctypes.byref(entry)):
                    break
        finally:
            k32.CloseHandle(snap)
        return pids
    except Exception:
        return None


def _pids_via_tasklist(exe_name):
    """备用方案。注意必须给 errors='replace'，否则中文 Windows 上会解码失败。"""
    try:
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) or 0x08000000
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq %s" % exe_name, "/FO", "CSV", "/NH"],
            capture_output=True, text=True, errors="replace",
            creationflags=flags, timeout=10)
        text = (out.stdout or "") + (out.stderr or "")
        pids = []
        for line in text.splitlines():
            parts = [p.strip('"') for p in line.split('","')]
            if len(parts) > 1 and parts[0].lower() == exe_name.lower():
                try:
                    pids.append(int(parts[1]))
                except ValueError:
                    pass
        return pids
    except Exception:
        return None


def _pids_of(exe_name):
    pids = _pids_by_name(exe_name)
    if pids is None:
        pids = _pids_via_tasklist(exe_name)
    return pids


def _running_playnite_exe():
    """正在跑的 Playnite.exe 的完整路径（顺带能挖出便携版放在哪）。"""
    pids = _pids_of("Playnite.exe")
    if not pids:
        return None

    try:
        import ctypes
        from ctypes import wintypes
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.QueryFullProcessImageNameW.argtypes = [
            wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR,
            ctypes.POINTER(wintypes.DWORD)]
        for pid in pids:
            h = k32.OpenProcess(0x1000, False, pid)  # QUERY_LIMITED_INFORMATION
            if not h:
                continue
            try:
                buf = ctypes.create_unicode_buffer(1024)
                n = wintypes.DWORD(1024)
                if k32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(n)):
                    return buf.value
            finally:
                k32.CloseHandle(h)
    except Exception:
        pass
    return None


def _playnite_install_dirs():
    """猜 Playnite 的安装目录。顺序即优先级。"""
    out = []

    # 1) 正在运行的进程 —— 最准
    exe = _running_playnite_exe()
    if exe:
        out.append(os.path.dirname(exe))

    # 2) 注册表（安装版）
    try:
        import winreg
        for hive, key in (
            (winreg.HKEY_CURRENT_USER,
             r"Software\Microsoft\Windows\CurrentVersion\Uninstall\Playnite"),
            (winreg.HKEY_LOCAL_MACHINE,
             r"Software\Microsoft\Windows\CurrentVersion\Uninstall\Playnite"),
            (winreg.HKEY_CURRENT_USER, r"Software\Playnite"),
        ):
            try:
                with winreg.OpenKey(hive, key) as k:
                    for value in ("InstallLocation", "InstallDir", "Path",
                                  "UninstallString"):
                        try:
                            loc = _dir_of(winreg.QueryValueEx(k, value)[0])
                        except OSError:
                            continue
                        if loc:
                            out.append(loc)
            except OSError:
                pass
    except Exception:
        pass

    # 3) 状态文件里记过的插件目录 → 反推
    last = _load_state().get("plugin_data_dir")
    if last:
        out.append(os.path.dirname(last))

    # 4) 逐个盘符找便携版
    out.extend(_scan_drives_for_playnite())

    seen, uniq = set(), []
    for d in out:
        if not d:
            continue
        key = os.path.normcase(os.path.normpath(d))
        if key not in seen:
            seen.add(key)
            uniq.append(d)
    return uniq


def _candidate_roots():
    """所有可能装着插件数据的 ExtensionsData 目录。"""
    roots = []
    for env in ("LOCALAPPDATA", "APPDATA"):
        base = os.environ.get(env)
        if base:
            roots.append(os.path.join(base, "Playnite", "ExtensionsData"))
    for d in _playnite_install_dirs():
        roots.append(os.path.join(d, "ExtensionsData"))

    seen, out = set(), []
    for r in roots:
        key = os.path.normcase(os.path.normpath(r))
        if key not in seen:
            seen.add(key)
            out.append(r)
    return out


def _matches_plugin(path):
    """内容判定：这个目录是不是 Vault 插件的数据目录。

    为什么不用名字判断：Playnite 给扩展数据目录用的是**纯 GUID**
    （ExtensionsData\\5e76bf50-...），不是 VaultDemo_<GUID>。
    所以只能看内容 —— local-index.json / cache-index.json 是本插件独有的，
    settings.json 里带 LocalRoot / WebDavUrl 的也算。
    """
    if not path or not os.path.isdir(path):
        return False
    for name in (LOCAL_INDEX_NAME, CACHE_INDEX_NAME):
        if os.path.isfile(os.path.join(path, name)):
            return True
    st = os.path.join(path, SETTINGS_NAME)
    if os.path.isfile(st):
        try:
            with open(st, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, dict) and ("LocalRoot" in data or "WebDavUrl" in data):
                return True
        except Exception:
            pass
    return False


# 老名字，保留给已有调用
def _looks_like_plugin_dir(path):
    return _matches_plugin(path)


def find_plugin_data_dir():
    """
    返回 (目录, 说明)。找不到就返回 (None, 原因)。

    注意：Playnite 给扩展数据目录用的是**纯 GUID** 命名
    （ExtensionsData\\5e76bf50-cb8a-4a87-ad24-1912c746c6f0），
    所以最后一定要落到「按内容判定」上，光看名字会漏。
    """
    tried = []
    for root in _candidate_roots():
        if not os.path.isdir(root):
            continue
        tried.append(root)

        # 1) 直接命中已知名字（VaultDemo_<GUID> / <GUID>）
        for name in (PLUGIN_DIR_PREFIX + DEFAULT_GUID, DEFAULT_GUID):
            full = os.path.join(root, name)
            if _matches_plugin(full):
                return full, "名字命中：%s" % full

        names = _dirs_in(root)

        # 2) VaultDemo_ 前缀（老命名）
        for name in names:
            if name.startswith(PLUGIN_DIR_PREFIX):
                full = os.path.join(root, name)
                if _matches_plugin(full):
                    return full, "名字命中：%s" % full

        # 3) 全量内容匹配（Playnite 实际用的是纯 GUID）
        for name in names:
            full = os.path.join(root, name)
            if _matches_plugin(full):
                return full, "内容命中：%s" % full

        # 4) ExtensionsData 本身就是插件目录（便携/手工布局）
        if _matches_plugin(root):
            return root, "目录本身就是：%s" % root

    return None, ("没找到 Vault 插件的数据目录。\n"
                  "请在「Playnite 登记」里手动指定 —— 一般是\n"
                  "  <Playnite 安装目录>\\ExtensionsData\\<GUID>\n"
                  "（Playnite 便携版整个文件夹在哪就填哪个盘/目录）\n"
                  "试过这些位置：\n  " + ("\n  ".join(tried) if tried else "(无)"))


def looks_like_plugin_data_dir(path):
    """校验用户手填的路径。允许填 ExtensionsData 或 Playnite 根目录，自动往下找。"""
    if not path or not os.path.isdir(path):
        return None
    if _matches_plugin(path):
        return path
    # 填了 ExtensionsData（或任何上一层）
    for name in _dirs_in(path):
        full = os.path.join(path, name)
        if _matches_plugin(full):
            return full
    # 填了 Playnite 根目录
    ed = os.path.join(path, "ExtensionsData")
    if os.path.isdir(ed):
        if _matches_plugin(ed):
            return ed
        for name in _dirs_in(ed):
            full = os.path.join(ed, name)
            if _matches_plugin(full):
                return full
    return None


# --------------------------------------------------------------------------
# 读插件设置（用来预填仓库地址等）
# --------------------------------------------------------------------------

def read_plugin_settings(plugin_dir):
    p = os.path.join(plugin_dir, SETTINGS_NAME)
    if not os.path.isfile(p):
        return {}
    try:
        with open(p, "r", encoding="utf-8-sig") as f:
            return json.load(f) or {}
    except Exception:
        return {}


# --------------------------------------------------------------------------
# local-index.json 读写
# --------------------------------------------------------------------------

def _now_iso_utc():
    """和 Newtonsoft 一致：2026-09-17T18:24:38.1171714Z（7 位小数）。"""
    now = datetime.now(timezone.utc)
    return "%s.%06d0Z" % (now.strftime("%Y-%m-%dT%H:%M:%S"), now.microsecond)


def read_local_index(plugin_dir):
    p = os.path.join(plugin_dir, LOCAL_INDEX_NAME)
    if not os.path.isfile(p):
        return {"Apps": []}
    try:
        with open(p, "r", encoding="utf-8-sig") as f:
            data = json.load(f) or {}
    except Exception as ex:
        raise PlayniteError("local-index.json 解析失败（%s）。\n"
                            "为避免覆盖有用数据，这里不自动修复，请先手工检查：%s" % (ex, p))
    if not isinstance(data.get("Apps"), list):
        data["Apps"] = []
    return data


def find_entry(index, app_id):
    for e in index.get("Apps") or []:
        if str(e.get("AppId", "")).lower() == str(app_id).lower():
            return e
    return None


def register_install(plugin_dir, app_id, install_dir, version, launch_exe,
                     dry_run=False):
    """
    把一条安装记录写进 local-index.json（存在则覆盖）。返回要写入的 entry。
    写入前会自动备份一份 .bak。
    """
    install_dir = os.path.abspath(install_dir)
    if not os.path.isdir(install_dir):
        raise PlayniteError("安装目录不存在，无法登记：%s" % install_dir)

    index = read_local_index(plugin_dir)
    entry = {
        "AppId": app_id,
        "InstallDir": install_dir,
        "Version": version or "",
        "LaunchExe": launch_exe or "",
        "InstalledAt": _now_iso_utc(),
    }

    old = find_entry(index, app_id)
    apps = [e for e in (index.get("Apps") or [])
            if str(e.get("AppId", "")).lower() != str(app_id).lower()]
    apps.append(entry)
    index["Apps"] = apps

    if dry_run:
        return entry

    target = os.path.join(plugin_dir, LOCAL_INDEX_NAME)
    if os.path.isfile(target):
        try:
            shutil.copy2(target, target + ".bak")
        except OSError:
            pass

    tmp = target + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(index, f, ensure_ascii=False, indent=2)
        f.write("\n")
    os.replace(tmp, target)

    entry["_replaced"] = old is not None
    return entry


def unregister_install(plugin_dir, app_id, dry_run=False):
    index = read_local_index(plugin_dir)
    before = len(index.get("Apps") or [])
    index["Apps"] = [e for e in (index.get("Apps") or [])
                     if str(e.get("AppId", "")).lower() != str(app_id).lower()]
    removed = before - len(index["Apps"])
    if dry_run or not removed:
        return removed
    target = os.path.join(plugin_dir, LOCAL_INDEX_NAME)
    if os.path.isfile(target):
        try:
            shutil.copy2(target, target + ".bak")
        except OSError:
            pass
    tmp = target + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(index, f, ensure_ascii=False, indent=2)
        f.write("\n")
    os.replace(tmp, target)
    return removed


def show_windows_paths():
    """把有安装记录的应用列出来，用于界面展示。"""
    plugin_dir, _note = find_plugin_data_dir()
    if not plugin_dir:
        return []
    try:
        index = read_local_index(plugin_dir)
    except PlayniteError:
        return []
    out = []
    for e in index.get("Apps") or []:
        d = e.get("InstallDir") or ""
        out.append({
            "app_id": e.get("AppId"),
            "dir": d,
            "exists": bool(d) and os.path.isdir(d),
            "version": e.get("Version") or "",
        })
    return out


# --------------------------------------------------------------------------
# 运行状态 / 状态文件
# --------------------------------------------------------------------------

def is_playnite_running():
    """
    检测 Playnite 主进程。
    返回 True / False / None（None 表示查不出来，调用方应当按「不确定」处理并提示用户）。
    """
    if not sys.platform.startswith("win"):
        return None
    pids = _pids_of("Playnite.exe")
    if pids is None:
        return None
    return bool(pids)


def _state_file():
    return os.path.join(_STATE_DIR, "state.json")


def _load_state():
    try:
        with open(_state_file(), "r", encoding="utf-8") as f:
            return json.load(f) or {}
    except Exception:
        return {}


def save_state(**kw):
    try:
        os.makedirs(_STATE_DIR, exist_ok=True)
        data = _load_state()
        data.update(kw)
        with open(_state_file(), "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def load_state():
    return _load_state()


def default_out_root():
    r"""默认解包到哪里：优先插件设置里的 LocalRoot，否则用 <用户>\VaultApps。"""
    plugin_dir, _ = find_plugin_data_dir()
    if plugin_dir:
        s = read_plugin_settings(plugin_dir)
        root = s.get("LocalRoot")
        if root and os.path.isdir(root):
            return root
    return os.path.join(os.path.expanduser("~"), "VaultApps")
