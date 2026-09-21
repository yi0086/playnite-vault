#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 ../release/v<版本>/ 里的产物发布成 GitHub + Gitee 的 Release。

用法：
    python tools/publish-release.py 1.8.1 --notes-file notes.md --verify-download
    python tools/publish-release.py 1.8.1 --dry-run
    python tools/publish-release.py 1.8.1 --notes-file notes.md --only github
    python tools/publish-release.py 1.8.1 --tag-message "一句话摘要"
    python tools/publish-release.py 1.8.1 --force          # 同名资产删掉重传 + 重建 tag
    python tools/publish-release.py 1.6.0 --retitle "Vault 1.6.0"   # 只改标题，不动正文
    python tools/publish-release.py 1.8.0 --delete                  # 删 release + tag

约定（与仓库历史一致）：
  * 产物目录：仓库外的 ``../release/v<版本>/``，只取**顶层**文件，跳过 ``stage/``。
  * **tag 由本脚本先建**（``ensure_annotated_tag``）：GitHub 的 release API 只给
    ``tag_name`` 时它会替你建**轻量 tag**，而 Gitee 建的是**附注 tag** —— 两边会不一致。
    所以先在本地建好附注 tag 再推上去，API 看到同名 tag 就直接复用。``--tag-message``
    给一句摘要（正式 tag 的说明就一句话，别把整篇发布说明塞进去）；``--force`` 会重建
    tag（挪到 HEAD / 修正 tagger 与说明）；``--skip-tag`` 则完全不碰 git。
  * **标题 = 正文首行，且必须是 ``Vault X.Y.Z``**（后面不接任何说明）。首行写成
    ``# Vault X.Y.Z`` 也行，脚本会把 ``#`` 去掉 —— 历史上 v1.7.0/v1.8.0 就是把 ``#``
    连同说明一起带进了标题字段。
  * **发布正文只写两样**：逐条要点 + 一段引导（注意事项 / 适用边界）。要点**不要**再套一个
    「要点」标题，直接列；**每条以 ``[类型]`` 开头**（``[bugfix]`` / ``[feature]`` /
    ``[ui]`` / ``[perf]`` / ``[docs]`` / ``[breaking]`` …）。类型名旁边的 emoji、
    以及「同一类型有多条就做成二级列表」，都由 ``_render_updates`` 生成 ——
    作者不要自己打 emoji，也别自己起小标题。
    **别重复 README 里已有的项目介绍 / 适用边界 / 许可**。只有当用户升级时必须自己动手
    （破坏性变更）才写一段「注意事项」，达不到就不写。
  * **正文别再写页脚**。``---`` 之后的「完整改动见 CHANGELOG」「装好之后怎么用见…」
    由脚本生成：作者手写的那两行会被丢掉，脚本自己拼出
    ``使用说明见 docs/plugin-usage.md``（域名按平台换）。CHANGELOG 那行已彻底去掉。
  * **下载区由脚本拼**（``release_body_for``）：加 ``## 下载`` 标题、**不折叠**、放正文最上；
    紧随其后是 ``## 更新`` + 分组后的要点。正文里写的 GitHub 链接会自动改写成目标平台的
    域名，所以正文里只写 GitHub 那份就够。
  * ``--retitle`` 只改标题（修正历史 release 的命名），``--delete`` 撤掉整条 release 与 tag。
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
    """返回 (标题, 正文)。**首行是标题**，正文从第二行起 —— 标题不会再在正文里重复一遍。

    标题会被去掉前导的 ``#``：写成 ``# Vault 1.8.1`` 是允许的，但贴进 release 的「标题」
    字段时不能带 ``#``。历史上 v1.7.0（``# Vault 1.7.0``）与 v1.8.0（``# v1.8.0 —— …``）
    就是没去掉才走样的。
    """
    if args.notes_file:
        with open(args.notes_file, "r", encoding="utf-8") as fh:
            text = fh.read()
    elif args.notes:
        text = args.notes
    else:
        text = auto_changelog(version)
    lines = text.splitlines()
    raw_title = lines[0].strip() if lines else ""
    title = re.sub(r"^#+\s*", "", raw_title).strip() or f"Playnite Vault {version}"
    body = "\n".join(lines[1:]).strip("\n")
    return title, body


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


# ---------------------------------------------------------------- tag

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def git_out(*args: str) -> tuple[int, str]:
    """在仓库根跑一条 git 命令，返回 (退出码, stdout+stderr)。"""
    proc = subprocess.run(["git", "-C", REPO_ROOT, *args],
                          capture_output=True, text=True)
    return proc.returncode, (proc.stdout + proc.stderr).strip()


def tag_kind(tag: str) -> str | None:
    """`git cat-file -t` 的结果：'tag' = 附注 tag，'commit' = 轻量 tag，None = 不存在。"""
    rc, out = git_out("cat-file", "-t", f"refs/tags/{tag}")
    return out.strip() if rc == 0 else None


def ensure_annotated_tag(tag: str, version: str, only: str, force: bool,
                         message: str | None = None) -> None:
    """把 `tag` 弄成**附注 tag** 并推到本次要发布的平台。

    为什么非自己建不可：GitHub 的 release API 只给一个 `tag_name` 时，它替你建的是
    **轻量 tag**（一个直接指向 commit 的 ref，`ls-remote` 里只有一行）；Gitee 的 API
    建的是**附注 tag**（一个 tag 对象，带 tagger / 说明，`ls-remote` 里有 `^{}` 那行）。
    同一个脚本跑两边，tag 类型就岔开了 —— v1.7.0 正是如此（GitHub 轻量、Gitee 附注）。
    所以先在本地把附注 tag 建好再推上去；等 API 建 release 时看到同名 tag 已存在，
    它会直接复用，两边就一致了。

    `--force` 时**无条件重建**：既用于「把 tag 挪到新提交」，也用于修正 tagger / 说明
    （Gitee 自动建的 tag 里 tagger 是它的机器人，说明还会被塞成整篇发布说明）。

    本地 `user.name` / `user.email` 就是 tagger，所以仓库的提交身份得先配对。
    """
    kind = tag_kind(tag)
    _, head = git_out("rev-parse", "HEAD")
    msg = (message or "").strip() or f"Playnite Vault {version}"

    if kind == "tag" and not force:
        _, target = git_out("rev-parse", f"refs/tags/{tag}^{{}}")
        if target != head:
            log(f"  ! 附注 tag {tag} 指向 {target[:10]}，而 HEAD 是 {head[:10]} —— tag 不动。"
                f"要把它挪到 HEAD、或重建以修正 tagger/说明，请加 --force")
        else:
            log(f"  · 附注 tag {tag} 已在本地，指向 HEAD {head[:10]}")
    else:
        if kind == "commit":
            log(f"  · 本地 {tag} 是轻量 tag，改建成附注 tag")
        elif kind == "tag":
            log(f"  · 按 --force 重建附注 tag {tag}（顺带把 tagger/说明改成本仓库的）")
        if kind:
            rc, out = git_out("tag", "-d", tag)
            if rc != 0:
                raise SystemExit(f"删掉旧 tag {tag} 失败：{out}")
        rc, out = git_out("tag", "-a", tag, "-m", msg, head)
        if rc != 0:
            raise SystemExit(f"建附注 tag 失败：{out}")
        log(f"  · 已建附注 tag {tag} → {head[:10]}")
        log(f"    说明：{msg}")

    remotes = {"github": ["github"], "gitee": ["gitee"],
               "both": ["github", "gitee"]}[only]
    for remote in remotes:
        rc, out = git_out("push", "--force", remote, f"refs/tags/{tag}")
        if rc != 0:
            raise SystemExit(f"推 tag 到 {remote} 失败：{out}")
        log(f"  · 已推 {tag} → {remote}")

    # 回验三件事：两边都能解引用到同一个 commit（轻量 tag 没有 ^{} 这行，会露馅）、
    # 两边是**同一个 tag 对象**、tagger 是本仓库的提交身份。
    _, target = git_out("rev-parse", f"refs/tags/{tag}^{{}}")
    # %(taggeremail) 自带尖括号，别再加一层
    _, tagger = git_out("for-each-ref", "--format=%(taggername) %(taggeremail)",
                        f"refs/tags/{tag}")
    log(f"  · tagger：{tagger}")

    objects = set()
    for remote in remotes:
        rc, out = git_out("ls-remote", "--tags", remote, f"refs/tags/{tag}*")
        if rc != 0:
            raise SystemExit(f"回验 tag 失败（{remote}）：{out}")
        refs = {}
        for line in out.splitlines():
            if "\t" in line:
                sha, ref = line.split("\t", 1)
                refs[ref.strip()] = sha.strip()
        deref = refs.get(f"refs/tags/{tag}^{{}}")
        if deref != target:
            raise SystemExit(
                f"{remote} 的 {tag} 不是附注 tag（或指向不对）：\n{out or '(空)'}")
        objects.add(refs.get(f"refs/tags/{tag}"))
        log(f"  · 回验 {remote}：附注 tag → {deref[:10]}")

    if len(objects) != 1:
        raise SystemExit(f"两个平台的 tag 不是同一个对象：{sorted(o[:10] for o in objects)}")


# ---------------------------------------------------------------- release 正文

REPO_URLS = {"github": f"https://github.com/{OWNER}/{REPO}",
             "gitee": f"https://gitee.com/{OWNER}/{REPO}"}

# 资产名 → 「该下哪个」。这段**由脚本写**：手写必然写成另一个平台的链接。
ASSET_HINT = (
    ("PlayniteVault-",
     "Playnite 插件包 —— 解压后把文件夹整个放进 Playnite 安装目录下的 `Extensions` 里，"
     "完全退出 Playnite 再打开。**内含《使用说明.txt》**"),
    ("VaultUnpacker-",
     "独立解包器 —— 单文件、免安装、不需要 Python，双击即用；也负责把插件装进 Playnite"),
    ("使用说明",
     "纯文本说明，和插件包里那份是同一份"),
)


def asset_hint(name: str) -> str:
    for prefix, text in ASSET_HINT:
        if name.startswith(prefix):
            return text
    return ""


# 要点类型标记 → （emoji, 中文名）。作者写 `[bugfix]`，脚本渲染成 `[🐛 修复]`。
# 为什么由脚本翻译而不是让作者直接打 emoji：emoji 手打容易漏、容易前后不一致，
# 而类型词是稳定的枚举；把「好看」交给代码，作者只管写「这条是什么类型」。
TYPE_STYLE = {
    "feature":  ("✨", "新增"),
    "feat":     ("✨", "新增"),
    "new":      ("✨", "新增"),
    "bugfix":   ("🐛", "修复"),
    "fix":      ("🐛", "修复"),
    "hotfix":   ("🚑", "紧急修复"),
    "perf":     ("⚡", "性能"),
    "ui":       ("🎨", "界面"),
    "ux":       ("🎨", "界面"),
    "docs":     ("📄", "文档"),
    "refactor": ("🔧", "重构"),
    "security": ("🔒", "安全"),
    "breaking": ("💥", "破坏性变更"),
    "note":     ("📌", "说明"),
}

# `- [bugfix] 正文` / `- [fix] 正文`
BULLET_RE = re.compile(r"^-\s*\[([A-Za-z][A-Za-z0-9_-]*)\]\s*(.*)$")

# 脚本自己生成的页脚行 —— 作者正文里若重复写了，丢掉，避免出现两份。
FOOTER_NOISE_RE = re.compile(
    r"^\s*(完整改动见|完整变更见|更新日志见|使用说明见|装好之后怎么用见)\b")

USAGE_PATH = "docs/plugin-usage.md"


def _strip_notes_footer(body: str) -> str:
    """丢掉作者手写的页脚段（`---` 之后的 CHANGELOG / 使用说明引导）。

    这两行现在由脚本生成：平台域名要按 GitHub/Gitee 分别拼，手写迟早写错一个。
    """
    lines = body.splitlines()
    kept, dropping = [], False
    for line in lines:
        if line.strip() == "---":
            dropping = True            # 从这里往后的引导段交给脚本
            continue
        if dropping and (FOOTER_NOISE_RE.match(line) or not line.strip()):
            continue
        if dropping and line.strip() and not FOOTER_NOISE_RE.match(line):
            dropping = False           # `---` 之后还有正经内容，照收
        kept.append(line)
    return "\n".join(kept)


def _parse_notes(body: str):
    """把作者正文拆成 (要点分组, 其余段落)。

    要点 = ``- [类型] 文字``；紧跟其后、以空白开头的行算它的续行（用 ``<br>`` 接回去）。
    其余非空行按原顺序收进段落，输出时放在要点之后。
    """
    order, grouped = [], {}
    prose = []
    current_key = None

    for raw in _strip_notes_footer(body).splitlines():
        hit = BULLET_RE.match(raw.strip())
        if hit:
            key = hit.group(1).lower()
            if key not in grouped:
                grouped[key] = []
                order.append(key)
            grouped[key].append(hit.group(2).strip())
            current_key = key
            continue

        if current_key and raw[:1] in (" ", "\t") and raw.strip():
            grouped[current_key][-1] = grouped[current_key][-1] + "<br>" + raw.strip()
            continue

        current_key = None
        if raw.strip():
            prose.append(raw.rstrip())

    return [(k, grouped[k]) for k in order], prose


def _render_updates(items) -> list[str]:
    """按类型分组渲染要点：单条并排，多条做成二级列表。"""
    lines = []
    for key, texts in items:
        emoji, label = TYPE_STYLE.get(key, ("•", key))
        head = f"- [{emoji} {label}]"
        if len(texts) == 1:
            lines.append(f"{head} {texts[0]}")
        else:
            # 同一类型有多条 → 类型行只写类型，条目缩进成二级列表，
            # 扫一眼就能看出「这次修了几个 bug / 加了几个功能」。
            lines.append(head)
            lines.extend(f"  - {t}" for t in texts)
    return lines


def release_body_for(platform: str, version: str, body: str,
                     assets: list[str]) -> str:
    """把作者写的正文改成**这个平台**的版本。

    版面（自上而下，四段）：

        1. `## 下载` —— 资产直链，**不折叠**，第一眼就能看到下哪个；
        2. `## 更新` —— 要点，按类型分组、类型名带 emoji，同类型多条做二级列表；
        3. 作者写的引导段（注意事项 / 适用边界）；
        4. 页脚：`使用说明见 docs/plugin-usage.md`（按平台拼域名）。

    三件事必须由脚本做，不能靠手写：

    1. **平台链接**：正文里的 `https://github.com/...` 在 Gitee 上点开是另一个站点。
    2. **资产直链**：两边格式一样（`<repo>/releases/download/<tag>/<文件名>`），
       但按平台拼才不会写错。
    3. **类型 emoji 与分组**：`[bugfix]` → `[🐛 修复]`，反复起标题容易写歪。

    作者只写逐条要点（每条以 `[类型]` 开头）与正文引导。
    正文写法约定见 `docs/dev-notes.md` 第 6 节。
    """
    tag = f"v{version}"
    base = REPO_URLS[platform]
    out = body
    for other, url in REPO_URLS.items():
        if other != platform:
            out = out.replace(url, base)

    lines = ["## 下载", ""]
    for path in assets:
        name = os.path.basename(path)
        hint = asset_hint(name)
        link = f"{base}/releases/download/{tag}/{name}"
        lines.append(f"- **[{name}]({link})**" + (f" —— {hint}" if hint else ""))

    items, prose = _parse_notes(out)
    if items:
        lines += ["", "## 更新", ""]
        lines += _render_updates(items)

    if prose:
        lines += [""] + prose

    lines += ["", "---", "",
              f"使用说明见 [{USAGE_PATH}]({base}/blob/main/{USAGE_PATH})"]
    return "\n".join(lines).rstrip() + "\n"


# --------------------------------------------------- 改标题 / 撤版本

def retitle_release(args, title: str, tag: str) -> int:
    """只改 release 的标题，正文与资产一概不碰（用来统一历史 release 的命名）。"""
    title = title.strip()
    log(f"改标题：{tag} → {title!r}（不动正文、不动资产）")
    if args.only in ("github", "both"):
        tok = read_token("github", None)
        rel = github_find_release(tok, tag)
        if rel is None:
            log(f"  ! GitHub 上没有 {tag}")
        elif rel.get("name") == title:
            log("  · GitHub 标题已是目标值")
        else:
            api("PATCH", f"{GITHUB_API}/repos/{OWNER}/{REPO}/releases/{rel['id']}",
                token=tok, json_body={"name": title}, expect=(200,),
                label="GitHub 改标题")
            log(f"  · GitHub：{rel.get('name')!r} → {title!r}")
    if args.only in ("gitee", "both"):
        tok = read_token("gitee", None)
        rel = gitee_find_release(tok, tag)
        if rel is None:
            log(f"  ! Gitee 上没有 {tag}")
        elif rel.get("name") == title:
            log("  · Gitee 标题已是目标值")
        else:
            # Gitee 的 release 更新接口**强制要求 body**（只传 name 会 400
            # "body is missing"），所以把原正文原样带回去 —— 只改标题、不动内容。
            api("PATCH", gitee_url(f"/releases/{rel['id']}", tok), token="",
                json_body={"tag_name": tag, "name": title,
                           "body": rel.get("body") or ""}, expect=(200,),
                label="Gitee 改标题")
            log(f"  · Gitee：{rel.get('name')!r} → {title!r}")
    return 0


def drop_release(args, tag: str) -> int:
    """撤掉一整条 release 与 tag —— release 走 API，tag 走 ``git push --delete``。"""
    log(f"删除 release 与 tag：{tag}")
    remotes = {"github": ["github"], "gitee": ["gitee"],
               "both": ["github", "gitee"]}[args.only]

    if "github" in remotes:
        tok = read_token("github", None)
        rel = github_find_release(tok, tag)
        if rel is None:
            log("  · GitHub 上没有该 release")
        else:
            api("DELETE", f"{GITHUB_API}/repos/{OWNER}/{REPO}/releases/{rel['id']}",
                token=tok, expect=(204,), label="GitHub 删 release")
            log(f"  · GitHub release {tag} 已删（id={rel['id']}）")
    if "gitee" in remotes:
        tok = read_token("gitee", None)
        rel = gitee_find_release(tok, tag)
        if rel is None:
            log("  · Gitee 上没有该 release")
        else:
            api("DELETE", gitee_url(f"/releases/{rel['id']}", tok), token="",
                expect=(200, 204), label="Gitee 删 release")
            log(f"  · Gitee release {tag} 已删（id={rel['id']}）")

    if tag_kind(tag) is None:
        log("  · 本地没有这个 tag")
    else:
        rc, out = git_out("tag", "-d", tag)
        log(f"  · 本地 tag {'已删' if rc == 0 else '删除失败：' + out}")

    for remote in remotes:
        rc, out = git_out("push", remote, "--delete", f"refs/tags/{tag}")
        log(f"  · 远端 {remote} tag：{'已删' if rc == 0 else out + '（可能本来就没有）'}")
        _, ls = git_out("ls-remote", "--tags", remote, f"refs/tags/{tag}")
        log(f"  · 回验 {remote}：{'仍在（要手动处理）' if ls.strip() else '已无此 tag'}")
    return 0


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
    ap.add_argument("--skip-tag", action="store_true",
                    help="不碰 git tag（默认会先建/校正附注 tag 并推到发布平台）")
    ap.add_argument("--tag-message", default=None,
                    help="附注 tag 的说明（一句话摘要）。不填就用「Playnite Vault <版本>」")
    ap.add_argument("--retitle", default=None,
                    help="只把两个平台的 release 标题改成这个值（不动正文 / 资产）")
    ap.add_argument("--delete", action="store_true",
                    help="删掉两个平台的 release 以及 tag（本地 + 远端），撤掉一个不该发的版本")
    ap.add_argument("--verify-download", action="store_true",
                    help="额外把资产从两个平台真下回来比 sha256（Gitee 不回传大小，只有这个算数）")
    args = ap.parse_args(argv)

    version = args.version.lstrip("v")
    tag = f"v{version}"

    if args.retitle is not None or args.delete:
        for key in PROXY_ENV_KEYS:
            if os.environ.pop(key, None):
                log(f"（已清除环境变量 {key}，改用内置代理兜底）")
        if args.delete:
            return drop_release(args, tag)
        return retitle_release(args, args.retitle, tag)

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

    log("\n== tag ==")
    if args.skip_tag:
        log("  · 按 --skip-tag 跳过（tag 保持原样）")
    else:
        ensure_annotated_tag(tag, version, args.only, args.force, args.tag_message)

    problems: list[str] = []
    gh_token = gi_token = None

    if args.only in ("github", "both"):
        log("\n== GitHub ==")
        gh_token = read_token("github", None)
        github_publish(gh_token, tag, title,
                       release_body_for("github", version, body, assets),
                       assets, args.draft, args.force)
        if not args.skip_verify:
            problems += verify_github(gh_token, tag, assets)

    if args.only in ("gitee", "both"):
        log("\n== Gitee ==")
        gi_token = read_token("gitee", None)
        gitee_publish(gi_token, tag, title,
                      release_body_for("gitee", version, body, assets),
                      assets, args.draft, args.force)
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
