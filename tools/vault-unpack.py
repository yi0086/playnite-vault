#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
vault-unpack —— 命令行解包 Vault 仓库的分片归档（不需要 Playnite 插件）。

这只是个入口壳，真正的实现在同目录的 vault_unpacker/ 包里，
和图形界面（VaultUnpacker.py）共用同一套逻辑 —— 只有一份解包代码，不会两头跑偏。

为什么能独立解包：
Vault 的分片格式刻意做得极简 —— part-NNNN.bin 只是「文件字节的顺次拼接」，
分片文件本身没有任何索引头；每个文件在哪里由 manifest.json 里的
Part / Offset / StoredSize 描述。所以任何能「从偏移量读 N 个字节」的工具都能解。

用法示例：
    # 列仓库里有哪些应用
    python vault-unpack.py --base https://host:5006/app/Store --user u --password p --list

    # 解包某个应用（内网会自动绕过系统代理）
    python vault-unpack.py --id adofai-test \
        --base https://host:5006/app/Store --user u --password p \
        --out "D:/Games/A Dance of Fire and Ice"

    # 从本地已下载的 apps/ 目录解包，并登记到 Playnite
    python vault-unpack.py --id adofai-test --dir D:/repo/apps --out ./out --register

完整参数见 --help。
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from vault_unpacker.cli import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
