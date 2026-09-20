#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 ../release/v<版本>/ 里的产物发布成 GitHub + Gitee 的 Release。

用法：
    python tools/publish-release.py 1.6.0 --verify-download
    python tools/publish-release.py 1.6.0 --dry-run
    python tools/publish-release.py 1.6.0 --notes-file notes.md --only github
    python tools/publish-release.py 1.6.0 --force          # 同名资产删掉重传

约定（与仓库历史一致）：
  * 产物目录：仓库外的 ``../release/v<版本>/``，只取**顶层**文件，跳过 ``stage/``。
  * 令牌：``%USERPROFILE%\\.playnite-vault\\github-token.txt`` / ``gitee-token.txt``。
    git push 走 SSH，但 Release / 资产上传只认 token。
  * Gitee 建 release 时带 ``attach_files`` 已失效（返回 201 但资产不挂），
    必须另发 ``POST /releases/{id}/attach_files``，字段名 ``file``。
  * Gitee 重复 attach 同名文件**不会覆盖，会挂成两份**，而下载地址解析到哪一份不可控；
    且 ``/releases/tags/{tag}`` 不回传附件 id，没法按 id 删单个附件。
    所以 Gitee 的 ``--force`` = 整条 release 删掉重建。
  * 重复跑不会重复挂资产：同名已存在就跳过（``--force`` 才删旧重传），
    Release 的标题/正文变化也会同步过去。
  * 回验分两层：先看资产在不在、大小对不对；``--verify-download`` 再**真下载**
    比 sha256 —— Gitee API 不回传大小，只有下载这层骗不了人。

只依赖标准库（本机 managed Python 没装 requests）。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import mimetypes
import os
import random
import re
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# ---------------------------------------------------------------- 基础设置

GITHUB_API = "https://api.github.com"
GITHUB_UPLOAD = "https://uploads.github.com"
GITEE_API = "https://gitee.com/api/v5"
OWNER = "yi0086"
REPO = "playnite-vault"

# GitHub API 直连偶尔被中间设备干扰；需要走代理就自己设这个环境变量，
# 不设就没有代理兜底（仓库里不硬编码任何内网地址）。
DEFAULT_PROXY = os.environ.get("VAULT_HTTPS_PROXY", "").strip()
PROXY_ENV_KEYS = ("HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy")

RETRY_COUNT = 4
RETRY_BASE = 1.5
TIMEOUT = 300

SKIP_NAMES = {"stage", ".gitkeep"}


def log(msg: str) -> None:
    print(msg, flush=True)


def repo_root() -> str:
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def release_dir(version: str) -> str:
    return os.path.join(os.path.dirname(repo_root()), "release", f"v{version}")


# ---------------------------------------------------------------- HTTP 层

def _ssl_context() -> ssl.SSLContext:
    return ssl.create_default_context()


def _opener(proxy: str | None, use_proxy_env: bool = True):
    handlers = []
    if proxy:
        handlers.append(urllib.request.ProxyHandler({"http": proxy, "https": proxy}))
    elif not use_proxy_env:
        handlers.append(urllib.request.ProxyHandler({}))
    handlers.append(urllib.request.HTTPSHandler(context=_ssl_context()))
    return urllib.request.build_opener(*handlers)


def _request(method: str, url: str, *, data=None, headers=None, proxy=None,
             no_proxy_env=False, timeout=TIMEOUT):
    req = urllib.request.Request(url, data=data, method=method)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    opener = _opener(proxy, use_proxy_env=not no_proxy_env)
    return opener.open(req, timeout=timeout)


def _describe(exc: BaseException) -> str:
    if isinstance(exc, urllib.error.HTTPError):
        try:
            body = exc.read().decode("utf-8", "replace")
        except Exception:
            body = ""
        return f"HTTP {exc.code} {exc.reason}: {body[:600]}"
    return f"{type(exc).__name__}: {exc}"


def api(method: str, url: str, *, token: str, auth_scheme="Bearer", json_body=None,
        data=None, headers=None, expect=(200, 201, 204), allow_codes=(),
        retries=RETRY_COUNT, proxy_fallback=True, label=None):
    """带重试与代理兜底的 HTTP 调用。返回 (status, 解析后的 body 或 None)。"""
    hdrs = {"User-Agent": "playnite-vault-publish", "Accept": "application/vnd.github+json"}
    if token:
        hdrs["Authorization"] = f"{auth_scheme} {token}"
    if json_body is not None:
        data = json.dumps(json_body).encode("utf-8")
        hdrs["Content-Type"] = "application/json"
    hdrs.update(headers or {})

    attempt_proxies = [None]
    if proxy_fallback:
        if DEFAULT_PROXY:
            attempt_proxies.append(DEFAULT_PROXY)

    last = None
    for attempt in range(retries):
        for proxy in attempt_proxies:
            try:
                with _request(method, url, data=data, headers=hdrs, proxy=proxy) as resp:
                    raw = resp.read()
                    status = resp.status
                    if status in expect or status in allow_codes:
                        body = None
                        if raw:
                            try:
                                body = json.loads(raw.decode("utf-8"))
                            except Exception:
                                body = raw
                        return status, body
                    last = RuntimeError(f"{label or url} 返回 {status}: {raw[:400]!r}")
            except urllib.error.HTTPError as exc:
                if exc.code in allow_codes:
                    try:
                        body = json.loads(exc.read().decode("utf-8", "replace"))
                    except Exception:
                        body = None
                    return exc.code, body
                last = RuntimeError(f"{label or url} → {_describe(exc)}")
                # 4xx 除 429 外是确定性错误，换代理/重试没意义
                if 400 <= exc.code < 500 and exc.code != 429:
                    raise last
            except Exception as exc:  # 网络类，换代理/重试
                last = RuntimeError(f"{label or url} → {_describe(exc)}")
        if attempt < retries - 1:
            time.sleep(RETRY_BASE ** attempt + random.uniform(0, 0.7))
    raise last if last else RuntimeError(f"{label or url} 失败")


def _multipart(fields: dict, files: list[tuple[str, str, bytes, str]]):
    """files: [(字段名, 文件名, 内容, 内容类型)]；返回 (content_type, body)。"""
    boundary = "----VaultBoundary" + hashlib.sha1(os.urandom(16)).hexdigest()
    out = bytearray()
    for name, value in fields.items():
        out += f"--{boundary}\r\n".encode()
        out += f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode()
        out += str(value).encode("utf-8") + b"\r\n"
    for name, filename, content, ctype in files:
        out += f"--{boundary}\r\n".encode()
        out += (f'Content-Disposition: form-data; name="{name}"; '
                f"filename=\"{filename}\"\r\n").encode("utf-8")
        out += f"Content-Type: {ctype}\r\n\r\n".encode()
        out += content + b"\r\n"
    out += f"--{boundary}--\r\n".encode()
    return f"multipart/form-data; boundary={boundary}", bytes(out)


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ---------------------------------------------------------------- 令牌

def token_path(host: str) -> str:
    home = os.path.expanduser("~")
    return os.path.join(home, ".playnite-vault", f"{host}-token.txt")


def read_token(host: str, override: str | None) -> str:
    if override:
        return override.strip()
    path = token_path(host)
    if not os.path.isfile(path):
        raise SystemExit(f"找不到 {host} 令牌：{path}")
    with open(path, "r", encoding="utf-8") as fh:
        tok = fh.read().strip()
    if not tok:
        raise SystemExit(f"{host} 令牌文件是空的：{path}")
    return tok


# ---------------------------------------------------------------- 资产收集

def collect_assets(version: str, patterns: list[str]) -> list[str]:
    d = release_dir(version)
    if not os.path.isdir(d):
        raise SystemExit(f"产物目录不存在：{d}（先跑 tools/build-release.py {version}）")
    names = []
    for name in sorted(os.listdir(d)):
        full = os.path.join(d, name)
        if not os.path.isfile(full) or name in SKIP_NAMES:
            continue
        if any(re.fullmatch(p, name, re.IGNORECASE) for p in patterns):
            names.append(name)
    return [os.path.join(d, n) for n in names]


# ---------------------------------------------------------------- Release 正文

def auto_changelog(version: str) -> str:
    """取上一个 tag 到 HEAD 的提交信息当正文。"""
    prev = ""
    try:
        tags = subprocess.run(["git", "tag", "--list", "v*", "--sort=-v:refname"],
                              cwd=repo_root(), capture_output=True, text=True,
                              check=True).stdout.split()
        tags = [t for t in tags if t != f"v{version}"]
        prev = tags[0] if tags else ""
        rng = f"{prev}..HEAD" if prev else "HEAD"
        log_out = subprocess.run(["git", "log", "--pretty=format:- %s", rng],
                                 cwd=repo_root(), capture_output=True, text=True,
                                 check=True).stdout.strip()
    except Exception as exc:
        log(f"  ! 生成 changelog 失败（忽略）：{exc}")
        return f"Playnite Vault {version}"
    header = f"Playnite Vault {version}"
    body = log_out or "- 细节见提交记录"
    return f"{header}\n\n{body}\n"


def read_notes(args, version: str) -> tuple[str, str]:
    if args.notes_file:
        with open(args.notes_file, "r", encoding="utf-8") as fh:
            text = fh.read()
    elif args.notes:
        text = args.notes
    else:
        text = auto_changelog(version)
    lines = text.splitlines()
    title = lines[0].strip() if lines else f"Playnite Vault {version}"
    return title, text


# ---------------------------------------------------------------- GitHub

def github_find_release(token: str, tag: str):
    status, body = api("GET", f"{GITHUB_API}/repos/{OWNER}/{REPO}/releases/tags/{tag}",
                       token=token, expect=(200,), allow_codes=(404,),
                       label=f"GitHub 查询 release {tag}")
    return None if status == 404 else body


def github_publish(token: str, tag: str, title: str, body: str, assets: list[str],
                   draft: bool, force: bool) -> dict:
    rel = github_find_release(token, tag)
    if rel is None:
        log(f"  → 新建 GitHub Release {tag}")
        _, rel = api("POST", f"{GITHUB_API}/repos/{OWNER}/{REPO}/releases", token=token,
                     json_body={"tag_name": tag, "name": title, "body": body,
                                "draft": draft, "prerelease": False},
                     expect=(201,), label="GitHub 建 release")
    else:
        log(f"  → GitHub Release {tag} 已存在（id={rel['id']}），复用")
        patch = {}
        if rel.get("name") != title:
            patch["name"] = title
        if (rel.get("body") or "").strip() != body.strip():
            patch["body"] = body
        if not draft and rel.get("draft"):
            patch["draft"] = False
        if patch:
            log(f"  → 同步 Release 信息（{', '.join(sorted(patch))}）")
            _, rel = api("PATCH", f"{GITHUB_API}/repos/{OWNER}/{REPO}/releases/{rel['id']}",
                         token=token, json_body=patch, expect=(200,),
                         label="GitHub 更新 release")

    existing = {a["name"]: a for a in rel.get("assets", [])}
    upload_url = (rel.get("upload_url") or "").split("{")[0]
    if not upload_url:
        upload_url = f"{GITHUB_UPLOAD}/repos/{OWNER}/{REPO}/releases/{rel['id']}/assets"
    else:
        upload_url = upload_url.replace("https://uploads.github.com",
                                        GITHUB_UPLOAD)

    for path in assets:
        name = os.path.basename(path)
        size = os.path.getsize(path)
        if name in existing and not force:
            log(f"    · 跳过已有资产 {name}（{existing[name].get('size')} 字节）")
            continue
        if name in existing and force:
            api("DELETE", f"{GITHUB_API}/repos/{OWNER}/{REPO}/releases/assets/{existing[name]['id']}",
                token=token, expect=(204,), label=f"GitHub 删除旧资产 {name}")
        log(f"    ↑ 上传 {name}（{size} 字节）")
        with open(path, "rb") as fh:
            payload = fh.read()
        ctype = mimetypes.guess_type(name)[0] or "application/octet-stream"
        q = urllib.parse.urlencode({"name": name})
        api("POST", f"{upload_url}?{q}", token=token, headers={"Content-Type": ctype},
            data=payload, expect=(201,), label=f"GitHub 上传 {name}")

    rel = github_find_release(token, tag)
    return rel


# ---------------------------------------------------------------- Gitee

def gitee_url(path: str, token: str | None = None) -> str:
    """Gitee OpenAPI 的鉴权走 access_token 查询参数（头部 token 不一定被认）。"""
    url = f"{GITEE_API}/repos/{OWNER}/{REPO}{path}"
    if token:
        sep = "&" if "?" in url else "?"
        url += f"{sep}access_token={urllib.parse.quote(token)}"
    return url


def gitee_find_release(token: str, tag: str):
    quoted = urllib.parse.quote(tag, safe="")
    try:
        _, body = api("GET", gitee_url(f"/releases/tags/{quoted}", token),
                      token=token, auth_scheme="token", expect=(200,),
                      label=f"Gitee 查询 release {tag}")
        return body
    except RuntimeError as exc:
        if "HTTP 404" in str(exc):
            return None
        raise


def gitee_publish(token: str, tag: str, title: str, body: str, assets: list[str],
                  draft: bool, force: bool) -> dict:
    rel = gitee_find_release(token, tag)

    # Gitee 的 /releases/tags/{tag} **不回传附件 id**，所以没法按 id 删单个附件；
    # 而重复 attach 同名文件不会覆盖 —— 会挂成两份，下载地址解析到哪一份看运气。
    # 因此「覆盖重发」= 把整条 Release 删掉重建（附件随之清空）。
    if rel is not None and force:
        log(f"  → 删掉旧 Gitee Release {tag}（id={rel['id']}）重建，避免同名附件重复挂载")
        api("DELETE", gitee_url(f"/releases/{rel['id']}", token), token="",
            expect=(200, 204), label="Gitee 删除 release")
        rel = None

    if rel is None:
        log(f"  → 新建 Gitee Release {tag}")
        _, rel = api("POST", gitee_url("/releases", token), token="",
                     json_body={"tag_name": tag, "name": title, "body": body,
                                "target_commitish": "main", "prerelease": draft},
                     expect=(201,), label="Gitee 建 release")
    else:
        log(f"  → Gitee Release {tag} 已存在（id={rel['id']}），复用")
        if (rel.get("name") != title) or ((rel.get("body") or "").strip() != body.strip()):
            log("  → 同步 Release 信息（name, body）")
            _, rel = api("PATCH", gitee_url(f"/releases/{rel['id']}", token),
                         token="", json_body={"tag_name": tag, "name": title, "body": body},
                         expect=(200,), label="Gitee 更新 release")

    # Gitee 建 release 时带 attach_files 不可靠，必须逐个 attach_files
    existing = set()
    for a in (rel.get("assets") or []):
        nm = a.get("name") or os.path.basename(a.get("browser_download_url", ""))
        existing.add(nm)

    for path in assets:
        name = os.path.basename(path)
        if name in existing and not force:
            log(f"    · 跳过已有资产 {name}（Gitee 不回传大小，去重用文件名）")
            continue
        with open(path, "rb") as fh:
            payload = fh.read()
        ctype = mimetypes.guess_type(name)[0] or "application/octet-stream"
        ct, mp = _multipart({}, [("file", name, payload, ctype)])
        log(f"    ↑ attach {name}（{len(payload)} 字节）")
        api("POST", gitee_url(f"/releases/{rel['id']}/attach_files", token), token="",
            data=mp, headers={"Content-Type": ct}, expect=(200, 201),
            label=f"Gitee attach {name}")
        time.sleep(0.4)

    rel = gitee_find_release(token, tag)
    return rel


# ---------------------------------------------------------------- 回验

def verify_github(token: str, tag: str, assets: list[str]) -> list[str]:
    rel = github_find_release(token, tag)
    got = {a["name"]: a for a in rel.get("assets", [])}
    problems = []
    for path in assets:
        name = os.path.basename(path)
        local = os.path.getsize(path)
        if name not in got:
            problems.append(f"GitHub 缺资产 {name}")
            continue
        if got[name].get("size") != local:
            problems.append(f"GitHub {name} 大小不符：远端 {got[name].get('size')} ≠ 本地 {local}")
            continue
        log(f"    ✓ GitHub {name} 大小一致（{local} 字节）")
    return problems


def verify_gitee(token: str, tag: str, assets: list[str]) -> list[str]:
    rel = gitee_find_release(token, tag)
    attachments = rel.get("assets") or []
    got = {a.get("name") or os.path.basename(a.get("browser_download_url", "")): a
           for a in attachments}
    problems = []

    # Gitee 的 tags 接口不回传附件 id / 大小，但会如实回传「挂了几份」。
    # 重复挂载时下载地址解析到哪一份是随机的 —— 所以这里把重复本身当错误。
    seen = [a.get("name") or os.path.basename(a.get("browser_download_url", ""))
            for a in attachments]
    for path in assets:
        name = os.path.basename(path)
        if seen.count(name) > 1:
            problems.append(f"Gitee {name} 重复挂了 {seen.count(name)} 份"
                            f"（下载地址会解析到随机一份，必须 --force 重建 release）")

    for path in assets:
        name = os.path.basename(path)
        local = os.path.getsize(path)
        if name not in got:
            problems.append(f"Gitee 缺资产 {name}（实际：{sorted(got) or '无'}）")
            continue
        remote_size = got[name].get("size")
        if isinstance(remote_size, int) and remote_size != local:
            problems.append(f"Gitee {name} 大小不符：远端 {remote_size} ≠ 本地 {local}")
            continue
        log(f"    ✓ Gitee {name} 已挂载（本地 {local} 字节，远端记录 {remote_size}）")
    return problems


def download_sha256(url: str, retries=RETRY_COUNT) -> tuple[str, int]:
    """真的把资产下回来算 sha256。Gitee API 不回传大小，只有下载才骗不了人。"""
    last = None
    for attempt in range(retries):
        for proxy in ([None] + ([DEFAULT_PROXY] if DEFAULT_PROXY else [])):
            try:
                req = urllib.request.Request(url, headers={"User-Agent": "playnite-vault-publish"})
                with _opener(proxy).open(req, timeout=TIMEOUT) as resp:
                    h = hashlib.sha256()
                    size = 0
                    while True:
                        chunk = resp.read(1 << 20)
                        if not chunk:
                            break
                        h.update(chunk)
                        size += len(chunk)
                    return h.hexdigest(), size
            except Exception as exc:
                last = exc
        time.sleep(RETRY_BASE ** attempt)
    raise RuntimeError(f"下载回验失败：{url} → {type(last).__name__}: {last}")


def verify_downloads(tag: str, assets: list[str], which: str,
                     gitee_token: str | None = None) -> list[str]:
    """按平台把资产真的下回来逐字节比对。"""
    problems = []
    gitee_urls = {}
    if which in ("gitee", "both"):
        gitee_urls = {a.get("name"): a.get("browser_download_url")
                      for a in (gitee_find_release(gitee_token, tag).get("assets") or [])}

    for path in assets:
        name = os.path.basename(path)
        local_hash = sha256_file(path)
        local_size = os.path.getsize(path)
        targets = []
        if which in ("github", "both"):
            targets.append(("github",
                            f"https://github.com/{OWNER}/{REPO}/releases/download/{tag}/"
                            + urllib.parse.quote(name)))
        if which in ("gitee", "both") and gitee_urls.get(name):
            targets.append(("gitee", gitee_urls[name]))
        for plat, url in targets:
            try:
                got_hash, got_size = download_sha256(url)
            except Exception as exc:
                problems.append(f"{plat} {name} 下载回验失败：{exc}")
                continue
            if got_hash == local_hash and got_size == local_size:
                log(f"    ✓ {plat} {name} 下载回验一致（{got_size} 字节，sha256 {got_hash[:16]}…）")
            else:
                problems.append(f"{plat} {name} 下载后哈希不符："
                                f"远端 {got_size}/{got_hash[:16]}… ≠ 本地 {local_size}/{local_hash[:16]}…")
    return problems


# ---------------------------------------------------------------- main

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="发布 Playnite Vault 到 GitHub + Gitee")
    ap.add_argument("version", help="版本号，如 1.6.0")
    ap.add_argument("--patterns", default=r".+\.(zip|exe)",
                    help="默认 .zip/.exe（正则，逗号分隔可给多条）")
    ap.add_argument("--notes", default=None, help="Release 正文（首行当标题）")
    ap.add_argument("--notes-file", default=None, help="从文件读正文（首行当标题）")
    ap.add_argument("--only", choices=["github", "gitee", "both"], default="both")
    ap.add_argument("--draft", action="store_true", help="发成草稿")
    ap.add_argument("--force", action="store_true", help="同名资产覆盖重传")
    ap.add_argument("--dry-run", action="store_true", help="只打印计划，不联网")
    ap.add_argument("--skip-verify", action="store_true", help="跳过上传后回验")
    ap.add_argument("--verify-download", action="store_true",
                    help="额外把资产从两个平台真下回来比 sha256（Gitee 不回传大小，只有这个算数）")
    args = ap.parse_args(argv)

    version = args.version.lstrip("v")
    tag = f"v{version}"
    patterns = [p.strip() for p in args.patterns.split(",") if p.strip()]

    assets = collect_assets(version, patterns)
    if not assets:
        raise SystemExit(f"{release_dir(version)} 里没匹配到资产（patterns={patterns}）")
    title, body = read_notes(args, version)

    log(f"版本 {version} / tag {tag}")
    log(f"标题 {title}")
    log("资产：")
    for path in assets:
        log(f"  - {os.path.basename(path)}  {os.path.getsize(path)} 字节  "
            f"sha256={sha256_file(path)[:16]}…")
    if args.dry_run:
        log("\n--dry-run，未联网。")
        return 0

    for key in PROXY_ENV_KEYS:
        if os.environ.pop(key, None):
            log(f"（已清除环境变量 {key}，改用内置代理兜底）")

    problems: list[str] = []
    gh_token = gi_token = None

    if args.only in ("github", "both"):
        log("\n== GitHub ==")
        gh_token = read_token("github", None)
        github_publish(gh_token, tag, title, body, assets, args.draft, args.force)
        if not args.skip_verify:
            problems += verify_github(gh_token, tag, assets)

    if args.only in ("gitee", "both"):
        log("\n== Gitee ==")
        gi_token = read_token("gitee", None)
        gitee_publish(gi_token, tag, title, body, assets, args.draft, args.force)
        if not args.skip_verify:
            problems += verify_gitee(gi_token, tag, assets)

    if args.verify_download:
        log("\n== 下载回验（逐字节）==")
        problems += verify_downloads(tag, assets, args.only, gi_token)

    log("")
    if problems:
        log("回验发现问题：")
        for p in problems:
            log(f"  ✗ {p}")
        return 2
    log("全部完成，资产回验一致。")
    return 0


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    sys.exit(main())
