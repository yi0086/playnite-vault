# -*- coding: utf-8 -*-
"""
Vault 解包器 —— 不依赖 Playnite 插件地把 NAS 上的分片归档还原成本地文件。

模块划分：
    core.py      数据来源（WebDAV / 本地）+ 清单解析 + 分片解包 + 自检
    playnite.py  定位插件数据目录、读写 local-index.json（登记「已安装」）
    config.py    外部配置文件（仓库地址/账号/密码/目录，不进代码）
    cli.py       命令行入口
    gui.py       tkinter 图形界面
    icon.py      界面/窗口/任务栏图标（内嵌 base64 PNG，由 make-app-icon.py 生成）

CLI：  python -m vault_unpacker.cli --help
GUI：  python -m vault_unpacker.gui
"""

__version__ = "1.3.0"
__all__ = ["core", "playnite", "config", "cli", "gui", "icon"]
