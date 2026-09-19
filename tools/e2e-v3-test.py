# -*- coding: utf-8 -*-
"""v3（内容寻址区块）端到端验证。

一条命令跑完整链路，**不碰真实 NAS**（用 tools/webdav_mock.py 起一个本地 WebDAV）：

    构造测试目录 → VaultPack 打包上传 → 检查仓库落盘结构
    → Python 解包器下载还原 → 与原始目录逐字节比对
    → 中断-续传验证

用法:
    python tools/e2e-v3-test.py                 # 常规（约 230 MB，含跨块文件）
    python tools/e2e-v3-test.py --big           # 再加一个 1.93 GB 单文件（复现历史故障规模）
    python tools/e2e-v3-test.py --keep          # 保留临时目录，便于手工查看
    python tools/e2e-v3-test.py --compress      # 顺带验证 deflate 分支

为什么值得留着这个脚本：v3 有两个「写错了也能跑通小文件」的地方 ——
文件跨区块、区块乱序到达（并发下载）—— 只有用一个明显大于区块大小的文件
加多路并发才能把它们暴露出来。
"""

import argparse
import hashlib
import json
import os
import shutil
import socket
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)

from vault_unpacker import core  # noqa: E402

# 测试用的应用：中文显示名 + ASCII 安装目录名。
# 这正是需求 #3 的场景：显示名不能当目录名。
APP_ID = "plants-vs-zombies-rh"
DISPLAY_NAME = "植物大战僵尸融合版"
INSTALL_DIR_NAME = "Plants Vs Zombies RH"
CHUNK_MB = 8


# ---------------------------------------------------------------- 小工具

class Step(object):
    def __init__(self):
        self.n = 0

    def __call__(self, text):
        self.n += 1
        print("")
        print("[%d] %s" % (self.n, text), flush=True)


def log(text=""):
    print("    " + text, flush=True)


def rmtree(path):
    if os.path.isdir(path):
        shutil.rmtree(path, ignore_errors=True)


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def tree_digest(root):
    """整棵目录的内容指纹：路径 + 大小 + 内容都算进去，顺序无关。"""
    h = hashlib.sha1()
    items = []
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in filenames:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, root).replace("\\", "/")
            items.append((rel, full))
    for rel, full in sorted(items):
        h.update(rel.encode("utf-8"))
        h.update(b"\x00")
        h.update(str(os.path.getsize(full)).encode("ascii"))
        h.update(b"\x00")
        with open(full, "rb") as fh:
            while True:
                buf = fh.read(1 << 20)
                if not buf:
                    break
                h.update(buf)
        h.update(b"\x01")
    return h.hexdigest(), len(items)


def make_blob(path, size, _seed=None):
    """随机内容。

    故意不用全零：全零文件会让「偏移算错」这类 bug 恰好看不出来，
    也会让压缩分支走到一个不真实的比例上。
    内容不需要可复现 —— 比对靠的是「打包前后整棵树的指纹」。
    """
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    block = 1 << 20
    written = 0
    with open(path, "wb") as fh:
        while written < size:
            n = min(block, size - written)
            fh.write(os.urandom(n))
            written += n
    return size


# ---------------------------------------------------------------- 测试目录

def build_fixture(src_root, big=False):
    """构造一份「像真实游戏」的目录树。"""
    root = os.path.join(src_root, INSTALL_DIR_NAME)
    rmtree(root)
    os.makedirs(root, exist_ok=True)

    written = []
    written.append(("game.exe", make_blob(os.path.join(root, "game.exe"), 45 * 1024, 1)))

    # 关键：一个文件明显大于区块大小 → 必然跨多个区块
    written.append(("res.pak", make_blob(os.path.join(root, "res.pak"), 200 * 1024 * 1024, 2)))

    written.append(("data/assets.bin",
                    make_blob(os.path.join(root, "data", "assets.bin"), 20 * 1024 * 1024, 3)))
    written.append(("data/nested/a/b/deep.bin",
                    make_blob(os.path.join(root, "data", "nested", "a", "b", "deep.bin"),
                              3 * 1024 * 1024, 4)))
    written.append(("readme.txt", make_blob(os.path.join(root, "readme.txt"), 1024, 5)))

    # 空文件：v3 里它不产生任何片段，读取端必须单独把它建出来
    empty = os.path.join(root, "empty.dat")
    open(empty, "wb").close()
    written.append(("empty.dat", 0))

    # 全汉字文件名 + 非 ASCII 目录：验证路径编码
    written.append(("说明.txt".encode("utf-8").decode("utf-8"),
                    make_blob(os.path.join(root, "说明.txt"), 2048, 6)))

    if big:
        written.append(("big/res.pak",
                        make_blob(os.path.join(root, "big", "res.pak"),
                                  1930 * 1024 * 1024, 7)))

    return root, written


def write_settings(path, url, data_dir, local_root, chunk_mb):
    settings = {
        "WebDavUrl": url,
        "Username": "demo",
        "Password": "demo123",
        "LocalRoot": local_root,
        "TimeoutSeconds": 15,
        "StallTimeoutSeconds": 30,
        "ResponseTimeoutSeconds": 180,
        "ChunkSizeMB": chunk_mb,
        "Concurrency": 4,
        "UploadConcurrency": 4,
        "MaxRetries": 3,
        "ResumePartial": True,
        "UseSystemProxy": False,
    }
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(settings, fh, ensure_ascii=False, indent=2)


def write_meta(path):
    """喂给 pack --meta：中文名走 JSON，避开命令行编码问题。

    同时塞进 InstallDirName —— 解包端就是靠它决定落盘目录名的。
    """
    meta = {
        "Name": DISPLAY_NAME,
        "Version": "1.0",
        "InstallDirName": INSTALL_DIR_NAME,
        "Developers": ["测试开发商"],
        "Genres": ["策略"],
        "Tags": ["塔防", "测试"],
        "Description": "端到端验证用的假应用。",
    }
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(meta, fh, ensure_ascii=False, indent=2)


# ---------------------------------------------------------------- 服务

class MockServer(object):
    def __init__(self, root, port, log_path):
        self.root = root
        self.port = port
        self.log_path = log_path
        self.proc = None
        self.log_file = None

    def start(self):
        script = os.path.join(HERE, "webdav_mock.py")

        # stdout 必须落到文件，**不能用 PIPE**：服务端每处理一个请求就写一行，
        # 而我们不读这个管道。管道缓冲区（约 64KB）一满，服务端就会阻塞在 write 上
        # ——现象是「前面几十个请求都正常，然后所有请求一起卡死到超时」，
        # 极具迷惑性，第一次跑就是这么栽的。
        self.log_file = open(self.log_path, "w+", encoding="utf-8", errors="replace")

        self.proc = subprocess.Popen(
            [sys.executable, script, "--root", self.root, "--port", str(self.port),
             "--no-init", "--user", "demo", "--passwd", "demo123"],
            stdout=self.log_file, stderr=subprocess.STDOUT)
        self.wait_ready()

    def wait_ready(self, timeout=15):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.proc.poll() is not None:
                raise RuntimeError("mock 服务提前退出：\n" + self.tail_log())
            try:
                s = socket.create_connection(("127.0.0.1", self.port), 0.3)
                s.close()
                return
            except OSError:
                time.sleep(0.15)
        raise RuntimeError("mock 服务在 %s 秒内没起来" % timeout)

    def tail_log(self, lines=20):
        try:
            self.log_file.flush()
            with open(self.log_path, "r", encoding="utf-8", errors="replace") as fh:
                return "".join(fh.readlines()[-lines:])
        except Exception:
            return "（读不到日志）"

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=5)
            except Exception:
                self.proc.kill()
        if self.log_file:
            try:
                self.log_file.close()
            except Exception:
                pass
            self.log_file = None


# ---------------------------------------------------------------- 检查项

def check_repo_layout(repo_root):
    """仓库落盘结构：区块文件名必须是 sha1，且路径里不能有中文。"""
    apps = os.path.join(repo_root, "apps")
    problems = []

    for dirpath, dirnames, filenames in os.walk(apps):
        for name in dirnames + filenames:
            try:
                name.encode("ascii")
            except UnicodeEncodeError:
                problems.append("仓库路径含非 ASCII 字符：%s" % name)

    app_dir = os.path.join(apps, APP_ID)
    if not os.path.isdir(app_dir):
        problems.append("没找到 apps/%s/ —— Id 没有按安装目录名派生" % APP_ID)
        return problems, 0

    chunks_dir = os.path.join(app_dir, "chunks")
    if not os.path.isdir(chunks_dir):
        problems.append("没有 chunks/ 目录 —— 不是 v3 布局")
        return problems, 0

    names = os.listdir(chunks_dir)
    for name in names:
        stem = name[:-4] if name.endswith(".bin") else name
        if len(stem) != 40 or any(c not in "0123456789abcdef" for c in stem):
            problems.append("区块文件名不是 40 位 sha1：%s" % name)

    return problems, len(names)


def check_manifest(repo_root):
    path = os.path.join(repo_root, "apps", APP_ID, "manifest.json")
    with open(path, "r", encoding="utf-8") as fh:
        mf = json.load(fh)

    problems = []
    chunks = mf.get("Chunks") or []
    files = mf.get("Files") or []

    if not chunks:
        problems.append("清单里没有 Chunks")
    if mf.get("Packed") is not True:
        problems.append("Packed 不是 true")

    cross = [f for f in files if len(f.get("Pieces") or []) > 1]
    if not cross:
        problems.append("没有任何文件跨区块 —— 这条用例没测到 v3 的关键点")

    for f in files:
        pieces = f.get("Pieces") or []
        if int(f.get("Size") or 0) == 0:
            continue
        if not pieces:
            problems.append("非空文件没有 Pieces：%s" % f.get("Path"))
            continue
        # 片段必须首尾相接、合计等于文件大小
        cursor = 0
        for p in sorted(pieces, key=lambda x: x.get("FileOffset") or 0):
            if int(p.get("FileOffset") or 0) != cursor:
                problems.append("片段不连续：%s" % f.get("Path"))
                break
            cursor += int(p.get("Size") or 0)
        if cursor != int(f.get("Size") or 0):
            problems.append("片段合计 %d != 文件大小 %d：%s"
                            % (cursor, f.get("Size"), f.get("Path")))

    return problems, mf


def download_with_interrupt(url, out_dir, tmp_dir, cancel_after_bytes):
    """在传到一半时取消，用来制造「续传」场景。返回 True 表示确实被取消了。"""
    stop = threading.Event()
    rep = _CountingReporter(stop, cancel_after_bytes)

    try:
        core.unpack(core.HttpSource(url, "demo", "demo123", timeout=30),
                    APP_ID, out_dir, rep, stop,
                    part_tmp=tmp_dir, chunk_workers=4)
        return False
    except core.Cancelled:
        return True


class _CountingReporter(core.Reporter):
    """按累计字节到阈值就把 cancel 事件置上。"""

    def __init__(self, event, threshold):
        self.event = event
        self.threshold = threshold
        self.last = 0

    def log(self, msg):
        pass

    def progress(self, done, total, label=""):
        self.last = done
        if done >= self.threshold:
            self.event.set()

    def stage(self, name):
        pass


# ---------------------------------------------------------------- 主流程

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--big", action="store_true", help="额外加一个 1.93 GB 单文件")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    ap.add_argument("--compress", action="store_true", help="打包时开 deflate")
    ap.add_argument("--chunk-mb", type=int, default=CHUNK_MB)
    args = ap.parse_args()

    chunk_mb = args.chunk_mb

    base = os.path.join(os.environ.get("TEMP") or "/tmp", "vault-e2e")
    src_root = os.path.join(base, "src")
    repo_root = os.path.join(base, "repo")
    out_dir = os.path.join(base, "out", INSTALL_DIR_NAME)
    tmp_dir = os.path.join(base, "tmp")
    work_dir = os.path.join(base, "work")
    settings_path = os.path.join(work_dir, "settings.json")
    meta_path = os.path.join(work_dir, "meta.json")

    step = Step()
    port = free_port()
    url = "http://127.0.0.1:%d/" % port

    rmtree(base)
    for path in (src_root, repo_root, tmp_dir, work_dir):
        os.makedirs(path, exist_ok=True)

    server = MockServer(repo_root, port, os.path.join(work_dir, "webdav.log"))
    failures = []

    try:
        step("构造测试目录（%s）" % ("含 1.93 GB 大文件" if args.big else "常规"))
        root, written = build_fixture(src_root, args.big)
        src_digest, src_count = tree_digest(root)
        log("目录：%s" % root)
        for rel, size in written:
            log("  %-28s %s" % (rel, core.human_size(size)))
        log("合计 %d 个文件，指纹 %s" % (src_count, src_digest[:16]))

        step("启动本地 WebDAV（%s → %s）" % (url, repo_root))
        server.start()
        log("已就绪")

        step("VaultPack pack（区块 %d MB，压缩=%s）" % (chunk_mb, args.compress))
        write_settings(settings_path, url, work_dir, out_dir, chunk_mb)
        write_meta(meta_path)

        exe = os.path.join(HERE, "VaultPack", "bin", "Release", "VaultPack.exe")
        if not os.path.isfile(exe):
            raise SystemExit("先编译 VaultPack：dotnet build -c Release "
                             "-p:PlayniteDir=<Playnite目录> tools/VaultPack/VaultPack.csproj")

        pack = [
            exe,
            "pack",
            "--src", root,
            "--id", APP_ID,
            "--meta", meta_path,
            "--exe", "game.exe",
            "--chunk-size", str(chunk_mb),
            "--settings", settings_path,
            "--data", work_dir,
        ]
        if args.compress:
            pack.append("--compress")
        if args.big:
            pack.append("--force")

        result = subprocess.run(pack, capture_output=True)
        out = result.stdout.decode("utf-8", "replace")
        err = result.stderr.decode("utf-8", "replace")
        for line in digest_tool_output(out):
            log(line)

        if result.returncode != 0:
            failures.append("pack 失败（exit %d）：%s" % (result.returncode, err.strip()))
            log("--- mock 服务最后 20 行 ---")
            for line in server.tail_log().splitlines():
                log(line)
            raise SystemExit(report(failures, base, args.keep))

        step("检查仓库落盘结构")
        layout_problems, chunk_count = check_repo_layout(repo_root)
        mf_problems, manifest = check_manifest(repo_root)
        for p in layout_problems + mf_problems:
            failures.append(p)
            log("✗ " + p)
        if not layout_problems and not mf_problems:
            cross = len([f for f in (manifest.get("Files") or [])
                         if len(f.get("Pieces") or []) > 1])
            log("✓ %d 个区块，%d 个文件，其中 %d 个跨区块"
                % (chunk_count, len(manifest.get("Files") or []), cross))
            log("✓ 仓库路径全 ASCII，Id = %s" % APP_ID)
            log("✓ 元数据 InstallDirName = %s"
                % (manifest.get("Metadata") or {}).get("InstallDirName"))

        # 需求 #3 的正经断言：解包端应当用 InstallDirName 当子目录名，
        # 而不是 Playnite 里的中文显示名。
        apps = core.list_apps(core.HttpSource(url, "demo", "demo123", timeout=30))
        target = [a for a in apps if a.get("id") == APP_ID]
        if not target:
            failures.append("索引里找不到 %s" % APP_ID)
        else:
            folder = core.folder_for(target[0])
            if folder != INSTALL_DIR_NAME:
                failures.append("落盘目录名应为 %r，实际 %r" % (INSTALL_DIR_NAME, folder))
                log("✗ 目录名不对：%s" % folder)
            else:
                log("✓ 目录名取自 InstallDirName，未被中文显示名污染：%s" % folder)

        step("Python 解包器下载还原（4 路并发）")
        src = core.HttpSource(url, "demo", "demo123", timeout=30)
        rep = core.Reporter()
        core.unpack(src, APP_ID, out_dir, rep, None,
                    part_tmp=tmp_dir, chunk_workers=4)

        out_digest, out_count = tree_digest(out_dir)
        if out_digest != src_digest:
            failures.append("还原结果与原始目录不一致：%s != %s" % (out_digest, src_digest))
            log("✗ 指纹不一致")
        else:
            log("✓ %d 个文件逐字节一致（指纹 %s）" % (out_count, out_digest[:16]))

        # 临时目录应当已经被清空 —— 「下完立刻删」是需求 #4 的硬要求
        leftovers = []
        for dirpath, _dn, fn in os.walk(tmp_dir):
            leftovers += [os.path.join(dirpath, f) for f in fn]
        if leftovers:
            failures.append("临时目录里还留着 %d 个文件（应下完即删）" % len(leftovers))
            log("✗ 临时目录残留：%s" % ", ".join(os.path.basename(x) for x in leftovers[:5]))
        else:
            log("✓ 临时区块文件已全部清理")

        step("中断 → 续传")
        rmtree(out_dir)
        rmtree(tmp_dir)
        os.makedirs(tmp_dir, exist_ok=True)

        total = sum(int(c.get("StoredBytes") or 0) for c in manifest["Chunks"])
        interrupted = download_with_interrupt(url, out_dir, tmp_dir,
                                             max(1, int(total * 0.45)))
        if not interrupted:
            log("（跑得太快，没来得及中断 —— 本轮跳过续传断言）")
        else:
            journalled = load_journal(tmp_dir)
            log("已中断；日志记录已完成 %d/%d 个区块" % (journalled, len(manifest["Chunks"])))
            if journalled <= 0:
                failures.append("中断后没有留下续传日志")
                log("✗ 没有续传日志")
            else:
                log("✓ 留下了续传日志")

            rep2 = core.Reporter()
            core.unpack(core.HttpSource(url, "demo", "demo123", timeout=30),
                        APP_ID, out_dir, rep2, None,
                        part_tmp=tmp_dir, chunk_workers=4)
            out_digest2, out_count2 = tree_digest(out_dir)
            if out_digest2 != src_digest:
                failures.append("续传后结果不一致")
                log("✗ 续传后指纹不一致")
            else:
                log("✓ 续传后 %d 个文件逐字节一致" % out_count2)

        step("重新归档（换区块大小）→ 过期区块必须被回收")
        # 换区块大小会让所有区块哈希全变，旧块 100% 变成垃圾。
        # 如果不回收，chunks/ 里的文件数会近乎翻倍 —— 这条断言就是在盯这个。
        before = len([n for n in os.listdir(os.path.join(repo_root, "apps", APP_ID, "chunks"))])
        before_bytes = dir_bytes(os.path.join(repo_root, "apps", APP_ID, "chunks"))

        repack = list(pack)
        repack[repack.index("--chunk-size") + 1] = str(chunk_mb * 2)
        repack.append("--force")
        again = subprocess.run(repack, capture_output=True)
        if again.returncode != 0:
            failures.append("重新归档失败（exit %d）：%s"
                            % (again.returncode,
                               again.stderr.decode("utf-8", "replace").strip()))
            log("✗ 重新归档失败")
        else:
            for line in digest_tool_output(again.stdout.decode("utf-8", "replace")):
                log(line)
            after = len([n for n in os.listdir(os.path.join(repo_root, "apps", APP_ID, "chunks"))])
            after_bytes = dir_bytes(os.path.join(repo_root, "apps", APP_ID, "chunks"))
            if after > before * 1.3:
                failures.append("过期区块没被回收：改块大小前 %d 个，之后 %d 个" % (before, after))
                log("✗ 区块数从 %d 涨到 %d —— 旧块没清" % (before, after))
            else:
                log("✓ 区块数 %d → %d（块大小 %d→%d MB），仓库体积 %s → %s"
                    % (before, after, chunk_mb, chunk_mb * 2,
                       core.human_size(before_bytes), core.human_size(after_bytes)))

    finally:
        server.stop()

    return report(failures, base, args.keep)


def dir_bytes(path):
    total = 0
    for dirpath, _dn, fn in os.walk(path):
        for name in fn:
            try:
                total += os.path.getsize(os.path.join(dirpath, name))
            except OSError:
                pass
    return total


def digest_tool_output(raw):
    """把工具输出里的进度行压掉。

    PrintProgress 用 \\r 原地刷新，被抓进管道后每一帧都变成独立一行，
    223 MB 能刷出上千行。这里只保留每个 \\r 片段的末帧，并丢掉进度行本身。
    """
    lines = []
    last_progress = None
    for chunk in raw.split("\r"):
        for piece in chunk.split("\n"):
            text = piece.rstrip()
            if not text:
                continue
            if text.startswith(("归档", "安装", "修复")) and "/" in text:
                last_progress = text
                continue
            lines.append(text)
    if last_progress:
        lines.append("（进度行末帧）" + last_progress)
    return lines


def load_journal(tmp_dir):
    path = os.path.join(tmp_dir, "%s.chunks.json" % APP_ID)
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return len(json.load(fh).get("done") or [])
    except Exception:
        return 0


def report(failures, base, keep):
    print("")
    print("=" * 62)
    if failures:
        print("结果：失败（%d 项）" % len(failures))
        for i, text in enumerate(failures, 1):
            print("  %d) %s" % (i, text))
    else:
        print("结果：全部通过")
    print("=" * 62)

    if keep:
        print("临时目录保留在：" + base)
    else:
        rmtree(base)

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
