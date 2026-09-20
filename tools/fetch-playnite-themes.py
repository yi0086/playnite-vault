#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 Playnite 官方主题库里的主题全部下载并装进指定的 Playnite 主题目录。

用途：给「主题同步」功能准备一份真实的测试语料 —— 语料要够杂（桌面/全屏、
纯 xaml 的、带一堆图片/字体的、几十 MB 的），才测得出同步的边界。

数据来源（都是官方公开接口，不需要登录）：
  1. `https://api.playnite.link/api/addons`   全量插件库 JSON（405 条）
     其中 `type` 3 = 桌面主题、4 = 全屏主题
  2. 每条主题的 `installerManifestUrl`       YAML 安装清单（JSON 也兼容）
     里面 `Packages[]` 逐个版本列出 `PackageUrl`
  3. `PackageUrl`                            实际包，`.pthm` 扩展名 = 一个 zip
     解压出来就是主题目录（theme.yaml + xaml + 资源）

装好的目录结构与 Playnite 自己的完全一致 —— 目录名一律取解压后 `theme.yaml` 的
`Id:`（Playnite 认标识就是它，不是清单里的 AddonId）：
    <Themes>\\Desktop\\<theme.yaml 的 Id>\\      （形如 Mythic_e231056c-...）
    <Themes>\\Fullscreen\\<theme.yaml 的 Id>\\
清单/接口给的 AddonId 只用来建临时目录，落地后按 Id 纠正：它有时是**裸 GUID**
（实测 Light Mode），照抄就会得到一个 Playnite 认不出的目录名。

用法：
    python tools/fetch-playnite-themes.py --dest "D:\\Game\\Playnite.bak\\Themes"
    python tools/fetch-playnite-themes.py --dest ... --only Mythic
    python tools/fetch-playnite-themes.py --dest ... --kind desktop --limit 3
    python tools/fetch-playnite-themes.py --dest ... --list        # 只列出，不下载
    python tools/fetch-playnite-themes.py --selftest               # 离线自检，不联网

已装过的主题会按「目录名 / theme.yaml 的 Id」两个角度识别并跳过，所以重跑是幂等的；
`--force` 才会重新下载。改完认主题/定目录名那套逻辑务必跑一次 `--selftest`。

注意：GitHub 的 raw / release 直连在这台机器上很不稳（连接重置），
所以每个请求都「直连试一次 → 再走代理试」。代理地址从环境变量
`VAULT_HTTPS_PROXY` 取（例如 http://<代理主机>:<端口>），不写死在代码里。
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import shutil
import ssl
import sys
import time
import urllib.error
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor

ADDONS_API = "https://api.playnite.link/api/addons"
TYPE_DESKTOP = 3
TYPE_FULLSCREEN = 4

RETRIES = 4
RETRY_BASE = 1.5
TIMEOUT = 180

PROXY = os.environ.get("VAULT_HTTPS_PROXY", "").strip()


def log(msg):
    try:
        print(msg, flush=True)
    except Exception:
        pass


# --------------------------------------------------------------- HTTP

def _open(url, proxy=None, timeout=None):
    handlers = []
    if proxy:
        handlers.append(urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
    handlers.append(urllib.request.HTTPSHandler(context=ssl.create_default_context()))
    req = urllib.request.Request(url, headers={"User-Agent": "vault-theme-fetch"})
    # timeout 一定要走「运行时读全局」而不是默认值绑定：main 里会用 --timeout 改它，
    # 而 Python 的默认参数在函数定义时就固定了，改全局是改不动默认值的。
    return urllib.request.build_opener(*handlers).open(req, timeout=timeout or TIMEOUT)


def fetch(url, *, binary=False, tries=RETRIES, timeout=None):
    """直连优先、失败换代理，整体再重试若干轮。"""
    proxies = [None] + ([PROXY] if PROXY else [])
    last = None
    for attempt in range(tries):
        for proxy in proxies:
            try:
                with _open(url, proxy, timeout) as resp:
                    data = resp.read()
                return data if binary else data.decode("utf-8", "replace")
            except Exception as exc:                      # noqa: BLE001
                last = exc
        if attempt < tries - 1:
            time.sleep(RETRY_BASE ** attempt)
    raise RuntimeError("下载失败 %s（最后一个错误：%s: %s）"
                       % (url, type(last).__name__, last))


# --------------------------------------------------------------- 清单解析

def read_field(text, key):
    """抓形如 `Key: value` 的标量（YAML / JSON 文本通用够用）。"""
    m = re.search(r"^\s*%s\s*:\s*(.+?)\s*$" % re.escape(key), text, re.M)
    return m.group(1).strip().strip("'\"") if m else None


def parse_packages(text):
    """从安装清单里取出 [(版本, 包地址)]。

    清单可能是 YAML 也可能是 JSON；主题一般是：
        AddonId: X
        Packages:
          - Version: 1.2
            PackageUrl: https://.../*.pthm
    但也有老格式直接在顶层给一个 PackageUrl，这里都兜住。
    """
    pairs = []

    stripped = text.lstrip()
    if stripped.startswith("{"):                          # JSON 清单
        try:
            doc = json.loads(text)
        except Exception:
            doc = None
        if isinstance(doc, dict):
            pkgs = doc.get("Packages") or doc.get("packages") or []
            for p in pkgs:
                if isinstance(p, dict):
                    url = p.get("PackageUrl") or p.get("packageUrl")
                    if url:
                        pairs.append((str(p.get("Version") or p.get("version") or "0"), url))
            if not pairs:
                url = doc.get("PackageUrl") or doc.get("packageUrl")
                if url:
                    pairs.append((str(doc.get("Version") or "0"), url))
            return pairs

    current = None
    for line in text.splitlines():
        m = re.match(r"^\s*-\s*Version\s*:\s*(.+?)\s*$", line)
        if m:
            current = m.group(1).strip().strip("'\"")
            continue
        m = re.match(r"^\s*PackageUrl\s*:\s*(\S+)\s*$", line)
        if m:
            url = m.group(1).strip().strip("'\"")
            pairs.append((current or "0", url))

    if not pairs:                                        # 老格式：顶层一个地址
        url = read_field(text, "PackageUrl")
        if url:
            pairs.append((read_field(text, "Version") or "0", url))
    return pairs


def version_key(v):
    parts = re.findall(r"\d+", v or "")
    return [int(p) for p in parts] or [0]


def latest_package(text):
    pairs = parse_packages(text)
    if not pairs:
        return None, None
    best = max(pairs, key=lambda kv: version_key(kv[0]))
    return best


# --------------------------------------------------------------- 主题目录

def extract_pthm(data, target):
    """`.pthm` 就是 zip。有的包第一层还套着同名目录，这里统一压平一层。"""
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        names = z.namelist()
        roots = {n.split("/")[0] for n in names if "/" in n and n.split("/")[0]}
        strip = ""
        # 只有「包内所有条目都在同一个顶层目录下」时才压平（避免误伤正常结构）
        if len(roots) == 1 and not any(n.strip("/") == next(iter(roots)) or
                                       ("/" not in n.strip("/")) for n in names
                                       if n.strip("/") and n.split("/")[0] != next(iter(roots))):
            only = next(iter(roots))
            if all(n.startswith(only + "/") for n in names if n.strip("/")):
                strip = only + "/"
        count = 0
        total = 0
        for info in z.infolist():
            rel = info.filename[len(strip):] if strip and info.filename.startswith(strip) else info.filename
            if not rel.strip("/"):
                continue
            # 防路径穿越。注意 Windows 上 `os.path.isabs("/x")` 是 **False**
            # （ntpath 要求分隔符在索引 >0 处），所以根路径要自己判，别指望 isabs。
            safe = os.path.normpath(rel).replace("\\", "/")
            if (safe == ".." or safe.startswith("../") or safe.startswith("/")
                    or safe[1:2] == ":" or os.path.isabs(safe)):
                continue
            dest = os.path.normpath(os.path.join(target, *safe.split("/")))
            if dest != target and not dest.startswith(target + os.sep):
                continue                                      # 兜底：落点必须还在 target 里
            dest = os.path.join(target, *safe.split("/"))
            if info.is_dir():
                os.makedirs(dest, exist_ok=True)
                continue
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with z.open(info) as src, open(dest, "wb") as fh:
                shutil.copyfileobj(src, fh)
            count += 1
            total += info.file_size
        return count, total


def theme_mode_of(target):
    """读 theme.yaml 里的 Mode，确认它到底是桌面还是全屏主题。"""
    for name in ("theme.yaml", "theme.yml"):
        p = os.path.join(target, name)
        if os.path.isfile(p):
            try:
                with io.open(p, "r", encoding="utf-8-sig", errors="replace") as fh:
                    text = fh.read()
            except OSError:
                continue
            m = re.search(r"^\s*Mode\s*:\s*(\w+)\s*$", text, re.M | re.I)
            if m:
                return m.group(1).strip().capitalize()
    return None


def theme_id_of(target):
    r"""theme.yaml 的顶层 `Id:` —— 唯一权威的主题目录名。

    这里**要求顶格**：`Links:` 之类的子项是缩进的，用 `^\s*Id` 有误取的风险。
    """
    for name in ("theme.yaml", "theme.yml"):
        p = os.path.join(target, name)
        if not os.path.isfile(p):
            continue
        try:
            with io.open(p, "r", encoding="utf-8-sig", errors="replace") as fh:
                text = fh.read()
        except OSError:
            continue
        m = re.search(r"^Id\s*:\s*(.+?)\s*$", text, re.M)
        if m:
            return m.group(1).strip().strip("'\"") or None
    return None


GUID_RE = re.compile(
    r"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")


def theme_key(name):
    """把一个目录名/Id 归一成可比对的键。

    同一个主题可能叫 `Light Mode_cf0a70bd-…`（Playnite 惯例）也可能只叫
    `cf0a70bd-…`（作者上传时偷懒），两者其实是一回事 —— 拿尾部的 GUID 对齐。
    没有 GUID 的名字（`Aniki_Lite` 之类）退回小写全名比对。
    """
    m = GUID_RE.search((name or "").strip())
    return m.group(1).lower() if m else (name or "").strip().lower()


def find_installed(themes_dir, kind, ids):
    """在 `<themes_dir>/<Kind>` 下找这个主题已装的目录，没有则 None。

    `ids` 里可以混着 API 的 addonId、清单的 AddonId、theme.yaml 的 Id —— 只认键
    （见 `theme_key`），所以三种写法指向同一个目录时不会重复下载。
    """
    parent = os.path.join(themes_dir, kind.capitalize())
    if not os.path.isdir(parent):
        return None
    wanted = {theme_key(v) for v in ids if v}
    for name in os.listdir(parent):
        p = os.path.join(parent, name)
        if not os.path.isdir(p):
            continue
        if theme_key(name) in wanted or theme_key(theme_id_of(p) or "") in wanted:
            return p
    return None


def canonicalize_dir(parent, target):
    """把「只有 GUID、没有名字」的目录改成 theme.yaml 的 `Id:`。返回 (目录, 备注或 None)。

    Playnite 装主题时目录名是 `<名字>_<GUID>`。清单/接口偶尔只给一个裸 GUID（实测 Light
    Mode），照抄就得到一个没有名字的目录，跟官方安装器的产物长得不一样。

    反过来说，**目录已经有像样的名字时不动它**。理由不是省事：
    - Playnite 认主题看的是 `theme.yaml` 里的 `Id:`，目录名只是给人看的
      （官方自带主题就叫 `Default`，而它的 Id 是 `Playnite_builtin_DefaultDesktop`）；
    - 仓库那边是**只增不减**的镜像，本地改个名会在 NAS 上永久多留一份同样内容。
    所以只在「名字本身不可用」时才纠。
    """
    if not os.path.isdir(target):
        return target, None
    if not GUID_RE.fullmatch(os.path.basename(target).strip()):
        return target, None                        # 已经有名字了，不动
    canonical = theme_id_of(target)
    if not canonical or GUID_RE.fullmatch(canonical.strip()):
        return target, None                        # theme.yaml 自己也是裸 GUID（如 Helium）
    want = os.path.join(parent, canonical)
    if os.path.isdir(want):
        shutil.rmtree(want)
    shutil.move(target, want)
    return want, "目录名从裸 GUID 改为 theme.yaml 的 Id：%s" % canonical


# --------------------------------------------------------------- 自检

def selftest():
    """离线自检：不联网、不碰真实主题目录，只验「认主题 / 定目录名 / 解包」这套判断。

    这套判断是踩过坑的：Light Mode 的清单里 AddonId 是裸 GUID，照抄就得到一个
    Playnite 认不出的目录名，而且下次 --force 还会多留一份。
    """
    import tempfile

    fails = []

    def check(name, got, want):
        ok = got == want
        print("  %s  %s" % ("PASS" if ok else "FAIL", name))
        if not ok:
            print("        期望 %r，实际 %r" % (want, got))
            fails.append(name)

    guid = "cf0a70bd-1cf6-4fb3-91fa-35f50f1b5913"
    # 特意混入一个缩进的嵌套 Id：只有顶格的才是主题自己的 Id
    yaml_text = ("ThemeApiVersion: 2.6.0\n"
                 "Id: Light Mode_%s\n"
                 "Name: Light Mode\n"
                 "Version: 0.2.0\n"
                 "Links:\n"
                 "    Id: 不该被读到的嵌套 Id\n" % guid)

    tmp = tempfile.mkdtemp(prefix="theme-selftest-")
    try:
        themes_dir = os.path.join(tmp, "Themes")
        parent = os.path.join(themes_dir, "Desktop")
        bare = os.path.join(parent, guid)
        os.makedirs(bare)
        with io.open(os.path.join(bare, "theme.yaml"), "w", encoding="utf-8") as fh:
            fh.write(yaml_text)

        # ---- 认主题
        check("theme.yaml 的 Id 按顶格读",
              theme_id_of(bare), "Light Mode_" + guid)
        check("带前缀名与裸 GUID 是同一个主题",
              theme_key("Light Mode_" + guid), theme_key(guid))
        check("只差末位的两个 GUID 不混同",
              theme_key("8b15c46a-90c2-4fe5-9ebb-1ab25ba7fcb1") ==
              theme_key("8b15c46a-90c2-4fe5-9ebb-1ab25ba7fcb2"), False)
        check("没有 GUID 的名字退回小写全名",
              theme_key("Aniki_Lite"), "aniki_lite")
        check("Mode 能读出来",
              theme_mode_of(bare), None)          # 这份 yaml 故意没写 Mode

        # ---- 找已装目录（决定「会不会重复下载」）
        check("按 API 的裸 GUID 就能找到已装目录",
              find_installed(themes_dir, "desktop", [guid]), bare)
        check("按带前缀的 Id 也能找到",
              find_installed(themes_dir, "desktop", ["Light Mode_" + guid]), bare)
        check("主题不存在时返回 None",
              find_installed(themes_dir, "desktop", ["Nonexistent_x"]), None)

        # ---- 定目录名
        moved, note = canonicalize_dir(parent, bare)
        check("裸 GUID 目录被改名成 theme.yaml 的 Id",
              os.path.basename(moved), "Light Mode_" + guid)
        check("旧目录已经不在", os.path.isdir(bare), False)
        check("改名留下的说明不为空", bool(note), True)
        check("目录名与 theme.yaml 的 Id 一致",
              theme_id_of(moved), os.path.basename(moved))
        check("已经是规范名时不动它", canonicalize_dir(parent, moved)[1], None)

        # ---- 有名字的目录不许被动。
        # 依据：查用户机器上 Playnite 自己装的 61 个主题 —— 24/25 桌面 + 30/36 全屏同时命中
        # API addonId 与 theme.yaml 的 Id；**没有一个只命中 API addonId**；而
        # Anthem / Hero / Player / TrailerLovers 四个只命中 theme.yaml 的 Id（库里已漂移）。
        # 结论：Id 权威，但目录名只是给人看的 → 名字可用就别改，否则 NAS 上白多一份（远端只增不减）。
        named = os.path.join(parent, "GameUpdateStatus_Theme")
        os.makedirs(named)
        with io.open(os.path.join(named, "theme.yaml"), "w", encoding="utf-8") as fh:
            fh.write("Id: GameUpdateStatus\nName: Game Update Status\n")
        check("已经有名字的目录不动它", canonicalize_dir(parent, named)[1], None)
        check("它还在原名下", os.path.isdir(named), True)
        check("也没有按 Id 另建一个目录",
              os.path.isdir(os.path.join(parent, "GameUpdateStatus")), False)

        # ---- 清单解析：取最高版本
        manifest = ("AddonId: %s\n"
                    "Packages:\n"
                    "  - Version: 0.1.0\n"
                    "    PackageUrl: https://example.invalid/light_0_1.pthm\n"
                    "  - Version: 0.2.0\n"
                    "    PackageUrl: https://example.invalid/light_0_2.pthm\n" % guid)
        check("清单里取版本最高的包",
              latest_package(manifest),
              ("0.2.0", "https://example.invalid/light_0_2.pthm"))

        # ---- 解包：包内单顶层目录要压平（顶多一层同名壳）
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as z:
            z.writestr("SoloTheme/theme.yaml", "Id: SoloTheme\n")
            z.writestr("SoloTheme/xaml/desktop.xaml", "<Grid/>")
        out = os.path.join(tmp, "unpacked")
        extract_pthm(buf.getvalue(), out)
        check("包内单顶层目录被压平",
              os.path.isfile(os.path.join(out, "theme.yaml")), True)
        check("压平后子目录跟着下来",
              os.path.isfile(os.path.join(out, "xaml", "desktop.xaml")), True)
        check("壳目录没被留下",
              os.path.isdir(os.path.join(out, "SoloTheme")), False)

        # ---- 解包：顶层不是单一目录时不能乱压
        buf2 = io.BytesIO()
        with zipfile.ZipFile(buf2, "w") as z:
            z.writestr("theme.yaml", "Id: Flat\n")
            z.writestr("xaml/desktop.xaml", "<Grid/>")
        out2 = os.path.join(tmp, "unpacked2")
        extract_pthm(buf2.getvalue(), out2)
        check("本来就是平的不动它",
              os.path.isfile(os.path.join(out2, "theme.yaml")), True)
        check("平的包里 xaml 也在",
              os.path.isfile(os.path.join(out2, "xaml", "desktop.xaml")), True)

        # ---- 解包：防路径穿越（单独一个包 —— 有可疑条目时压平会主动关掉）
        buf3 = io.BytesIO()
        with zipfile.ZipFile(buf3, "w") as z:
            z.writestr("theme.yaml", "Id: Evil\n")
            z.writestr("../escaped.txt", "nope")
            z.writestr("a/../../escaped2.txt", "nope")
            z.writestr("/abs.txt", "nope")
            z.writestr("C:/drive.txt", "nope")
        out3 = os.path.join(tmp, "unpacked3")
        extract_pthm(buf3.getvalue(), out3)
        check("向上穿越的条目被丢弃",
              os.path.exists(os.path.join(tmp, "escaped.txt")), False)
        check("绕一圈再向上的条目被丢弃",
              os.path.exists(os.path.join(tmp, "escaped2.txt")), False)
        check("根路径条目被丢弃",
              os.path.isfile(os.path.join(out3, "abs.txt")), False)
        check("带盘符的条目被丢弃",
              os.path.exists("C:/drive.txt"), False)
        check("正常条目照样落盘",
              os.path.isfile(os.path.join(out3, "theme.yaml")), True)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print("")
    if fails:
        print("self-test 失败 %d 项：%s" % (len(fails), "、".join(fails)))
        return 1
    print("self-test 全部通过（25 项）")
    return 0


# --------------------------------------------------------------- 主流程

def collect(args):
    log("[1/3] 拉官方插件库清单 …")
    raw = fetch(ADDONS_API)
    doc = json.loads(raw)
    items = doc.get("data") if isinstance(doc, dict) else doc
    want = []
    for it in items:
        t = it.get("type")
        if t == TYPE_DESKTOP:
            kind = "desktop"
        elif t == TYPE_FULLSCREEN:
            kind = "fullscreen"
        else:
            continue
        if args.kind != "both" and args.kind != kind:
            continue
        url = it.get("installerManifestUrl")
        if not url:
            continue
        want.append({"name": it.get("name"), "kind": kind,
                     "addonId": it.get("addonId"), "manifest": url,
                     "author": it.get("author")})
    log("      桌面主题 %d / 全屏主题 %d，合计 %d 个候选"
        % (sum(1 for w in want if w["kind"] == "desktop"),
           sum(1 for w in want if w["kind"] == "fullscreen"), len(want)))
    if args.only:
        needle = args.only.lower()
        want = [w for w in want
                if needle in (w["name"] or "").lower()
                or needle in (w["addonId"] or "").lower()]
        log("      按 --only '%s' 过滤后剩 %d 个" % (args.only, len(want)))
    if args.limit:
        want = want[:args.limit]
    return want


def resolve_manifest(entry):
    """取安装清单 → 最新版本与包地址。失败返回 error 文本。"""
    try:
        text = fetch(entry["manifest"])
        ver, url = latest_package(text)
        if not url:
            return entry, None, None, "清单里没有 PackageUrl"
        # 清单里的 AddonId 只当参考（可能是裸 GUID），不覆盖 API 给的 addonId。
        m = re.search(r"^\s*AddonId\s*:\s*(.+?)\s*$", text, re.M)
        entry["manifestId"] = (m.group(1).strip().strip("'\"") if m else None) or None
        return entry, entry["addonId"], (ver, url), None
    except Exception as exc:                              # noqa: BLE001
        return entry, None, None, "%s: %s" % (type(exc).__name__, exc)


def main(argv=None):
    global TIMEOUT
    ap = argparse.ArgumentParser(description="下载 Playnite 官方全部主题")
    ap.add_argument("--dest", help="Playnite 的 Themes 目录，例如 D:\\Game\\Playnite.bak\\Themes")
    ap.add_argument("--kind", choices=["desktop", "fullscreen", "both"], default="both")
    ap.add_argument("--only", help="只处理名称/Id 含该子串的主题")
    ap.add_argument("--limit", type=int, help="只处理前 N 个")
    ap.add_argument("--list", action="store_true", help="只列出，不下载")
    ap.add_argument("--force", action="store_true", help="已存在也重新下载")
    ap.add_argument("--jobs", type=int, default=4, help="清单解析并发（默认 4）")
    ap.add_argument("--workers", type=int, default=4, help="包下载并发（默认 4）")
    ap.add_argument("--report", help="把结果写成 JSON 到这里")
    ap.add_argument("--timeout", type=int, default=TIMEOUT,
                    help="单个请求的超时秒数（默认 %d）。遇到黑洞地址（比如某些自建源）"
                         "会把每一轮重试都拖满，调小它能让失败快点暴露" % TIMEOUT)
    ap.add_argument("--selftest", action="store_true",
                    help="离线自检（不联网、不碰 --dest），验证认主题/定目录名/解包那套判断")
    args = ap.parse_args(argv)
    if args.selftest:
        return selftest()
    TIMEOUT = max(5, args.timeout)
    if not args.dest and not args.list:
        ap.error("必须给 --dest（或用 --list 只看清单）")

    want = collect(args)

    log("[2/3] 解析 %d 份安装清单，找最新版本 …" % len(want))
    resolved, failed = [], []
    with ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        for entry, addon_id, pkg, err in pool.map(resolve_manifest, want):
            if err:
                failed.append({"name": entry["name"], "error": err,
                               "manifest": entry["manifest"]})
            else:
                entry["addonId"] = addon_id
                entry["version"], entry["packageUrl"] = pkg
                resolved.append(entry)
    log("      解析成功 %d 个，失败 %d 个" % (len(resolved), len(failed)))

    if args.list:
        for e in sorted(resolved, key=lambda x: (x["kind"], x["name"] or "")):
            log("  %-11s %-38s v%-9s %s" % (e["kind"], (e["name"] or "")[:38],
                                           e["version"], e["addonId"]))
        for f in failed:
            log("  !! 失败：%s —— %s" % (f["name"], f["error"]))
        if args.report:
            with io.open(args.report, "w", encoding="utf-8") as fh:
                json.dump({"resolved": resolved, "failed": failed}, fh,
                          ensure_ascii=False, indent=1)
        return 0

    themes_dir = os.path.abspath(args.dest)
    os.makedirs(themes_dir, exist_ok=True)
    log("[3/3] 下载并解压到 %s" % themes_dir)

    def work(entry):
        kind = entry["kind"]
        parent = os.path.join(themes_dir, kind.capitalize())
        # 已装目录可能叫 API 的 addonId、清单的 AddonId，或 theme.yaml 的 Id —— 三种都认
        found = find_installed(themes_dir, kind,
                               [entry.get("addonId"), entry.get("manifestId")])
        if found and not args.force:
            n = sum(len(f) for _, _, f in os.walk(found))
            return entry, "skip", n, 0, None
        target = os.path.join(parent, entry["addonId"])
        try:
            data = fetch(entry["packageUrl"], binary=True)
            for stale in {found, target} - {None}:         # --force 时两种旧名都可能残留
                if os.path.isdir(stale):
                    shutil.rmtree(stale)
            os.makedirs(parent, exist_ok=True)
            n, size = extract_pthm(data, target)
            note = None
            real = theme_mode_of(target)
            if real and real.lower() != kind:
                note = "theme.yaml 说的 Mode=%s，与库里的类型(%s)不一致" % (real, kind)
            _dir, fix = canonicalize_dir(parent, target)
            if fix:
                note = ((note + "；") if note else "") + fix
            return entry, "ok", n, size, note
        except Exception as exc:                          # noqa: BLE001
            return entry, "fail", 0, 0, "%s: %s" % (type(exc).__name__, exc)

    done, skipped, bad = [], [], []
    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        for entry, status, files, size, note in pool.map(work, resolved):
            label = "%-38s" % (entry["name"] or "")[:38]
            if status == "skip":
                skipped.append(entry)
                log("  = %s 已有（%d 个文件），跳过" % (label, files))
            elif status == "ok":
                done.append(entry)
                log("  + %s %-9s v%-8s %4d 文件 %8.1f MB%s"
                    % (label, entry["kind"], entry["version"], files,
                       size / 1048576.0, ("  [" + note + "]") if note else ""))
            else:
                entry["error"] = note
                bad.append(entry)
                log("  !! %s 失败：%s" % (label, note))

    log("")
    log("新增 %d 个 / 跳过 %d 个 / 失败 %d 个" % (len(done), len(skipped), len(bad)))
    for f in failed:
        log("  !! 清单解析失败：%s —— %s" % (f["name"], f["error"]))

    for kind in ("Desktop", "Fullscreen"):
        d = os.path.join(themes_dir, kind)
        if os.path.isdir(d):
            names = sorted(n for n in os.listdir(d) if os.path.isdir(os.path.join(d, n)))
            total = 0
            for n in names:
                for root, _, files in os.walk(os.path.join(d, n)):
                    for f in files:
                        try:
                            total += os.path.getsize(os.path.join(root, f))
                        except OSError:
                            pass
            log("  [%s] %d 个主题，%.1f MB" % (kind, len(names), total / 1048576.0))

    if args.report:
        with io.open(args.report, "w", encoding="utf-8") as fh:
            json.dump({"dest": themes_dir, "installed": done, "skipped": skipped,
                       "failed": bad, "manifestFailed": failed}, fh,
                      ensure_ascii=False, indent=1)
        log("报告：" + args.report)

    return 1 if (bad or failed) else 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    sys.exit(main())
