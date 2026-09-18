# -*- coding: utf-8 -*-
"""
NAS WebDAV 吞吐排查脚本。

目的：把「网络链路 / NAS 服务端 / 客户端代码」三者分离开，
定位下载速度的天花板到底在哪一层。

配置从外部文件读（vault_unpacker/config.py 管的那个 config.json），
代码里不留任何主机名 / 账号 / 密码 —— 需要临时换目标时用命令行参数覆盖即可。

用法:
    # 直接读配置文件里的仓库地址和账号，测某个应用的分片
    python speedtest.py --app adofai-test

    # 只测仓库根
    python speedtest.py --path /app/Store

    # 临时换一台机器（不改配置文件）
    python speedtest.py --host 10.0.0.5 --port 5006 --user u --password p --app xxx

    # 换一个配置文件
    python speedtest.py --config D:/somewhere/config.json --app xxx
"""
import argparse
import base64
import http.client
import os
import ssl
import sys
import tempfile
import threading
import time
import urllib.parse

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

CHUNK = 256 * 1024


def load_config(path=None):
    """读外部配置。读不到就返回空字典，后面用命令行参数补。"""
    try:
        from vault_unpacker import config as cfg
    except ImportError:
        return {}, None
    try:
        import json
        p = path or cfg.config_path()
        with open(p, "r", encoding="utf-8-sig") as f:
            return json.load(f) or {}, p
    except Exception:
        return {}, (path or None)


def split_base(url):
    """把 https://host:5006/app/Store 拆成 (scheme, host, port, path)。"""
    parts = urllib.parse.urlsplit(url if "://" in url else "http://" + url)
    scheme = parts.scheme or "http"
    host = parts.hostname or ""
    port = parts.port or (443 if scheme == "https" else 80)
    path = parts.path or "/"
    return scheme, host, port, path.rstrip("/")


def make_conn(host, port, scheme, timeout=60):
    if scheme == "https":
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        return http.client.HTTPSConnection(host, port, timeout=timeout, context=ctx)
    return http.client.HTTPConnection(host, port, timeout=timeout)


def head_size(host, port, scheme, path, auth):
    conn = make_conn(host, port, scheme)
    try:
        conn.request("HEAD", path, headers={"Authorization": auth})
        res = conn.getresponse()
        return res.status, int(res.getheader("Content-Length") or 0)
    finally:
        conn.close()


def fetch(host, port, scheme, path, auth, sink=None):
    """下载一个路径，返回 (字节数, 秒数)。sink 为 None 时丢弃数据（纯网络测试）。"""
    conn = make_conn(host, port, scheme)
    try:
        t0 = time.time()
        conn.request("GET", path, headers={"Authorization": auth})
        res = conn.getresponse()
        if res.status != 200:
            raise RuntimeError("HTTP %d %s" % (res.status, res.reason))

        total = 0
        while True:
            block = res.read(CHUNK)
            if not block:
                break
            total += len(block)
            if sink is not None:
                sink.write(block)
        return total, time.time() - t0
    finally:
        conn.close()


def mbps(nbytes, seconds):
    return nbytes / seconds / 1024 / 1024


def build_args():
    ap = argparse.ArgumentParser(
        description="NAS WebDAV 吞吐排查（默认参数取自外部配置文件）")
    ap.add_argument("--config", default=None,
                    help="配置文件路径，默认用 vault_unpacker.config 定位的那个")
    ap.add_argument("--host", default=None, help="覆盖配置文件里的主机名")
    ap.add_argument("--port", type=int, default=None)
    ap.add_argument("--scheme", default=None, choices=["http", "https"])
    ap.add_argument("--user", default=None)
    ap.add_argument("--password", default=None)
    ap.add_argument("--base-path", default=None,
                    help="仓库在服务器上的根路径，如 /app/Store（默认取自配置里的 URL）")
    ap.add_argument("--app", default=None,
                    help="要测的应用 id，会拼成 <base>/apps/<id>/parts")
    ap.add_argument("--path", default=None,
                    help="直接指定分片所在目录，优先于 --app")
    ap.add_argument("--threads", type=int, default=4, help="并发路数")
    ap.add_argument("--limit", type=int, default=4, help="测几个分片")
    ap.add_argument("--sink", default=None,
                    help="写盘测试的落盘文件，默认放系统临时目录")
    return ap


def main():
    args = build_args().parse_args()
    raw, used = load_config(args.config)

    # 命令行 > 配置文件 > 报错。绝不在代码里兜一个默认账号。
    url = raw.get("webdav_url") or ""
    scheme0, host0, port0, path0 = split_base(url) if url else ("https", "", 80, "")

    scheme = args.scheme or scheme0
    host = args.host or host0
    port = args.port or port0
    user = args.user if args.user is not None else (raw.get("webdav_user") or "")
    password = (args.password if args.password is not None
                else (raw.get("webdav_password") or ""))
    base_path = (args.base_path or path0 or "").rstrip("/")

    if not host:
        print("没有目标主机。请先在配置文件里填 webdav_url，或用 --host 指定。")
        print("（配置文件：%s）" % (used or "未找到"))
        return 2

    if args.path:
        target_dir = args.path.rstrip("/")
    elif args.app:
        target_dir = "%s/apps/%s/parts" % (base_path, args.app)
    else:
        target_dir = base_path or "/"
        print("提示：没给 --app / --path，只测仓库根目录。")

    if not target_dir.startswith("/"):
        target_dir = "/" + target_dir

    auth = "Basic " + base64.b64encode(
        ("%s:%s" % (user, password)).encode("utf-8")).decode("ascii")

    parts = ["/part-%04d.bin" % i for i in range(args.limit)]

    print("[配置] %s" % (used or "（没读到配置文件，全部按命令行参数走）"))
    print("[目标] %s://%s:%d%s" % (scheme, host, port, target_dir))
    print("[账号] %s%s" % (user or "(空)", "  /  ****" if password else ""))

    # 1) 探测每个分片的大小
    sizes = []
    for p in parts:
        try:
            status, size = head_size(host, port, scheme, target_dir + p, auth)
            sizes.append(size)
            print("  HEAD %s -> HTTP %s, %d 字节" % (p, status, size))
        except Exception as ex:
            print("  HEAD %s -> 失败: %s" % (p, ex))
    if not sizes:
        print("没有任何分片可测（路径对不对？应用 id 对不对？）")
        return 1

    # 2) 单连接顺序下载（丢弃数据，纯网络+服务端）
    print("\n--- 单连接顺序下载（数据丢弃，排除客户端磁盘）---")
    grand_bytes = 0
    grand_time = 0.0
    for p, size in zip(parts, sizes):
        try:
            n, sec = fetch(host, port, scheme, target_dir + p, auth, sink=None)
            grand_bytes += n
            grand_time += sec
            print("  %s  %8.2f MB  %6.2f s  ->  %6.2f MB/s"
                  % (p, n / 1048576, sec, mbps(n, sec)))
        except Exception as ex:
            print("  %s -> 失败: %s" % (p, ex))
    if grand_time > 0:
        print("  顺序合计: %.2f MB / %.2f s = %.2f MB/s"
              % (grand_bytes / 1048576, grand_time, mbps(grand_bytes, grand_time)))

    # 3) 多连接并发下载（看聚合带宽是否线性增长）
    print("\n--- %d 路并发下载（数据丢弃）---" % args.threads)
    results = {}
    lock = threading.Lock()

    def worker(idx, path):
        try:
            n, sec = fetch(host, port, scheme, target_dir + path, auth, sink=None)
            with lock:
                results[idx] = (path, n, sec)
        except Exception as ex:
            with lock:
                results[idx] = (path, 0, 0.0)
                print("  线程 %d 失败: %s" % (idx, ex))

    targets = [parts[i % len(parts)] for i in range(args.threads)]
    t0 = time.time()
    threads = [threading.Thread(target=worker, args=(i, p))
               for i, p in enumerate(targets)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    wall = time.time() - t0

    total = sum(r[1] for r in results.values())
    for i in sorted(results):
        path, n, sec = results[i]
        if sec > 0:
            print("  线程%d %s  %8.2f MB  %6.2f s  ->  %6.2f MB/s"
                  % (i, path, n / 1048576, sec, mbps(n, sec)))
    if wall > 0:
        print("  并发聚合: %.2f MB / %.2f s = %.2f MB/s"
              % (total / 1048576, wall, mbps(total, wall)))

    # 4) 真实写盘（隔离客户端磁盘影响）
    sink_path = args.sink or os.path.join(tempfile.gettempdir(),
                                          "vault-speedtest.bin")
    print("\n--- 单连接下载并写盘（%s）---" % sink_path)
    try:
        with open(sink_path, "wb") as f:
            n, sec = fetch(host, port, scheme, target_dir + parts[0], auth, sink=f)
        print("  %.2f MB / %.2f s = %.2f MB/s"
              % (n / 1048576, sec, mbps(n, sec)))
    except Exception as ex:
        print("  写盘测试失败: %s" % ex)

    return 0


if __name__ == "__main__":
    sys.exit(main())
