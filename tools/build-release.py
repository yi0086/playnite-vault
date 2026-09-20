#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
打一个版本的发布产物（插件 zip + 解包器 exe + 中文说明）。

用法：
    python tools/build-release.py 1.5.0
    python tools/build-release.py 1.5.0 --out D:/somewhere/v1.5.0

产物（默认落在仓库**外面**的 ../release/v<版本>/，二进制不进 git）：
    PlayniteVault-<版本>.zip          插件包，用户解压后整个文件夹丢进 Extensions/
    VaultUnpacker-<版本>.exe      独立解包器（裸 exe，双击即用）
    使用说明.txt                   给用户看的纯文本说明
    stage/Playnite-Vault/       打 zip 之前的暂存目录

为什么要有这个脚本（而不是临时敲命令）：
  1. 说明文件必须**从 docs/plugin-usage.md 生成**。上一版是把说明手工另写了一份，
     结果那份文件的开头被重复写了几十遍（36 个重复标题），而仓库里的 md 是好的 ——
     两份内容各写各的，迟早对不上。现在只有一份源头，txt 是派生物。
  2. 中文文件名必须用 Python 的 zipfile 打包（它会给非 ASCII 名字设 UTF-8 标记
     flag 0x800）；PowerShell 的 Compress-Archive 不设，某些解压器里会变乱码。
  3. 说明文件必须写成 UTF-8 **带 BOM** + CRLF，否则中文 Windows 的记事本按 GBK
     解码，打开就是乱码。
  4. 只挑明确列出的文件进产物目录 —— 绝不整目录拷贝。tools/dist/ 里躺着一个
     带真实 NAS 地址与账号的 config.json，整目录拷贝会把个人信息一起发出去。
"""

import argparse
import hashlib
import io
import os
import re
import shutil
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

PLUGIN_DIR = os.path.join(ROOT, "src", "PlayniteVault")
YAML = os.path.join(PLUGIN_DIR, "extension.yaml")
DLL = os.path.join(PLUGIN_DIR, "bin", "Release", "PlayniteVault.dll")
ICON = os.path.join(PLUGIN_DIR, "icon.png")
UNPACKER = os.path.join(HERE, "dist", "VaultUnpacker.exe")
USAGE_MD = os.path.join(ROOT, "docs", "plugin-usage.md")

USAGE_TXT_NAME = "使用说明.txt"
ONLINE_DOC = ("https://github.com/yi0086/playnite-vault/blob/main/"
              "docs/plugin-usage.md")


def log(msg):
    print(msg, flush=True)


def fail(msg):
    log("错误：" + msg)
    sys.exit(1)


# --------------------------------------------------------------- 说明文件

def read_yaml_version(path):
    """从 extension.yaml 抠 Version: —— 版本号只认这一个来源，别两处各写一个。"""
    for line in io.open(path, encoding="utf-8").read().split("\n"):
        line = line.strip()
        if line.lower().startswith("version:"):
            return line.split(":", 1)[1].strip().strip("\"'")
    return None


def read_yaml_id(path):
    for line in io.open(path, encoding="utf-8").read().split("\n"):
        line = line.strip()
        if line.lower().startswith("id:"):
            return line.split(":", 1)[1].strip().strip("\"'")
    return None


def md_to_plain(md, version):
    """把 plugin-usage.md 转成适合记事本的纯文本，并加上文件头。"""
    out = []
    out.append("Vault 插件 —— 使用说明")
    out.append("版本 " + version)
    out.append("（在线版见 " + ONLINE_DOC + "）")
    out.append("")
    out.append("=" * 68)
    out.append("")

    for raw in md.split("\n"):
        line = raw.rstrip()

        # 代码块围栏直接去掉，里面的内容原样留着
        if line.strip().startswith("```"):
            continue

        # 标题：# / ## / ### —— 降级成纯文本层级
        m = re.match(r"^(#{1,6})\s+(.*)$", line)
        if m:
            level = len(m.group(1))
            text = m.group(2).strip()
            if level == 1:
                out.append("")
                out.append(text)
                out.append("=" * len(text))
            else:
                out.append("")
                out.append(text)
                out.append("-" * len(text))
            continue

        # 引用块：缩进四格
        if line.startswith(">"):
            out.append("    " + line.lstrip("> ").rstrip())
            continue

        # 行内标记去掉，保留可读文本
        line = re.sub(r"\*\*(.+?)\*\*", r"\1", line)
        line = re.sub(r"`(.+?)`", r"\1", line)
        line = line.replace("→", "->")

        out.append(line)

    text = "\n".join(out).rstrip() + "\n"
    # 连续 3 个以上空行压成一个空行，转换过程容易多出空行
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text


def write_usage_txt(path, version):
    md = io.open(USAGE_MD, encoding="utf-8").read()
    text = md_to_plain(md, version)
    # utf-8-sig = 带 BOM；newline="\r\n" 让每行都以 CRLF 落盘（记事本友好）
    with io.open(path, "w", encoding="utf-8-sig", newline="\r\n") as fh:
        fh.write(text)
    return text


# --------------------------------------------------------------- 打包

def sha1_of(path):
    h = hashlib.sha1()
    with open(path, "rb") as fh:
        while True:
            buf = fh.read(1 << 20)
            if not buf:
                break
            h.update(buf)
    return h.hexdigest().upper()


def md5_of(path):
    h = hashlib.md5()
    with open(path, "rb") as fh:
        while True:
            buf = fh.read(1 << 20)
            if not buf:
                break
            h.update(buf)
    return h.hexdigest().upper()


def build_zip(zip_path, stage_dir, folder):
    """打 zip。目录项也写进去（Playnite 用户习惯先看到文件夹）。"""
    if os.path.exists(zip_path):
        os.remove(zip_path)

    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(folder + "/", b"")

        for name in sorted(os.listdir(stage_dir)):
            src = os.path.join(stage_dir, name)
            if not os.path.isfile(src):
                continue
            # ZipInfo + UTF-8 flag：中文文件名必须显式标记，否则解压出来是乱码
            info = zipfile.ZipInfo(folder + "/" + name,
                                   date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.flag_bits |= 0x800
            with open(src, "rb") as fh:
                z.writestr(info, fh.read())

    with zipfile.ZipFile(zip_path) as z:
        bad = z.testzip()
        if bad is not None:
            fail("zip CRC 校验失败：" + bad)
        names = z.namelist()
        for n in names:
            if not n.isascii():
                info = z.getinfo(n)
                if not (info.flag_bits & 0x800):
                    fail("中文条目没设 UTF-8 标记：" + n)
    return names


# --------------------------------------------------------------- 主流程

def main():
    ap = argparse.ArgumentParser(description="打 Vault 的发布产物")
    ap.add_argument("version", help="版本号，例如 1.5.0")
    ap.add_argument("--out", help="输出目录（默认 ../release/v<版本>）")
    ap.add_argument("--skip-unpacker", action="store_true",
                    help="不复制解包器 exe（只重打插件包时用）")
    args = ap.parse_args()

    version = args.version.lstrip("vV")
    out_dir = args.out or os.path.join(os.path.dirname(ROOT), "release",
                                       "v" + version)

    yaml_version = read_yaml_version(YAML)
    if yaml_version != version:
        fail("extension.yaml 里是 %s，但命令行要打 %s —— 先把版本号统一"
             % (yaml_version, version))

    plugin_id = read_yaml_id(YAML)
    if not plugin_id:
        fail("extension.yaml 里读不到 Id")

    for path, what in ((DLL, "PlayniteVault.dll（先编译 Release）"),
                       (ICON, "icon.png"),
                       (YAML, "extension.yaml"),
                       (USAGE_MD, "docs/plugin-usage.md")):
        if not os.path.isfile(path):
            fail("缺 " + what + "：" + path)

    log("版本 %s，插件 Id %s" % (version, plugin_id))
    log("输出目录 " + out_dir)

    stage_root = os.path.join(out_dir, "stage")
    folder = plugin_id                      # 发布包里的文件夹名 = Playnite 要的格式
    stage_dir = os.path.join(stage_root, folder)

    # 每次重建：暂存目录里绝不能混进上一版的文件（比如已经被删掉的旧 dll）
    if os.path.isdir(stage_root):
        shutil.rmtree(stage_root)
    os.makedirs(stage_dir)
    os.makedirs(out_dir, exist_ok=True)

    # 1) 说明文件（从 docs/plugin-usage.md 生成，不手工另写一份）
    usage_path = os.path.join(stage_dir, USAGE_TXT_NAME)
    text = write_usage_txt(usage_path, version)
    log("说明文件 %d 字符（%d 行），UTF-8+BOM / CRLF"
        % (len(text), text.count("\n")))

    # 2) 其余三个文件
    shutil.copy2(DLL, os.path.join(stage_dir, "PlayniteVault.dll"))
    shutil.copy2(YAML, os.path.join(stage_dir, "extension.yaml"))
    shutil.copy2(ICON, os.path.join(stage_dir, "icon.png"))

    log("暂存目录内容：")
    for name in sorted(os.listdir(stage_dir)):
        log("  %-20s %8d 字节" % (name, os.path.getsize(os.path.join(stage_dir, name))))

    # 3) 打 zip
    zip_name = "PlayniteVault-%s.zip" % version
    zip_path = os.path.join(out_dir, zip_name)
    names = build_zip(zip_path, stage_dir, folder)
    log("打包 %s（%d 字节，%d 个条目）" % (zip_name, os.path.getsize(zip_path), len(names)))

    # zip 里必须有的三个东西，少一个用户就装不上
    for need in ("PlayniteVault.dll", "extension.yaml", "icon.png"):
        if not any(n.endswith("/" + need) for n in names):
            fail("zip 里缺少 " + need)

    # 4) 解包器 exe（只复制这一个文件，绝不整目录 —— dist/ 里有个带真实账号的 config.json）
    exe_name = "VaultUnpacker-%s.exe" % version
    if args.skip_unpacker:
        log("按要求跳过解包器")
    elif os.path.isfile(UNPACKER):
        shutil.copy2(UNPACKER, os.path.join(out_dir, exe_name))
        log("解包器 %s（%d 字节）" % (exe_name, os.path.getsize(os.path.join(out_dir, exe_name))))
    else:
        log("!! 没有 tools/dist/VaultUnpacker.exe —— 先跑 tools/build-vault-unpacker.py")

    # 5) 顺便把说明文件在输出目录根也留一份（方便手动建 release 时直接拖）
    shutil.copy2(usage_path, os.path.join(out_dir, USAGE_TXT_NAME))

    log("")
    log("=" * 68)
    for name in sorted(os.listdir(out_dir)):
        p = os.path.join(out_dir, name)
        if os.path.isfile(p):
            log("  %-26s %10d 字节  MD5 %s" % (name, os.path.getsize(p), md5_of(p)))
    log("=" * 68)
    log("发布说明请另写 release-notes-v%s.md 放进同一目录。" % version)
    return 0


if __name__ == "__main__":
    sys.exit(main())
