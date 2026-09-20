# -*- coding: utf-8 -*-
"""外部配置文件 —— 个人设置一律放这里，不写进代码。

为什么要有这个东西：
    仓库地址、账号、密码、输出目录这些是「谁的机器谁的配置」，一旦写死在源码里，
    换台机器就要改代码、还会不小心把密码提交上仓库。所以统一落到一个 JSON 文件，
    程序启动时读它（没有就自动建一份带注释的模板），之后免配置直接用。

放哪儿：
    0) 环境变量 VAULT_UNPACKER_CONFIG 指定的路径 —— 测试隔离 / 多套配置切换用
    1) **exe（或源码 tools/）同目录的 config.json** —— 就这一个地方，便携
       没有就按默认值生成一个；目录不可写时只在内存里用默认值跑，不让程序起不来

    程序不往别处写配置（不碰 %APPDATA%），界面上也不专门提示路径 ——
    要改就用「编辑配置」按钮，它会用资源管理器定位到那个文件。

⚠️ 测试务必先设 VAULT_UNPACKER_CONFIG 指到临时目录，否则跑一遍测试就会把
自己真实的地址/密码/目录覆盖掉（这个坑实测踩过）。

关于密码：按需求存了，但它是**明文**。这个文件只放在自己机器上、别提交到仓库；
不想留明文就把 "webdav_password" 留空，每次启动手输。

文件里所有键都是平的、名字自解释，随手用记事本改就行，改完下次启动生效。
"""

import json
import os
import sys

FILE_NAME = "config.json"
# 旧名字，只在第一次启动时用来兜底迁移（读到就搬进 config.json）
LEGACY_FILE_NAMES = ("vault-unpacker.config.json",)
USER_DIR_NAME = "VaultUnpacker"
ENV_OVERRIDE = "VAULT_UNPACKER_CONFIG"

# 模板 = 全部默认值。新建配置时按这个写出来，用户照着填。
TEMPLATE = {
    "_readme": "Vault 解包器配置。可直接编辑，程序启动时读取，改完下次启动生效。"
               "密码是明文存储，仅放本机、勿提交到仓库。",
    "mode": "http",                  # http = WebDAV 仓库；local = 本地已下载目录
    "webdav_url": "",                # 例：https://host:5006/app/Store
    "webdav_user": "",
    "webdav_password": "",
    "webdav_insecure_tls": False,    # 自签证书时勾 true
    "local_dir": "",                 # local 模式：仓库根 / apps/ / apps/{id}/
    "output_parent_dir": "",         # 解包**父目录**，每个应用解到 <它>/<app_id>/
    "register_to_playnite": True,    # 解包后写 local-index.json
    "plugin_data_dir": "",           # 留空自动定位
    "playnite_dir": "",              # Playnite 安装目录（注入插件用），留空自动定位
    "last_app_id": "",               # 上次选中的应用，纯为方便
}

# 兼容旧版 state.json 的键名（老版本存在 APPDATA/VaultUnpacker/state.json）
_LEGACY_MAP = {
    "url": "webdav_url",
    "user": "webdav_user",
    "local_dir": "local_dir",
    "out_dir": "output_parent_dir",
    "mode": "mode",
    "plugin_dir": "plugin_data_dir",
}


def app_dir():
    """程序所在目录：冻结后是 exe 所在目录，源码运行时是 tools/（包的上一级）。"""
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def portable_path():
    """正式位置：exe（或源码 tools/）同目录的 config.json。"""
    return os.path.join(app_dir(), FILE_NAME)


def _legacy_portable_paths():
    """旧版配置文件的可能位置，只用于第一次启动时迁移。

    基准是**正式配置文件所在的目录**，而不是 app_dir()：生产环境两者相同
    （exe 同目录），但跑测试时正式路径被 VAULT_UNPACKER_CONFIG 指到临时目录，
    这时若还去 exe 旁边找旧文件，隔离就漏了 —— 临时目录里永远找不到，
    迁移逻辑也就永远测不到。
    """
    d = os.path.dirname(config_path())
    return [os.path.join(d, n) for n in LEGACY_FILE_NAMES]


def writable(d):
    """这个目录能不能新建文件（只读介质 / Program Files 会失败）。"""
    try:
        os.makedirs(d, exist_ok=True)
        probe = os.path.join(d, ".vault-write-test")
        with open(probe, "w") as f:
            f.write("")
        os.remove(probe)
        return True
    except OSError:
        return False


def override_path():
    """环境变量强制指定的配置文件路径（没有就返回 None）。

    用途：跑测试时指到临时目录，免得把真实配置覆盖掉；也可以拿它做
    「工作一套 / 家里一套」的多配置切换。
    """
    p = os.environ.get(ENV_OVERRIDE)
    return os.path.abspath(p) if p else None


def config_path():
    """永远是 exe 同目录的 config.json（环境变量可覆盖，供测试用）。"""
    forced = override_path()
    if forced:
        return forced
    return portable_path()


def merge_defaults(raw):
    """把读到的字典补齐成完整结构（多出来的键保留，方便用户自己加东西）。"""
    data = dict(TEMPLATE)
    if isinstance(raw, dict):
        for k, v in raw.items():
            data[k] = v
        if not data.get("mode"):
            data["mode"] = "http"
    return data


def _legacy_seed():
    """老版本把设置存在 state.json，第一次升级时拿来打底，避免又要重填。"""
    try:
        from . import playnite
    except Exception:
        return {}
    old = playnite.load_state() or {}
    seed = {}
    for old_key, new_key in _LEGACY_MAP.items():
        val = old.get(old_key)
        if val not in (None, ""):
            seed[new_key] = val
    return seed


def _read_json(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            return json.load(f)
    except Exception:
        return None


def ensure():
    """读 exe 同目录的 config.json；不存在就按默认值生成一个。

    返回 (路径, 配置字典, 是否新建)。第一次启动时会依次尝试：
      1) exe 同目录下的旧名字文件（vault-unpacker.config.json）—— 认出来直接搬，
         免得老用户升级后设置全丢
      2) 老版插件 state.json
    """
    path = config_path()

    if os.path.isfile(path):
        raw = _read_json(path)
        if raw is None:
            # 文件坏了：不覆盖（可能只是手改坏了一个逗号），读失败就用默认值跑，
            # 把坏文件原样留着，用户自己看得到问题。
            return path, merge_defaults({}), False
        return path, merge_defaults(raw), False

    # 没找到正式文件：先看旧名字那份能不能读
    data = None
    for old in _legacy_portable_paths():
        if os.path.isfile(old):
            raw = _read_json(old)
            if isinstance(raw, dict):
                data = merge_defaults(raw)
                break

    if data is None:
        data = merge_defaults(_legacy_seed())

    try:
        save(data, path)
        return path, data, True
    except OSError:
        # 写不进去（只读介质等）也不能让程序起不来，内存里用着就行
        return path, data, False


def save(data, path=None):
    path = path or config_path()
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")
    os.replace(tmp, path)
    return path


def open_in_explorer(path=None):
    """在资源管理器里定位配置文件，方便用户改。返回是否成功。"""
    path = path or config_path()
    try:
        if os.path.isfile(path):
            import subprocess
            subprocess.Popen(["explorer", "/select,", os.path.normpath(path)])
            return True
        d = os.path.dirname(path)
        if os.path.isdir(d):
            os.startfile(d)
            return True
    except Exception:
        pass
    return False
