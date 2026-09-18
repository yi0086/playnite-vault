# Vault —— 把 NAS 变成 Playnite 的游戏库

> 一个 Playnite 插件 + 一整套配套工具：把本地游戏/绿色软件打包归档到 NAS（WebDAV），
> 之后在任何一台机器上都能从 NAS「安装」回来，元数据随包走、不用重新刮削。

**English TL;DR** — A Playnite library plugin plus companion tooling that turns a WebDAV/NAS
share into a game repository: pack a local install directory into a simple chunked format,
upload it, and install it back on any machine with metadata included.

---

## 它解决什么问题

Playnite 本身没有「从自己的存储装游戏」的路径：库里要么是各平台客户端（Steam/Epic…），
要么是手动添加的本地条目。而绿色软件、免安装游戏、模拟器 ROM、自制工具这些「拷走就能跑」的东西，
既没有平台客户端，也不值得为每台机器重新下载一遍。

Vault 的做法是把 NAS 当成一个**自建的软件仓库**：

- **归档**：本地已装好的目录 → 打包成分片归档 → 上传到 NAS，顺手把 Playnite 库里的元数据
  （封面、开发商、类型、标签、评分…）一起塞进包里
- **安装**：在任何机器上，Playnite 里那条未安装的游戏点「安装」，就从 NAS 拉下来解包，
  元数据直接从包里读，不需要再刮削一次
- **便携解包**：不想开 Playnite 也行 —— 配套一个单文件 exe / 命令行，指向 NAS 就能解包

---

## 组成

| 目录 | 是什么 | 技术栈 |
|---|---|---|
| `src/VaultDemo/` | Playnite 插件本体：库来源、安装/卸载控制器、归档菜单、设置页、进度窗 | C# / .NET Framework 4.6.2 |
| `tools/VaultPack/` | 命令行工具：打包上传 / 下载安装 / 远端目录浏览 / 测速 / 备份 Playnite 库 / 导出库元数据 | C# / .NET Framework 4.6.2 |
| `tools/`（Python 部分） | 独立解包器：tkinter 图形界面 + 命令行，可打包成免 Python 环境的单文件 exe；解完可选登记回 Playnite | Python 3（标准库 + tkinter） |
| `tools/webdav_mock.py` | 极简本地 WebDAV 服务，用来离线跑端到端验证 | Python 3 |
| `tools/speedtest.py` | WebDAV 吞吐排查：把「链路 / 服务端 / 客户端」三层分开量 | Python 3 |

---

## 工作原理

```
    本地已安装的目录                      NAS（WebDAV）                        另一台机器
┌──────────────────────┐          ┌────────────────────────┐          ┌──────────────────────┐
│ D:\Games\Brotato     │          │ index.json             │          │ Playnite 库          │
│   ├─ Brotato.exe     │  打包     │ apps/Brotato/          │  解包     │   └─ 未安装条目       │
│   └─ …               │ ───────► │   ├─ manifest.json     │ ───────► │        ↓ 点「安装」   │
│                      │  上传     │   ├─ parts/part-0000…  │  下载     │ D:\Games\Brotato     │
│ + Playnite 元数据     │          │   └─ meta/cover.png    │          │   （目录名保持一致）  │
└──────────────────────┘          └────────────────────────┘          └──────────────────────┘
```

三条设计上的取舍，决定了它为什么这么简单：

1. **默认不压缩**。现代游戏的体积大头是已压缩的 pak / 视频 / 音频，再压只能省个位数百分比，
   却要吃满 CPU 跑几个小时。所以默认 `store`（裸字节），只对个别条目按需 deflate。
2. **分片是「裸字节流」**。`part-0000.bin` 就是若干文件字节的顺次拼接，文件头里没有任何索引；
   每个文件的位置由 `manifest.json` 的 `Part / Offset / StoredSize` 描述。
   于是任何能「从偏移量读 N 字节」的程序都能解包 —— 这是独立解包器能只有几百行的原因。
3. **元数据随包走**。归档时把 Playnite 库里的元数据写进 `manifest.json`，
   装机端直接读，省掉一次网络刮削。

详细的仓库格式规范见 [`docs/repo-format.md`](docs/repo-format.md)。

---

## 快速开始

### 0. 前置条件

- 一台支持 WebDAV 的 NAS（群晖 / 威联通 / TrueNAS / 飞牛 / 自建都行），或者任何能读写的 HTTP 目录
- Playnite（插件方式需要，用独立解包器则不需要）
- 编译需要 .NET SDK（`net462` 目标）；打解包器 exe 需要带 tkinter 的 Python 3 + PyInstaller

### 1. 装插件

编译：

```bat
dotnet build src\VaultDemo\VaultDemo.csproj -c Release -p:PlayniteDir="C:\Playnite"
```

`-p:PlayniteDir` 指向你的 Playnite 安装目录（编译时要引用它自带的 `Playnite.SDK.dll` /
`Newtonsoft.Json.dll`）。不想每次敲也可以设个同名环境变量 `PlayniteDir`。
Playnite **便携版**就是把整个文件夹解压到任意位置，ExtensionsData 就躺在它旁边。

把产物复制到 Playnite 的扩展目录（便携版 `<Playnite>\Extensions\`，
安装版 `%AppData%\Playnite\Extensions\`），目录名用 `extension.yaml` 里的 Id：

```
<Playnite>\Extensions\VaultDemo_5e76bf50-cb8a-4a87-ad24-1912c746c6f0\
    VaultDemo.dll
    extension.yaml
    icon.png
```

重启 Playnite，在「设置 → 扩展」里配置 WebDAV 地址、账号、密码。

### 2. 打包并上传（可选，也可以用插件右键菜单做）

```bat
dotnet build tools\VaultPack\VaultPack.csproj -c Release -p:PlayniteDir="C:\Playnite"

:: 看远端有什么
tools\VaultPack\bin\Release\VaultPack.exe ls

:: 从 Playnite 库里导出某个游戏的元数据（封面/开发商/类型/标签…）
tools\VaultPack\bin\Release\VaultPack.exe meta --game "Brotato" --out brotato.json

:: 打包上传（带上元数据；工具会走到插件同一条生产代码路径）
tools\VaultPack\bin\Release\VaultPack.exe pack --src D:\Games\Brotato --id Brotato --meta brotato.json

:: 反过来：从仓库装到本地并逐文件校验
tools\VaultPack\bin\Release\VaultPack.exe install --id Brotato --dir D:\Games\Brotato
```

### 3. 独立解包器（不需要 Playnite，也不需要 Python）

直接跑源码：

```bat
python tools\VaultUnpacker.py            :: 图形界面
python tools\vault-unpack.py --help      :: 命令行
```

打成单文件 exe（首次启动会自解压到临时目录，稍慢一两秒）：

```bat
python tools\build-vault-unpacker.py     :: 产物 tools\dist\VaultUnpacker.exe
```

配置放在 **exe 同目录的 `config.json`**，没有就按默认值自动生成一份：

```bat
copy tools\config.example.json tools\config.json
```

> `config.json` 里的密码是**明文**（按便携使用场景刻意如此）。它已写进 `.gitignore`，
> 别提交到仓库；不想留明文就把 `webdav_password` 留空，每次启动手输。

命令行示例：

```bat
:: 列仓库里有哪些应用
tools\vault-unpack.py --dir Z:\Vault --list

:: 解包（内网会自动绕过系统代理）
tools\vault-unpack.py --id Brotato --dir Z:\Vault --out D:\Games

:: 解完顺手登记回 Playnite，让库里的条目变成「已安装」
tools\vault-unpack.py --id Brotato --dir Z:\Vault --out D:\Games --register
```

---

## 离线开发与测试

不需要 NAS 也能把整条链路跑通：

```bat
:: 1. 造一个极小的 Schema-2 归档（store + deflate、子目录、中文/空格路径、多分片都覆盖）
python tools\make-test-fixture.py %TEMP%\vault-fixture Brotato

:: 2. 用本地来源模式解它（图形界面里选「本地目录」，或走命令行）
python tools\vault-unpack.py --dir %TEMP%\vault-fixture --id fixture-app --out %TEMP%\vault-out

:: 或者起一个带 Basic 认证的假 WebDAV，验证网络那条路
python tools\webdav_mock.py --root %TEMP%\vaultmock --port 8099 --user demo --pass demo123
```

`build-vault-unpacker.py` 打出来的 exe 自带自检（不打开主界面，逐项验证运行环境）：

```bat
tools\dist\VaultUnpacker.exe --selftest
```

---

## 目录结构

```
playnite-vault/
├── src/VaultDemo/              Playnite 插件
│   ├── VaultPlugin.cs          插件主类（LibraryPlugin）
│   ├── Controllers/            安装 / 卸载控制器
│   ├── Services/               仓库服务：索引、归档、安装、设置
│   ├── Net/                    WebDAV 客户端（PROPFIND/GET/PUT/MKCOL/DELETE + 断点续传）
│   ├── Sync/                   打包、增量同步、进度节流与心跳
│   ├── Models/                 数据模型（含仓库清单 / 随包元数据）
│   └── UI/                     设置页、全局进度窗适配
├── tools/
│   ├── VaultPack/              命令行工具（C#，直接编译插件源码，永远走最新生产代码）
│   ├── vault_unpacker/         独立解包器（Python 包）
│   │   ├── core.py             来源抽象 + 清单解析 + 分片解包 + 自检
│   │   ├── playnite.py         定位插件数据目录、读写 local-index.json
│   │   ├── config.py           外部配置（不进代码）
│   │   ├── cli.py / gui.py     两个入口
│   │   └── icon.py             内嵌图标（base64 PNG）
│   ├── VaultUnpacker.py        图形界面入口（也是打 exe 的入口）
│   ├── vault-unpack.py         命令行入口
│   ├── build-vault-unpacker.py 打包成单文件 exe
│   ├── make-app-icon.py        生成图标（ico + 内嵌 png）
│   ├── make-test-fixture.py    造离线测试归档
│   ├── webdav_mock.py          本地假 WebDAV（端到端验证用）
│   ├── speedtest.py            吞吐排查
│   └── config.example.json     配置模板（复制成 config.json 用）
└── docs/repo-format.md         仓库格式规范
```

---

## 适用边界（重要）

这套东西**只适合「拷贝就能跑」的东西**：

- ✅ 绿色软件、免安装游戏、模拟器 ROM、自制工具、单机老游戏
- ❌ 写注册表 / 装服务或驱动 / 依赖系统组件的软件
- ❌ 带 DRM 的平台游戏（Steam / Uplay / EA…）—— 它们必须走平台自己的备份与校验

原因很直接：拷贝文件无法重建注册表项、服务注册和驱动安装。这类需求请用平台自带的备份功能。

---

## 许可

[GPL-3.0](LICENSE)
