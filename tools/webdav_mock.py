"""极简 WebDAV 服务，仅用于本地端到端验证 Playnite Vault 插件。

只依赖 Python 标准库，实现插件实际用到的这几个方法：
    HEAD / GET / PUT / MKCOL / PROPFIND / DELETE
带 Basic 认证，行为与常见 NAS 的 WebDAV 端一致。

用法:
    python tools/webdav_mock.py [--root DIR] [--port 8099] [--user demo] [--pass demo123]
"""

import argparse
import base64
import json
import os
import re
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlparse

ROOT = ""
USER = "demo"
PASSWORD = "demo123"


def build_test_repo(root):
    """首次运行时生成一个测试仓库。"""
    apps_dir = os.path.join(root, "apps", "demo-game")
    files_dir = os.path.join(apps_dir, "files")
    os.makedirs(os.path.join(files_dir, "data"), exist_ok=True)

    payloads = {
        os.path.join(files_dir, "demo.exe"): (b"DEMO-EXE\x00" * 5120),                 # 45 KB
        os.path.join(files_dir, "readme.txt"): (
            "这是一个来自 NAS 的测试应用。\n"
            "由 Playnite Playnite Vault 插件下载到本地。\n"
        ).encode("utf-8"),
        os.path.join(files_dir, "data", "assets.pak"): (b"PAK" * 700000),              # 2.1 MB
    }

    entries = []
    for path, data in payloads.items():
        with open(path, "wb") as handle:
            handle.write(data)
        rel = os.path.relpath(path, files_dir).replace("\\", "/")
        entries.append({"Path": rel, "Size": len(data)})

    total = sum(e["Size"] for e in entries)

    manifest = {
        "Schema": 1,
        "Id": "demo-game",
        "Name": "Demo Game (NAS)",
        "Version": "1.0.0",
        "UpdatedAt": "2026-09-17T06:00:00Z",
        "LaunchExe": "demo.exe",
        "TotalBytes": total,
        "Files": entries,
    }
    with open(os.path.join(apps_dir, "manifest.json"), "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)

    index = {
        "Schema": 1,
        "Name": "Playnite Vault Repo",
        "UpdatedAt": "2026-09-17T06:00:00Z",
        "Apps": [
            {
                "Id": "demo-game",
                "Name": "Demo Game (NAS)",
                "Version": "1.0.0",
                "TotalBytes": total,
                "FileCount": len(entries),
                "LaunchExe": "demo.exe",
            },
            {
                "Id": "demo-tool",
                "Name": "Demo Tool (NAS, 未安装)",
                "Version": "0.9",
                "TotalBytes": 0,
                "FileCount": 0,
                "LaunchExe": "tool.exe",
            },
        ],
    }
    with open(os.path.join(root, "index.json"), "w", encoding="utf-8") as handle:
        json.dump(index, handle, ensure_ascii=False, indent=2)

    print("[init] 测试仓库已生成: %s (%d 字节)" % (root, total))


class WebDavHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"
    server_version = "VaultMockWebDAV/0.1"

    # ---------- 工具 ----------

    def log_message(self, fmt, *args):
        sys.stderr.write("[dav] %s %s\n" % (self.command, self.path))

    def _local_path(self):
        path = unquote(urlparse(self.path).path)
        rel = path.lstrip("/")
        full = os.path.normpath(os.path.join(ROOT, rel))
        if not full.startswith(os.path.normpath(ROOT)):
            return None
        return full

    def _authorized(self):
        header = self.headers.get("Authorization", "")
        if not header.startswith("Basic "):
            return False
        try:
            raw = base64.b64decode(header[6:]).decode("utf-8")
        except Exception:
            return False
        user, _, password = raw.partition(":")
        return user == USER and password == PASSWORD

    def _reject(self):
        body = b"unauthorized"
        self.send_response(401)
        self.send_header("WWW-Authenticate", 'Basic realm="vault"')
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _reply(self, code, body=b"", content_type="application/octet-stream", extra=None):
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        for key, value in (extra or {}).items():
            self.send_header(key, value)
        self.end_headers()
        if self.command != "HEAD" and body:
            self.wfile.write(body)

    def _multistatus(self, targets):
        parts = ['<?xml version="1.0" encoding="utf-8"?>', '<D:multistatus xmlns:D="DAV:">']
        for href, is_dir, size in targets:
            parts.append("<D:response><D:href>%s</D:href><D:propstat><D:prop>" % href)
            parts.append("<D:resourcetype>%s</D:resourcetype>" % ("<D:collection/>" if is_dir else ""))
            parts.append("<D:getcontentlength>%d</D:getcontentlength>" % size)
            parts.append("<D:getlastmodified>Thu, 17 Sep 2026 06:00:00 GMT</D:getlastmodified>")
            parts.append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>")
        parts.append("</D:multistatus>")
        return "".join(parts).encode("utf-8")

    # ---------- 方法 ----------

    def do_GET(self):
        if not self._authorized():
            return self._reject()
        full = self._local_path()
        if not (full and os.path.isfile(full)):
            return self._reply(404, b"not found", "text/plain")

        with open(full, "rb") as handle:
            data = handle.read()
        total = len(data)

        # 支持 Range，用于验证插件的断点续传
        rng = self.headers.get("Range")
        if rng:
            matched = re.match(r"bytes=(\d+)-", rng)
            if matched:
                start = int(matched.group(1))
                if start >= total:
                    return self._reply(416, b"", "text/plain",
                                       {"Content-Range": "bytes */%d" % total})
                chunk = data[start:]
                return self._reply(206, chunk, "application/octet-stream",
                                   {"Content-Range": "bytes %d-%d/%d" % (start, total - 1, total)})

        self._reply(200, data)

    def do_HEAD(self):
        if not self._authorized():
            return self._reject()
        full = self._local_path()
        if full and os.path.isfile(full):
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(os.path.getsize(full)))
            self.end_headers()
        else:
            self._reply(404, b"", "text/plain")

    def do_PUT(self):
        if not self._authorized():
            return self._reject()
        full = self._local_path()
        if not full:
            return self._reply(400, b"bad path", "text/plain")
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else b""
        parent = os.path.dirname(full)
        if parent:
            os.makedirs(parent, exist_ok=True)
        with open(full, "wb") as handle:
            handle.write(body)
        self._reply(201 if not os.path.exists(full) else 204)

    def do_MKCOL(self):
        if not self._authorized():
            return self._reject()
        full = self._local_path()
        if not full:
            return self._reply(400, b"bad path", "text/plain")
        if os.path.isdir(full):
            return self._reply(405, b"exists", "text/plain")
        os.makedirs(full, exist_ok=True)
        self._reply(201)

    def do_DELETE(self):
        if not self._authorized():
            return self._reject()
        full = self._local_path()
        if full and os.path.isfile(full):
            os.remove(full)
            return self._reply(204)
        self._reply(404, b"not found", "text/plain")

    def do_PROPFIND(self):
        if not self._authorized():
            return self._reject()
        full = self._local_path()
        if not full or not os.path.exists(full):
            return self._reply(404, b"not found", "text/plain")

        depth = self.headers.get("Depth", "0")
        targets = [(urlparse(self.path).path, os.path.isdir(full), 0 if os.path.isdir(full) else os.path.getsize(full))]

        if depth != "0" and os.path.isdir(full):
            base = urlparse(self.path).path.rstrip("/")
            for name in sorted(os.listdir(full)):
                child = os.path.join(full, name)
                targets.append((
                    "%s/%s" % (base, name),
                    os.path.isdir(child),
                    0 if os.path.isdir(child) else os.path.getsize(child),
                ))

        self._reply(207, self._multistatus(targets), 'text/xml; charset="utf-8"')


def main():
    global ROOT, USER, PASSWORD

    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "testrepo"))
    parser.add_argument("--port", type=int, default=8099)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--user", default="demo")
    parser.add_argument("--passwd", default="demo123")
    parser.add_argument("--no-init", action="store_true")
    args = parser.parse_args()

    ROOT = os.path.normpath(os.path.abspath(args.root))
    USER = args.user
    PASSWORD = args.passwd

    os.makedirs(ROOT, exist_ok=True)
    if not args.no_init and not os.path.exists(os.path.join(ROOT, "index.json")):
        build_test_repo(ROOT)

    server = ThreadingHTTPServer((args.host, args.port), WebDavHandler)
    print("[dav] 服务已启动 http://%s:%d/  root=%s  user=%s" % (args.host, args.port, ROOT, USER))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("[dav] 已停止")


if __name__ == "__main__":
    main()
