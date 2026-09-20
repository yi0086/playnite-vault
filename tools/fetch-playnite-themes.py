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

装好的目录结构与 Playnite 自己的完全一致：
    <Themes>\\Desktop\\<AddonId>\\      （AddonId 形如 Mythic_e231056c-...）
    <Themes>\\Fullscreen\\<AddonId>\\

用法：
    python tools/fetch-playnite-themes.py --dest "D:\\Game\\Playnite.bak\\Themes"
    python tools/fetch-playnite-themes.py --dest ... --only Mythic
    python tools/fetch-playnite-themes.py --dest ... --kind desktop --limit 3
    python tools/fetch-playnite-themes.py --dest ... --list        # 只列出，不下载

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

def _open(url, proxy=None, timeout=TIMEOUT):
    handlers = []
    if proxy:
        handlers.append(urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
    handlers.append(urllib.request.HTTPSHandler(context=ssl.create_default_context()))
    req = urllib.request.Request(url, headers={"User-Agent": "vault-theme-fetch"})
    return urllib.request.build_opener(*handlers).open(req, timeout=timeout)


def fetch(url, *, binary=False, tries=RETRIES, timeout=TIMEOUT):
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

def installed_ids(themes_dir, kind):
    d = os.path.join(themes_dir, kind)
    if not os.path.isdir(d):
        return set()
    return {n for n in os.listdir(d) if os.path.isdir(os.path.join(d, n))}


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
            # 防路径穿越
            safe = os.path.normpath(rel).replace("\\", "/")
            if safe.startswith("..") or os.path.isabs(safe):
                continue
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
        m = re.search(r"^\s*AddonId\s*:\s*(.+?)\s*$", text, re.M)
        addon_id = (m.group(1).strip().strip("'\"") if m else None) or entry["addonId"]
        return entry, addon_id, (ver, url), None
    except Exception as exc:                              # noqa: BLE001
        return entry, None, None, "%s: %s" % (type(exc).__name__, exc)


def main(argv=None):
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
    args = ap.parse_args(argv)
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
        target = os.path.join(themes_dir, kind.capitalize(), entry["addonId"])
        if os.path.isdir(target) and not args.force:
            n = sum(len(f) for _, _, f in os.walk(target))
            return entry, "skip", n, 0, None
        try:
            data = fetch(entry["packageUrl"], binary=True)
            if os.path.isdir(target):
                shutil.rmtree(target)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            n, size = extract_pthm(data, target)
            real = theme_mode_of(target)
            note = None
            if real and real.lower() != kind:
                note = "theme.yaml 说的 Mode=%s，与库里的类型(%s)不一致" % (real, kind)
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
