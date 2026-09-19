# -*- coding: utf-8 -*-
"""
Vault 解包核心 —— 不依赖 Playnite 插件，只用 Python 标准库。

从远端仓库（WebDAV）或本地已下载的 apps/{id}/ 目录，把归档还原成原始文件。

三种布局（**用字段推断，不要看 Schema 数值** —— 旧清单里根本没有这个字段）：

v1  Files=false：
    apps/{id}/files/<原路径>        逐个文件直传

v2  Parts 非空：
    apps/{id}/parts/part-0000.bin   裸字节流，一个文件不跨片
    blob = part[Offset .. Offset+StoredSize]

v3  Chunks 非空（内容寻址，当前版本）：
    apps/{id}/chunks/<sha1>.bin     文件名就是内容哈希，**一个文件可跨多个区块**
    每个 FileEntry 带 Pieces[]：{Chunk, Offset, Length, Size, FileOffset, Compression}
    块内偏移 Offset 决定从哪儿读，FileOffset 决定写到文件的哪个位置

Compression：
    null / "store"   → blob 就是文件内容
    "deflate"        → 裸 deflate（RFC 1951，无 zlib 头）解压
"""

import base64
import hashlib
import json
import os
import shutil
import ssl
import threading
import urllib.error
import urllib.parse
import urllib.request
import zlib
from concurrent.futures import ThreadPoolExecutor

CHUNK = 1 << 20
SCHEMA_V1_FILES = "files"

# 区块下载的默认并发与重试。
# 并发不要开太大：瓶颈通常在 NAS 的写入侧，而且峰值临时磁盘占用 ≈ 并发 × 区块大小。
DEFAULT_CHUNK_WORKERS = 4
DEFAULT_RETRIES = 3


class UnpackError(Exception):
    """可预期的业务错误，界面上直接展示 message 即可。"""


class Cancelled(Exception):
    """用户主动取消。"""


# --------------------------------------------------------------------------
# 日志 / 进度回调
# --------------------------------------------------------------------------

class Reporter(object):
    """解包过程的回调集合。GUI、CLI 各自实现需要的那部分。"""

    def log(self, msg):
        pass

    def progress(self, done, total, label=""):
        """done/total 单位为字节；total 为 0 表示进度未知。"""

    def stage(self, name):
        """进入一个粗粒度阶段（下载 / 解包 / 登记 Playnite），用于界面显示。"""


class NullReporter(Reporter):
    pass


# --------------------------------------------------------------------------
# 数据来源
# --------------------------------------------------------------------------

class Source(object):
    """统一读取接口：read() 取文本，download() 落盘并回报进度。"""

    kind = "?"

    def read(self, rel):
        raise NotImplementedError

    def read_json(self, rel):
        raw = self.read(rel)
        try:
            return json.loads(raw.decode("utf-8"))
        except ValueError as ex:
            raise UnpackError("不是合法的 JSON：%s（%s）" % (rel, ex))

    def download(self, rel, dest, on_bytes=None, cancel=None):
        raise NotImplementedError

    def manifest_rel(self, app_id):
        """清单文件的相对路径。仓库布局固定是 apps/{id}/manifest.json，
        但本地来源可以指向单个应用目录，那时要换成 manifest.json。"""
        return "apps/%s/manifest.json" % app_id

    def describe(self):
        return self.kind


class HttpSource(Source):
    """WebDAV / 静态 HTTP 仓库。"""

    kind = "WebDAV"

    def __init__(self, base, user=None, password=None, verify_tls=True, timeout=30):
        if not base:
            raise UnpackError("仓库地址不能为空")
        self.base = base.rstrip("/")
        self.timeout = timeout
        self.auth = None
        if user:
            token = base64.b64encode(("%s:%s" % (user, password or "")).encode("utf-8"))
            self.auth = "Basic " + token.decode("ascii")

        # 空 ProxyHandler 覆盖环境里的 HTTP(S)_PROXY：内网 NAS 不能被 Clash 之类截走
        handlers = [urllib.request.ProxyHandler({})]
        if self.base.lower().startswith("https") and not verify_tls:
            ctx = ssl.create_default_context()
            ctx.check_hostname = False
            ctx.verify_mode = ssl.CERT_NONE
            handlers.append(urllib.request.HTTPSHandler(context=ctx))
        self.opener = urllib.request.build_opener(*handlers)

    def _request(self, rel):
        url = self.base + "/" + urllib.parse.quote(rel)
        req = urllib.request.Request(url)
        if self.auth:
            req.add_header("Authorization", self.auth)
        return req

    def read(self, rel):
        try:
            with self.opener.open(self._request(rel), timeout=self.timeout) as resp:
                return resp.read()
        except urllib.error.HTTPError as ex:
            if ex.code in (401, 403):
                raise UnpackError("认证失败（HTTP %d）。请检查用户名 / 密码。" % ex.code)
            if ex.code == 404:
                raise UnpackError("仓库里没有这个文件：%s" % rel)
            raise UnpackError("请求失败 HTTP %d：%s" % (ex.code, rel))
        except urllib.error.URLError as ex:
            raise UnpackError("连不上仓库：%s\n（地址：%s）" % (ex.reason, self.base))
        except ssl.SSLError as ex:
            raise UnpackError("TLS 握手失败：%s\n若是自签证书，请勾选「跳过证书校验」。" % ex)

    def download(self, rel, dest, on_bytes=None, cancel=None):
        os.makedirs(os.path.dirname(dest) or ".", exist_ok=True)
        try:
            with self.opener.open(self._request(rel), timeout=self.timeout) as resp:
                total = int(resp.headers.get("Content-Length") or 0)
                done = 0
                with open(dest, "wb") as out:
                    while True:
                        if cancel is not None and cancel.is_set():
                            raise Cancelled()
                        blk = resp.read(CHUNK)
                        if not blk:
                            break
                        out.write(blk)
                        done += len(blk)
                        if on_bytes:
                            on_bytes(done, total)
                return done
        except urllib.error.HTTPError as ex:
            if ex.code in (401, 403):
                raise UnpackError("认证失败（HTTP %d）。请检查用户名 / 密码。" % ex.code)
            if ex.code == 404:
                raise UnpackError("仓库里没有这个文件：%s" % rel)
            raise UnpackError("下载失败 HTTP %d：%s" % (ex.code, rel))
        except urllib.error.URLError as ex:
            raise UnpackError("下载中断：%s" % (ex.reason,))

    def describe(self):
        return "WebDAV  %s" % self.base


class LocalSource(Source):
    """
    本地来源。相对路径的解析方式和 WebDAV 源保持一致 —— 都以「仓库根」为基准，
    所以清单里写的 `apps/{id}/manifest.json`、`apps/{id}/parts/part-0000.bin`
    必须原样能拼出来。可以指向：

      · 仓库根目录（里面有 index.json 和 apps/）—— 最直观，推荐
      · apps/ 目录本身（会自动回到它的上一级）
      · 单个应用的目录 apps/{id}/（里面有 manifest.json），此时会把
        `apps/{id}/` 前缀折掉，等价于只装了这一个应用的仓库
    """

    kind = "本地"

    def __init__(self, root):
        if not root or not os.path.isdir(root):
            raise UnpackError("目录不存在：%s" % root)
        root = os.path.abspath(root)
        self.given = root
        self.app_dir = None      # 指向 apps/{id}/ 时才有值
        self.app_id = None
        self.root = root         # 仓库根

        base = os.path.basename(os.path.normpath(root))
        if os.path.isfile(os.path.join(root, "manifest.json")):
            # 指向单个应用目录
            self.app_dir = root
            self.app_id = base
        elif base.lower() == "apps" and os.path.isdir(root):
            # 指向 apps/ 本身 → 回到仓库根
            self.root = os.path.dirname(root)
        elif (os.path.isfile(os.path.join(root, "index.json"))
              or os.path.isdir(os.path.join(root, "apps"))):
            self.root = root
        else:
            # 认不出来就按仓库根处理，报错信息里能看到实际拼的路径，便于排查
            self.root = root

    def _path(self, rel):
        rel = rel.replace("/", os.sep)
        if self.app_dir:
            parts = rel.split(os.sep)
            if len(parts) >= 3 and parts[0] == "apps":
                # apps/{id}/x  →  x
                rel = os.sep.join(parts[2:])
            elif parts and parts[0] == "index.json":
                raise UnpackError(
                    "这里指到的是单个应用目录（%s），里面没有仓库总索引 index.json。\n"
                    "请改指向它的上一级（含 apps/ 的那个目录）。" % self.app_dir)
            return os.path.join(self.app_dir, rel)
        return os.path.join(self.root, rel)

    def read(self, rel):
        p = self._path(rel)
        if not os.path.isfile(p):
            raise UnpackError("找不到文件：%s" % p)
        with open(p, "rb") as f:
            return f.read()

    def download(self, rel, dest, on_bytes=None, cancel=None):
        src = self._path(rel)
        if not os.path.isfile(src):
            raise UnpackError("找不到文件：%s" % src)
        os.makedirs(os.path.dirname(dest) or ".", exist_ok=True)
        total = os.path.getsize(src)
        done = 0
        with open(src, "rb") as fi, open(dest, "wb") as fo:
            while True:
                if cancel is not None and cancel.is_set():
                    raise Cancelled()
                blk = fi.read(CHUNK)
                if not blk:
                    break
                fo.write(blk)
                done += len(blk)
                if on_bytes:
                    on_bytes(done, total)
        return done

    def manifest_rel(self, app_id):
        if self.app_dir:
            return "manifest.json"
        return "apps/%s/manifest.json" % app_id

    def describe(self):
        if self.app_dir:
            return "本地  %s（单个应用目录）" % self.app_dir
        return "本地  %s" % self.root

    def candidate_dirs(self):
        """列出这个来源里看起来像应用的目录（含 manifest.json）。"""
        if self.app_dir:
            return [self.app_id]
        scan = os.path.join(self.root, "apps")
        if not os.path.isdir(scan):
            scan = self.root
        found = []
        try:
            for name in sorted(os.listdir(scan)):
                sub = os.path.join(scan, name)
                if os.path.isfile(os.path.join(sub, "manifest.json")):
                    found.append(name)
        except OSError:
            pass
        return found


# --------------------------------------------------------------------------
# 本地子目录名
# --------------------------------------------------------------------------

# Windows 不允许出现在文件名里的字符，以及保留设备名
_BAD_NAME_CHARS = set('<>:"/\\|?*')
_RESERVED_NAMES = {"CON", "PRN", "AUX", "NUL"}
_RESERVED_NAMES |= {"COM%d" % i for i in range(1, 10)}
_RESERVED_NAMES |= {"LPT%d" % i for i in range(1, 10)}


def sanitize_folder_name(name):
    """把元数据里的目录名清洗成「安全的单级目录名」；不合法返回 None。

    必须当成不可信输入：这个字段来自仓库里的 JSON，直接拼路径会被
    `..\\..\\Windows` 这种名字穿越出去。所以拒绝分隔符、保留名和全是点的名字。
    """
    if name is None:
        return None
    s = str(name).strip()
    if not s or s in (".", ".."):
        return None
    if any(ch in _BAD_NAME_CHARS or ord(ch) < 32 for ch in s):
        return None
    if s.split(".")[0].upper() in _RESERVED_NAMES:
        return None
    if len(s) > 128:
        return None
    return s


def folder_for(app):
    """这个应用该解到哪个子目录名。

    优先用归档时记下的**原始安装目录名**（元数据 InstallDirName，
    例如上传前装在 D:\\Games\\Brotato → "Brotato"），让解出来的目录
    和上传前一致；旧归档没这个字段，就退回 app id（仓库里的 slug）。
    """
    name = sanitize_folder_name(app.get("install_dir_name"))
    if name:
        return name
    return sanitize_folder_name(app.get("id")) or str(app.get("id") or "")


def layout_label(app):
    """列表里「布局」那一列该显示什么。

    v3 / v2 / v1 三种归档混在一个仓库里是常态（旧归档不必重打），
    所以列表必须一眼看出哪条是哪种格式。
    """
    chunks = int(app.get("chunk_count") or 0)
    if chunks:
        return "区块 %d" % chunks
    parts = int(app.get("part_count") or 0)
    if parts:
        return "分片 %d" % parts
    return "直传"


# --------------------------------------------------------------------------
# 仓库浏览
# --------------------------------------------------------------------------

def list_apps(source):
    """
    读仓库总索引 index.json。拿不到时就退化成扫描目录（对本地来源很有用）。
    返回 [ {id, name, version, total_bytes, file_count, launch_exe,
            part_count, chunk_count, packed, install_dir_name} ]
    """
    # 读不到 index.json 不算致命 —— 本地来源可以退化成扫目录。
    # 注意 here 不能 `except UnpackError: raise`：read_json 在「文件不存在」时
    # 抛的正是 UnpackError，一 raise 就永远走不到下面的退化路径了。
    index = None
    try:
        index = source.read_json("index.json")
    except Exception:
        index = None

    if index:
        apps = []
        for a in (index.get("Apps") or []):
            apps.append({
                "id": a.get("Id"),
                "name": a.get("Name") or a.get("Id"),
                "version": a.get("Version") or "",
                "total_bytes": int(a.get("TotalBytes") or 0),
                "file_count": int(a.get("FileCount") or 0),
                "launch_exe": a.get("LaunchExe") or "",
                "part_count": int(a.get("PartCount") or 0),
                "chunk_count": int(a.get("ChunkCount") or 0),
                "packed": bool(a.get("Packed")),
                "install_dir_name": _meta_value(a.get("Metadata"),
                                                "InstallDirName"),
            })
        if apps:
            return apps

    # 退化路径：本地目录扫描
    if isinstance(source, LocalSource):
        apps = []
        for app_id in source.candidate_dirs():
            try:
                mf = source.read_json(source.manifest_rel(app_id))
            except UnpackError:
                continue
            apps.append({
                "id": mf.get("Id") or app_id,
                "name": mf.get("Name") or app_id,
                "version": mf.get("Version") or "",
                "total_bytes": int(mf.get("TotalBytes") or 0),
                "file_count": len(mf.get("Files") or []),
                "launch_exe": mf.get("LaunchExe") or "",
                "part_count": len(mf.get("Parts") or []),
                "chunk_count": len(mf.get("Chunks") or []),
                "packed": bool(mf.get("Packed")),
                "install_dir_name": _meta_value(mf.get("Metadata"),
                                                "InstallDirName"),
            })
        if apps:
            return apps

    raise UnpackError("读不到仓库索引 index.json，也没有可识别的应用目录。")


def _meta_value(meta, key):
    """从 Metadata 子对象里取一个字段（大小写不敏感），没有就 None。"""
    if not isinstance(meta, dict):
        return None
    if key in meta:
        return meta[key]
    low = key.lower()
    for k, v in meta.items():
        if isinstance(k, str) and k.lower() == low:
            return v
    return None


def fetch_manifest(source, app_id):
    return source.read_json(source.manifest_rel(app_id))


# --------------------------------------------------------------------------
# 解包
# --------------------------------------------------------------------------

def _is_stored(entry):
    comp = entry.get("Compression")
    if comp is None or comp == "":
        return True
    return comp == "store"


def _inflate_raw(data):
    try:
        return zlib.decompressobj(-15).decompress(data)
    except zlib.error as ex:
        raise UnpackError("解压失败（应为裸 deflate）：%s" % ex)


def _safe_target(out_dir, rel):
    """防目录穿越：清单里的路径不允许跑到输出目录之外。"""
    rel = rel.replace("\\", "/").lstrip("/")
    target = os.path.normpath(os.path.join(out_dir, rel.replace("/", os.sep)))
    root = os.path.normpath(out_dir)
    if target != root and not target.startswith(root + os.sep):
        raise UnpackError("清单里有越界路径，已拒绝：%s" % rel)
    return target


def _write_file(target, blob):
    os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
    with open(target, "wb") as f:
        f.write(blob)


def unpack(source, app_id, out_dir, reporter=None, cancel=None,
           keep_parts=False, part_tmp=None,
           chunk_workers=None, retries=DEFAULT_RETRIES):
    """
    把 apps/{app_id} 还原到 out_dir。
    返回 {files, bytes, parts, chunks, manifest, out_dir, mode}

    布局由清单字段推断：Chunks 非空 → v3；Packed + Parts → v2；否则 v1。
    **不要看 Schema 数值**：v1/v2 的清单里没有这个字段。
    """
    rep = reporter or NullReporter()
    manifest = fetch_manifest(source, app_id)

    packed = bool(manifest.get("Packed"))
    files = manifest.get("Files") or []
    parts = manifest.get("Parts") or []
    chunks = manifest.get("Chunks") or []

    rep.log("应用      : %s (%s)" % (manifest.get("Name"), app_id))
    rep.log("Schema    : %s   Packed=%s" % (manifest.get("Schema"), packed))
    rep.log("文件数    : %d" % len(files))
    rep.log("原始总量  : %.1f MB" % ((manifest.get("TotalBytes") or 0) / 1048576.0))

    if not files:
        raise UnpackError("清单里没有任何文件记录，这个归档可能是空的。")

    os.makedirs(out_dir, exist_ok=True)

    if chunks:
        return _unpack_chunks(source, app_id, manifest, files, chunks, out_dir,
                              rep, cancel, keep_parts, part_tmp,
                              chunk_workers or DEFAULT_CHUNK_WORKERS, retries)

    if packed:
        return _unpack_packed(source, app_id, manifest, files, parts, out_dir,
                              rep, cancel, keep_parts, part_tmp)
    return _unpack_plain(source, app_id, manifest, files, out_dir, rep, cancel)


# --------------------------------------------------------------------------
# v3：内容寻址区块
# --------------------------------------------------------------------------

def _piece_is_stored(piece):
    comp = piece.get("Compression")
    return comp is None or comp == "" or comp == "store"


def _manifest_stamp(manifest):
    """清单指纹。区块表本身变了才算「换了一份归档」，此时旧进度作废。"""
    ids = [str(c.get("Id") or "") for c in (manifest.get("Chunks") or [])]
    raw = "|".join(ids) + "|" + str(manifest.get("TotalBytes") or 0)
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()


def _journal_path(part_tmp, app_id):
    safe = "".join(ch for ch in str(app_id) if ch.isalnum() or ch in "-_")
    return os.path.join(part_tmp, (safe or "app") + ".chunks.json")


def _load_journal(path, stamp):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
    except Exception:
        return set()
    if not isinstance(data, dict) or data.get("stamp") != stamp:
        return set()
    return set(str(x) for x in (data.get("done") or []))


def _save_journal(path, stamp, done_ids):
    tmp = path + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as fh:
            json.dump({"stamp": stamp, "done": sorted(done_ids)}, fh)
        os.replace(tmp, path)
    except OSError:
        pass


def _unpack_chunks(source, app_id, manifest, files, chunks, out_dir, rep, cancel,
                   keep_chunks, part_tmp, workers, retries):
    """v3 内容寻址区块布局。

    和 v2 的两处关键区别：

    1. **一个文件可以跨多个区块**，所以还原不是「按分片顺序喂文件」，而是
       「按区块把它里面的片段各写一段」，落点由 Piece.FileOffset 决定。
       正因为如此，落盘必须用 seek 随机写：并发下载时区块是乱序完成的，
       同一个文件的后半段完全可能比前半段先到。
    2. **区块下载完就立刻解包并删掉临时文件**。峰值临时占用只有
       「并发数 × 区块大小」（默认 4 × 32MB ≈ 128MB），而不是整个应用的大小。

    断点续传靠 part_tmp 下的一份小日志（记「哪些区块已经解完」）：
    区块文件本身不保留，所以「续传」是「跳过已完成区块」而不是「续传半个区块」。
    """
    if not chunks:
        raise UnpackError("清单里没有区块表，无法按 v3 布局解包。")

    part_tmp = part_tmp or _default_part_tmp()
    os.makedirs(part_tmp, exist_ok=True)

    # 区块下标 → 落在里面的 (文件, 片段)
    pieces_by_chunk = {}
    for f in files:
        for piece in (f.get("Pieces") or []):
            pieces_by_chunk.setdefault(int(piece.get("Chunk") or 0), []).append((f, piece))

    # 空文件：v3 里它不会产生任何片段，得先把空壳建出来，
    # 否则最后自检会报「缺失」，文件数也会少数一个。
    zero_targets = set()
    for f in files:
        if int(f.get("Size") or 0) == 0:
            target = _safe_target(out_dir, f.get("Path") or "")
            os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
            if not os.path.exists(target):
                open(target, "wb").close()
            zero_targets.add(target)

    ordered = sorted(chunks, key=lambda c: int(c.get("Index") or 0))
    total = sum(int(c.get("StoredBytes") or 0) for c in ordered) or 1

    stamp = _manifest_stamp(manifest)
    journal = _journal_path(part_tmp, app_id)
    done_ids = _load_journal(journal, stamp) if not keep_chunks else set()

    done_ids = set(cid for cid in done_ids
                   if any(str(c.get("Id")) == cid for c in ordered))
    if done_ids:
        rep.log("")
        rep.log("续传：已完成的 %d 个区块（%.1f MB）会跳过。"
                % (len(done_ids), sum(int(c.get("StoredBytes") or 0) for c in ordered
                                      if str(c.get("Id")) in done_ids) / 1048576.0))

    pending = [c for c in ordered if str(c.get("Id")) not in done_ids]

    rep.stage("下载并解包")

    # 每个区块对总进度贡献多少字节。**用单调递增的方式记账**：
    # 并发下载时区块是乱序完成的，若按「已完成累计 + 在传部分」现算，
    # 某个区块下载完的那一刻它会从「在传」挪到「已完成」，
    # 总数就会往回掉一截 —— 进度条会肉眼可见地倒退。
    lock = threading.Lock()
    progress_map = {}
    finished = {"n": 0}
    written_files = set()

    # 跳过的区块直接记满格，这样进度一开始反映的是真实位置
    for c in ordered:
        cid = str(c.get("Id"))
        if cid in done_ids:
            progress_map[cid] = int(c.get("StoredBytes") or 0)
            finished["n"] += 1

    def report(text):
        with lock:
            cur = sum(progress_map.values())
            n = finished["n"]
        rep.progress(min(cur, total), total, text)

    report("准备中")

    def handle(chunk):
        _check_cancel(cancel)

        idx = int(chunk.get("Index") or 0)
        cid = str(chunk.get("Id") or "")
        stored = int(chunk.get("StoredBytes") or 0)
        rel = chunk.get("Path") or ("chunks/%s.bin" % cid)
        local = os.path.join(part_tmp, "%s.bin" % cid)

        def on_bytes(d, _t, _cid=cid, _idx=idx):
            # 用 max 收紧：重试时已经传过的字节不该让进度条往回缩
            with lock:
                progress_map[_cid] = max(progress_map.get(_cid, 0),
                                         min(int(d), stored))
            report("区块 %d/%d 下载中" % (_idx + 1, len(ordered)))

        last_error = None
        for attempt in range(1, max(1, retries) + 1):
            _check_cancel(cancel)
            try:
                source.download("apps/%s/%s" % (app_id, rel), local, on_bytes, cancel)
                break
            except Cancelled:
                _quiet_remove(local)
                raise
            except Exception as ex:          # noqa: BLE001 —— 网络层什么都可能抛
                last_error = ex
                _quiet_remove(local)
                if attempt >= max(1, retries):
                    raise UnpackError("区块 %s 下载失败（重试 %d 次）：%s"
                                      % (cid[:12], attempt, ex))
                rep.log("  区块 %s 第 %d 次失败，重试：%s" % (cid[:12], attempt, ex))

        if last_error is not None and not os.path.isfile(local):
            raise UnpackError("区块 %s 下载失败：%s" % (cid[:12], last_error))

        actual = os.path.getsize(local)
        if stored and actual != stored:
            _quiet_remove(local)
            raise UnpackError("区块大小不符：%s 期望 %d 实际 %d" % (cid[:12], stored, actual))

        # 内容寻址的额外好处：可以顺手校验哈希。文件名叫什么，内容就该是什么。
        if cid:
            digest = _hash_file(local)
            if digest != cid:
                _quiet_remove(local)
                raise UnpackError("区块哈希不符：%s 实际 %s（传输损坏或服务端改过文件）"
                                  % (cid[:12], digest[:12]))

        # 立刻解包：把这一块里的所有片段写到各自文件的目标偏移上
        try:
            with open(local, "rb") as cf:
                for f, piece in sorted(pieces_by_chunk.get(idx, []),
                                       key=lambda t: int(t[1].get("Offset") or 0)):
                    blob, expect = _read_piece(cf, piece, cid, f.get("Path"))
                    target = _safe_target(out_dir, f.get("Path") or "")
                    _write_piece(target, piece, blob)

                    # 记的是**文件个数**，不是片段个数：一个文件跨多个区块时
                    # 每个区块里都有一段，按片段数就会多算好几倍。
                    written_files.add(target)

                    if len(written_files) % 20 == 0:
                        report("解包 %s" % os.path.basename(f.get("Path") or ""))
        finally:
            if not keep_chunks:
                _quiet_remove(local)

        with lock:
            progress_map[cid] = stored
            finished["n"] += 1
            if not keep_chunks:
                done_ids.add(cid)
                _save_journal(journal, stamp, done_ids)
        report("已完成 %d/%d 个区块" % (finished["n"], len(ordered)))

    if pending:
        workers = max(1, min(int(workers), 8))
        if workers == 1:
            for chunk in pending:
                handle(chunk)
        else:
            # 用线程池而不是 asyncio：Source.download 是同步阻塞的 urllib，
            # 多线程最省事，而且 GIL 在等 IO 时会释放，并发是真实有效的。
            with ThreadPoolExecutor(max_workers=workers) as pool:
                futures = [pool.submit(handle, c) for c in pending]
                for fut in futures:
                    fut.result()   # 第一个异常在这里抛出，其余任务随 with 退出被收走

    rep.stage("完成")
    rep.progress(total, total, "完成")

    if not keep_chunks:
        _quiet_remove(journal)

    written_all = set(written_files) | zero_targets
    total_bytes_written = 0
    for path in written_all:
        try:
            total_bytes_written += os.path.getsize(path)
        except OSError:
            pass

    return {
        "files": len(written_all),
        "bytes": total_bytes_written,
        "parts": 0,
        "chunks": len(ordered),
        "manifest": manifest,
        "out_dir": out_dir,
        "mode": "chunks",
    }


def _hash_file(path):
    h = hashlib.sha1()
    with open(path, "rb") as fh:
        while True:
            buf = fh.read(CHUNK)
            if not buf:
                break
            h.update(buf)
    return h.hexdigest()


def _read_piece(chunk_file, piece, cid, rel):
    """从区块文件里读出一个片段，必要时解压，并核对还原后的大小。"""
    offset = int(piece.get("Offset") or 0)
    length = int(piece.get("Length") or 0)

    chunk_file.seek(offset)
    blob = chunk_file.read(length)
    if len(blob) != length:
        raise UnpackError("从区块 %s 读取越界（文件 %s）" % (cid[:12], rel))

    if not _piece_is_stored(piece):
        blob = _inflate_raw(blob)

    expect = int(piece.get("Size") or 0)
    if len(blob) != expect:
        raise UnpackError("解出大小不符：%s 期望 %d 实际 %d" % (rel, expect, len(blob)))

    return blob, expect


def _write_piece(target, piece, blob):
    """把片段写到文件的目标偏移。

    **不能用 append**：并发下载会让同一个文件的后半段先到，
    append 会把顺序搞反。必须用 r+b + seek 随机写。
    """
    os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
    if not os.path.exists(target):
        open(target, "wb").close()

    with open(target, "r+b") as out:
        out.seek(int(piece.get("FileOffset") or 0))
        out.write(blob)


def _unpack_packed(source, app_id, manifest, files, parts, out_dir, rep, cancel,
                   keep_parts, part_tmp):
    if not parts:
        raise UnpackError("清单标了 Packed=true，但没有分片表，无法解包。")

    part_tmp = part_tmp or _default_part_tmp()
    os.makedirs(part_tmp, exist_ok=True)

    by_part = {}
    for f in files:
        by_part.setdefault(f.get("Part"), []).append(f)

    total = sum((p.get("StoredBytes") or 0) for p in parts) or 1
    base = 0                       # 已完成分片的累计字节
    rep.stage("下载")
    rep.progress(0, total, "准备中")

    written_files = 0
    written_bytes = 0

    for part in sorted(parts, key=lambda p: p.get("Index") or 0):
        _check_cancel(cancel)
        idx = part.get("Index") or 0
        entries = by_part.get(idx, [])
        if not entries:
            continue

        stored = int(part.get("StoredBytes") or 0)
        local_part = os.path.join(part_tmp, os.path.basename(part.get("Path") or "part.bin"))

        rep.log("")
        rep.log("[分片 %d/%d] %s  (%.1f MB)"
                % (idx + 1, len(parts), part.get("Path"), stored / 1048576.0))

        def on_bytes(done, _t, _base=base, _idx=idx, _n=len(parts)):
            # 下载占整体进度的绝大部分，按累计字节直接推
            rep.progress(_base + done, total, "分片 %d/%d 下载中" % (_idx + 1, _n))

        try:
            source.download("apps/%s/%s" % (app_id, part.get("Path")),
                            local_part, on_bytes, cancel)
        except Cancelled:
            _quiet_remove(local_part)
            raise
        except UnpackError:
            _quiet_remove(local_part)
            raise

        actual = os.path.getsize(local_part)
        if stored and actual != stored:
            _quiet_remove(local_part)
            raise UnpackError("分片大小不符：%s 期望 %d 实际 %d"
                              % (part.get("Path"), stored, actual))

        rep.stage("解包")
        with open(local_part, "rb") as pf:
            for n, entry in enumerate(sorted(entries, key=lambda e: e.get("Offset") or 0), 1):
                _check_cancel(cancel)
                pf.seek(int(entry.get("Offset") or 0))
                blob = pf.read(int(entry.get("StoredSize") or 0))
                if len(blob) != int(entry.get("StoredSize") or 0):
                    raise UnpackError("从分片里读取越界：%s" % entry.get("Path"))
                if not _is_stored(entry):
                    blob = _inflate_raw(blob)

                expect = int(entry.get("Size") or 0)
                if len(blob) != expect:
                    raise UnpackError("解出大小不符：%s 期望 %d 实际 %d"
                                      % (entry.get("Path"), expect, len(blob)))

                _write_file(_safe_target(out_dir, entry.get("Path") or ""), blob)
                written_files += 1
                written_bytes += len(blob)

                if n % 20 == 0 or n == len(entries):
                    rep.progress(base + stored, total,
                                 "解包 %s（%d/%d）" % (os.path.basename(entry.get("Path") or ""),
                                                     n, len(entries)))

        base += stored
        rep.progress(base, total, "分片 %d/%d 完成" % (idx + 1, len(parts)))
        rep.log("           解出 %d 个文件" % len(entries))

        if not keep_parts:
            _quiet_remove(local_part)

    rep.stage("完成")
    rep.progress(total, total, "完成")
    return {
        "files": written_files,
        "bytes": written_bytes,
        "parts": len(parts),
        "manifest": manifest,
        "out_dir": out_dir,
        "mode": "packed",
    }


def _unpack_plain(source, app_id, manifest, files, out_dir, rep, cancel):
    """v1 逐文件布局：apps/{id}/files/<原路径>。"""
    rep.log("")
    rep.log("这是 v1 逐文件布局（Packed=false），按文件逐个取回。")

    total = sum(int(f.get("Size") or 0) for f in files) or 1
    done = 0
    written = 0
    rep.stage("下载并写入")

    for f in files:
        _check_cancel(cancel)
        rel = f.get("Path") or ""
        target = _safe_target(out_dir, rel)
        os.makedirs(os.path.dirname(target) or ".", exist_ok=True)

        tmp = target + ".vaultpart"

        def on_bytes(d, _t, _base=done, _rel=rel):
            rep.progress(_base + d, total, "下载 %s" % os.path.basename(_rel))

        source.download("apps/%s/%s/%s" % (app_id, SCHEMA_V1_FILES, rel), tmp, on_bytes, cancel)
        os.replace(tmp, target)

        size = os.path.getsize(target)
        expect = int(f.get("Size") or 0)
        if size != expect:
            raise UnpackError("大小不符：%s 期望 %d 实际 %d" % (rel, expect, size))

        done += size
        written += 1
        rep.progress(done, total, "已取回 %d/%d" % (written, len(files)))

    rep.stage("完成")
    rep.progress(total, total, "完成")
    return {
        "files": written,
        "bytes": done,
        "parts": 0,
        "manifest": manifest,
        "out_dir": out_dir,
        "mode": "plain",
    }


# --------------------------------------------------------------------------
# 工具
# --------------------------------------------------------------------------

def _check_cancel(cancel):
    if cancel is not None and cancel.is_set():
        raise Cancelled()


def _quiet_remove(path):
    try:
        if path and os.path.isfile(path):
            os.remove(path)
    except OSError:
        pass


def _default_part_tmp():
    base = os.environ.get("TEMP") or os.environ.get("TMPDIR") or "/tmp"
    return os.path.join(base, "vault-parts")


def human_size(n):
    n = float(n or 0)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or unit == "TB":
            return ("%.0f %s" % (n, unit)) if unit == "B" else ("%.1f %s" % (n, unit))
        n /= 1024.0


def verify_dir(out_dir, manifest, reporter=None):
    """
    解包后自检：按清单核对每个文件是否存在、大小是否一致。
    返回 (ok_count, problems[list of str])
    """
    rep = reporter or NullReporter()
    problems = []
    ok = 0
    for f in (manifest.get("Files") or []):
        rel = f.get("Path") or ""
        target = _safe_target(out_dir, rel)
        if not os.path.isfile(target):
            problems.append("缺失：%s" % rel)
            continue
        if os.path.getsize(target) != int(f.get("Size") or 0):
            problems.append("大小不符：%s" % rel)
            continue
        ok += 1
    return ok, problems


def dir_size(path):
    total = 0
    for dp, _dn, fn in os.walk(path):
        for f in fn:
            try:
                total += os.path.getsize(os.path.join(dp, f))
            except OSError:
                pass
    return total
