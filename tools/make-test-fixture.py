# -*- coding: utf-8 -*-
"""造一个极小的 Schema-2 本地归档，用来**离线**验证解包器（不连 NAS 也能测）。

用法：
    python tools/make-test-fixture.py [输出目录] [原始安装目录名]
    # 第二个参数会写进元数据的 InstallDirName（例：Brotato），
    # 不传就不写 —— 正好用来验证「旧归档没有这个字段时退回 app id」的路径。
    # 然后用 VaultUnpacker.exe 指向这个目录（本地来源）测一遍

覆盖：store 与 deflate 两种 Compression、子目录、中文/空格路径、多分片。
生成的 _expected/ 子目录是逐字节比对的参照物。
"""
import json
import os
import shutil
import sys
import tempfile
import zlib
from datetime import datetime, timezone

OUT = (sys.argv[1] if len(sys.argv) > 1
       else os.path.join(tempfile.gettempdir(), "vault-fixture"))
INSTALL_DIR_NAME = sys.argv[2] if len(sys.argv) > 2 else None
APP_ID = "fixture-app"

FILES = [
    (b"hello vault\n", "hello.txt", "store"),
    (bytes(range(256)) * 48, "sub/nested.bin", "store"),
    ("中文名 带空格.dat".encode("utf-8") + b"x" * 777, "中文名 带空格.dat", "deflate"),
    (b"compress me " * 500, "docs/deep/compressed.txt", "deflate"),
    (os.urandom(3000), "run.exe", "store"),
]

# 每个分片 4KB，逼出多分片路径
PART_SIZE = 4096


def main():
    root = OUT
    if os.path.isdir(root):
        shutil.rmtree(root)
    appdir = os.path.join(root, "apps", APP_ID)
    partsdir = os.path.join(appdir, "parts")
    os.makedirs(partsdir, exist_ok=True)

    # 贪心装箱：与插件一致 —— 按原始大小攒整文件，整文件不切
    parts = []          # list of bytearray
    entries = []
    cur = bytearray()
    cur_off = 0
    for blob, rel, comp in FILES:
        if comp == "deflate":
            co = zlib.compressobj(9, zlib.DEFLATED, -15)  # 裸 deflate
            stored = co.compress(blob) + co.flush()
        else:
            stored = blob
        if cur and len(cur) + len(stored) > PART_SIZE:
            parts.append(cur)
            cur = bytearray()
            cur_off = 0
        entries.append({
            "Path": rel, "Size": len(blob), "Part": len(parts),
            "Offset": cur_off, "StoredSize": len(stored),
            "Compression": comp, "IsStored": comp == "store",
        })
        cur += stored
        cur_off += len(stored)
    if cur:
        parts.append(cur)

    for i, buf in enumerate(parts):
        with open(os.path.join(partsdir, "part-%04d.bin" % i), "wb") as f:
            f.write(buf)

    total_raw = sum(e["Size"] for e in entries)
    manifest = {
        "Schema": 2, "Id": APP_ID, "Name": "夹具测试 Fixture App",
        "Version": "1.0", "UpdatedAt": _now(), "LaunchExe": "run.exe",
        "WorkingDir": "{InstallDir}", "TotalBytes": total_raw,
        "Packed": True, "PartSize": PART_SIZE,
        "Parts": [{"Index": i, "Path": "parts/part-%04d.bin" % i,
                   "StoredBytes": len(b), "RawBytes": len(b)}
                  for i, b in enumerate(parts)],
        "Files": entries,
        "Metadata": {"Name": "夹具测试 Fixture App", "Version": "1.0",
                     "InstallDirName": INSTALL_DIR_NAME},
    }
    with open(os.path.join(appdir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)

    index = {
        "Schema": 2, "Name": "Vault Store", "UpdatedAt": _now(),
        "Apps": [{"Id": APP_ID, "Name": manifest["Name"], "Version": "1.0",
                  "TotalBytes": total_raw, "FileCount": len(entries),
                  "LaunchExe": "run.exe", "PartCount": len(parts),
                  "Packed": True, "Metadata": manifest["Metadata"]}],
    }
    with open(os.path.join(root, "index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, ensure_ascii=False, indent=2)

    print("夹具已生成：%s" % root)
    print("  文件 %d 个 / 原始 %d 字节 / 分片 %d 个（每片上限 %d）"
          % (len(entries), total_raw, len(parts), PART_SIZE))
    for e in entries:
        print("   · %-24s raw=%-6d stored=%-6d part=%d off=%d %s"
              % (e["Path"], e["Size"], e["StoredSize"], e["Part"],
                 e["Offset"], e["Compression"]))

    # 顺便把期望内容写下来，方便逐字节比对
    exp = os.path.join(root, "_expected")
    for blob, rel, _c in FILES:
        t = os.path.join(exp, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(t), exist_ok=True)
        with open(t, "wb") as f:
            f.write(blob)
    print("  期望内容：%s" % exp)


def _now():
    n = datetime.now(timezone.utc)
    return "%s.%06d0Z" % (n.strftime("%Y-%m-%dT%H:%M:%S"), n.microsecond)


if __name__ == "__main__":
    main()
