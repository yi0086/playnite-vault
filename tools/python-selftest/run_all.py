# -*- coding: utf-8 -*-
"""一次跑完 Python 侧的全部自检。

为什么要有这个：
解包器这块的坑几乎都在「跟真实系统打交道」的地方 —— 定位 Playnite、判断它在不在跑、
写文件前先关它、GUI 布局有没有被改坏。这些用单元测试测不出来，只能真跑。
所以每个脚本都是「真建窗口、真起进程、真写文件」，这个 runner 把它们串起来，
并且**退出码 0 才代表全过**。

用法：
    python tools/python-selftest/run_all.py
    python tools/python-selftest/run_all.py --only inject

注意：`test_gui.py` 需要**带 tkinter 的解释器**。当前解释器没有的话，
可以设 `VAULT_TK_PYTHON` 指向一个有的；实在没有就跳过那一项（不算失败）。
"""

import argparse
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

RUNS = [
    ("entry", "test_entry.py",
     "exe 入口分流：带参数走命令行（注入插件），不带参数才开界面"),
    ("inject", "test_inject.py",
     "inject.py：定位 Playnite / 注入 / 拒绝坏包 / 路径穿越"),
    ("e2e", "test_inject_e2e.py",
     "注入链路端到端：命令行注入 + 真实 WM_CLOSE 优雅关闭"),
    ("gui", "test_gui.py",
     "GUI 冒烟：真把主窗口和插件安装窗口建出来"),
]


def tk_python():
    """找一个带 tkinter 的解释器给 GUI 测试用。找不到返回 None。"""
    for cand in (sys.executable, os.environ.get("VAULT_TK_PYTHON")):
        if not cand or not os.path.isfile(cand):
            continue
        probe = subprocess.run([cand, "-c", "import tkinter"],
                               capture_output=True)
        if probe.returncode == 0:
            return cand
    return None


def main():
    # 子进程直接继承 stdout，而父进程的 print 默认是块缓冲 —— 不改成行缓冲的话，
    # 子进程的输出会整体跑到父进程的标题前面，日志看起来是乱的。
    try:
        sys.stdout.reconfigure(line_buffering=True)
    except Exception:
        pass

    ap = argparse.ArgumentParser(description="跑完 Python 侧的全部自检")
    ap.add_argument("--only", help="只跑某一项（inject / e2e / gui）")
    args = ap.parse_args()

    tk = tk_python()
    results = []

    for key, script, title in RUNS:
        if args.only and args.only != key:
            continue

        print("")
        print("=" * 66)
        print("  %s" % title)
        print("=" * 66)

        python = sys.executable
        if key == "gui":
            if not tk:
                print("  [跳过] 找不到带 tkinter 的解释器；"
                      "设 VAULT_TK_PYTHON 指向一个即可。")
                results.append((key, None))
                continue
            python = tk

        code = subprocess.call([python, os.path.join(HERE, script)], cwd=HERE)
        results.append((key, code))

    print("")
    print("=" * 66)
    print("  汇总")
    print("=" * 66)
    bad = 0
    for key, code in results:
        if code is None:
            print("  -  %-7s 跳过" % key)
        elif code == 0:
            print("  ok %-7s 全过" % key)
        else:
            print("  !! %-7s 失败（退出码 %s）" % (key, code))
            bad += 1

    if bad:
        print("")
        print("有 %d 项没过。" % bad)
        return 1
    print("")
    print("全部通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
