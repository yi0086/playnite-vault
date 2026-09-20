# -*- coding: utf-8 -*-
"""解包器「入口分流」的自检。

为什么单独测这个：
`tools/VaultUnpacker.py` 是打包成 exe 的入口，它要区分「双击 = 开界面」和
「带参数 = 跑命令行」（注入插件、列仓库、解包都走命令行）。
早先这里只认 `--selftest`，于是 `VaultUnpacker.exe --inject-plugin` 会**静默弹出图形界面** ——
脚本里看起来像「命令跑完了但什么都没发生」，很难查。所以这条分流必须有回归测试。

全部在临时目录/内存里跑，不开窗口、不碰真实 Playnite。
"""

import importlib.util
import io
import os
import sys
import tempfile
import types

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TOOLS = os.path.join(ROOT, "tools")
sys.path.insert(0, TOOLS)

ok = fail = 0


def check(cond, label, detail=""):
    global ok, fail
    if cond:
        ok += 1
        print("  [通过] " + label)
    else:
        fail += 1
        print("  [失败] " + label + (" —— " + detail if detail else ""))


# 注意：导入本文件只会执行到模块级代码，`if __name__ == "__main__"` 挡住了 main()，
# 所以不会真的开界面。
spec = importlib.util.spec_from_file_location(
    "vault_unpacker_entry", os.path.join(TOOLS, "VaultUnpacker.py"))
entry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(entry)

print("== 参数分流 ==")
check(entry._is_cli_invocation([]) is False, "不带参数 → 图形界面")
check(entry._is_cli_invocation(["--inject-plugin"]) is True,
      "--inject-plugin 认得出是命令行")
check(entry._is_cli_invocation(["--plugin-source=gitee", "--inject-plugin"]) is True,
      "--key=value 这种写法也认得出")
check(entry._is_cli_invocation(["--list"]) is True, "--list 认得出是命令行")
check(entry._is_cli_invocation(["--playnite-dir", "D:/Game/Playnite", "--inject-plugin"])
      is True, "带路径值的组合也认得出")
check(entry._is_cli_invocation(["--selftest"]) is False,
      "--selftest 不算普通命令行（它有自己的分支）")
check(entry._is_cli_invocation(["--help"]) is True,
      "--help 走命令行的帮助，不会弹出主界面")
check(entry._is_cli_invocation(["-h"]) is True, "-h 同上")
check(entry._is_cli_invocation(["--typo-flag"]) is True,
      "不认识的开关也当命令行（让 argparse 报错，而不是静默开界面）")

print("")
print("== 冻结产物自检清单 ==")
# 这条清单存在的意义：PyInstaller 的 --exclude-module 误伤时只有 exe 会炸，
# 源码跑得好好的。清单里少了关键项，这道关卡就形同虚设。
need = ("vault_unpacker.core", "vault_unpacker.gui", "vault_unpacker.cli",
        "concurrent.futures", "tkinter", "urllib.request")
for m in need:
    check(m in entry.FROZEN_CHECK_MODULES, "自检清单包含 %s" % m)
check(callable(getattr(entry, "_run_frozen_check", None)),
      "冻结自检函数存在（打包脚本会调它）")

print("")
print("== main() 真的分流了 ==")

from vault_unpacker import cli  # noqa: E402

calls = []


def fake_cli_main(argv=None):
    calls.append(list(argv or []))
    return 0


real_cli_main = cli.main
cli.main = fake_cli_main
try:
    saved_argv = sys.argv
    sys.argv = ["VaultUnpacker.exe", "--inject-plugin", "--plugin-source", "bundled"]
    code = entry.main()
    sys.argv = saved_argv
finally:
    cli.main = real_cli_main

check(code == 0, "命令行模式返回 cli.main 的退出码")
check(calls == [["--inject-plugin", "--plugin-source", "bundled"]],
      "参数原样传给了 cli.main", "实际 " + repr(calls))

# 无参数时应走图形界面；这里塞一个假的 gui 模块，免得为了测分流去依赖 tkinter
fake_gui = types.ModuleType("vault_unpacker.gui")
fake_gui.main = lambda: 42
import vault_unpacker  # noqa: E402
saved_gui = getattr(vault_unpacker, "gui", None)
had_gui = "gui" in sys.modules
saved_mod = sys.modules.get("vault_unpacker.gui")
sys.modules["vault_unpacker.gui"] = fake_gui
vault_unpacker.gui = fake_gui
try:
    saved_argv = sys.argv
    sys.argv = ["VaultUnpacker.exe"]
    code = entry.main()
    sys.argv = saved_argv
finally:
    if had_gui and saved_mod is not None:
        sys.modules["vault_unpacker.gui"] = saved_mod
    else:
        sys.modules.pop("vault_unpacker.gui", None)
    if saved_gui is not None:
        vault_unpacker.gui = saved_gui
    else:
        try:
            del vault_unpacker.gui
        except AttributeError:
            pass

check(code == 42, "不带参数时走图形界面（拿到了 gui.main 的返回值）", "实际 " + repr(code))

print("")
print("== 命令行模式下导入失败：报错退出，不许弹阻塞弹窗 ==")
# 把 vault_unpacker.cli 在 sys.modules 里设成 None，import 就会抛 ImportError
fatal_calls = []
real_fatal = entry._fatal
entry._fatal = lambda msg: fatal_calls.append(msg)
saved_cli_mod = sys.modules.get("vault_unpacker.cli")
had_cli_mod = "vault_unpacker.cli" in sys.modules
saved_cli_attr = getattr(vault_unpacker, "cli", None)
sys.modules["vault_unpacker.cli"] = None
try:
    if hasattr(vault_unpacker, "cli"):
        del vault_unpacker.cli
    saved_argv = sys.argv
    sys.argv = ["VaultUnpacker.exe", "--inject-plugin"]
    code = entry.main()
    sys.argv = saved_argv
finally:
    entry._fatal = real_fatal
    if had_cli_mod and saved_cli_mod is not None:
        sys.modules["vault_unpacker.cli"] = saved_cli_mod
    else:
        sys.modules.pop("vault_unpacker.cli", None)
    if saved_cli_attr is not None:
        vault_unpacker.cli = saved_cli_attr

check(code == 1, "返回 1（非零退出码，脚本能感知失败）", "实际 " + repr(code))
check(fatal_calls == [],
      "没有调用 _fatal —— 它是阻塞式弹窗，命令行下会看起来像「卡死没输出」",
      "实际调用了 %d 次" % len(fatal_calls))

print("")
print("== --windowed 下 stdout 为空时的兜底 ==")
with tempfile.TemporaryDirectory() as tmp:
    fake_exe = os.path.join(tmp, "VaultUnpacker.exe")
    open(fake_exe, "w").close()
    saved_stdout, saved_stderr = sys.stdout, sys.stderr
    saved_exec = sys.executable
    saved_frozen = getattr(sys, "frozen", None)
    try:
        sys.frozen = True
        sys.executable = fake_exe
        sys.stdout = None
        sys.stderr = None
        log_path = entry._ensure_output()
        alive = sys.stdout is not None and sys.stderr is not None
        if alive:
            print("兜底日志这一行应该落进文件")
            sys.stdout.flush()
    finally:
        out_now = sys.stdout
        sys.stdout, sys.stderr = saved_stdout, saved_stderr
        sys.executable = saved_exec
        if saved_frozen is None:
            try:
                del sys.frozen
            except AttributeError:
                pass
        else:
            sys.frozen = saved_frozen
        try:
            if out_now is not None:
                out_now.close()
        except Exception:
            pass

    check(log_path is not None, "识别出「冻结 + stdout 为空」并给了兜底日志路径")
    check(alive, "兜底后 stdout / stderr 都能写（不再是 None）")
    check(log_path and os.path.isfile(log_path), "兜底日志文件确实建出来了")
    check(log_path and os.path.basename(log_path) == "vault-unpack-cli.log",
          "日志文件名固定，便于排查")
    check(log_path and os.path.dirname(log_path) == tmp,
          "日志写在 exe 同目录（不是临时目录，重启后还在）")
    if log_path and os.path.isfile(log_path):
        content = io.open(log_path, encoding="utf-8", errors="replace").read()
        check("兜底日志这一行应该落进文件" in content,
              "写进 stdout 的内容真的落进了日志文件")

print("")
print("通过 %d 项，失败 %d 项" % (ok, fail))
sys.exit(1 if fail else 0)
