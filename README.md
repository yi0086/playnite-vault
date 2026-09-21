# Vault —— 把 NAS 变成 Playnite 的游戏库

> 一个 Playnite 插件 + 一整套配套工具：把本地游戏/绿色软件打包归档到 NAS（WebDAV），
> 之后在任何一台机器上都能从 NAS「安装」回来，元数据随包走、不用重新刮削。

**English TL;DR** — A Playnite library plugin plus companion tooling that turns a WebDAV/NAS
share into a game repository: pack a local install directory into a simple chunked format,
upload it, and install it back on any machine with metadata included.

---

## 功能

把 NAS 当成**自建的软件仓库**：归档、安装、存档、主题四件事都在 Playnite 里完成。

| 功能 | 说明 |
|---|---|
| **归档 / 安装** | 本地已装目录 → 分片归档进 NAS，Playnite 元数据随包走；在任何机器上给那条未安装的游戏点「安装」就解包回来，**不用重新刮削**。旧归档（Schema 1/2）读取端仍然完全支持 |
| **云存档**（v1.7.0） | 存档也进 NAS：内容寻址 + 快照 + 分支，路径自动嗅探，触发时机与保留份数都可配。**默认关闭** |
| **主题同步** | 本地 `Themes` 目录与 NAS 互传：上传 / 下载 / 双向，带预演与冲突留档 |
| **自更新** | 打开 Playnite 自动检查插件更新，国内 / 国外自动换源，慢网络有兜底 |
| **侧边栏控制台** | 左侧边栏一格管完：概览统计 / 主题同步 / 仓库与归档 / 设置。明暗三档（跟随 Playnite / 常亮 / 常暗），右上角一个小圆点报连通状态 |
| **仓库与归档**（v1.8.1） | 本机库里的游戏铺成卡片墙，每个游戏标出「传过没有」，未传的可直接归档；判定按 Playnite 游戏 ID → 库内 ID → 安装目录名三级 |
| **删归档** | 卡片上直接删（不用再单开窗口），删前要输管理口令；仓库里有、本地库里已经没有的条目也列在同一页 |

配套还有一个**免 Python 环境的单文件解包器**（图形界面 + 命令行，也能把插件装进 Playnite）。

- 每一版改了什么：[`CHANGELOG.md`](CHANGELOG.md)
- 工程上的取舍与踩坑：[`docs/dev-notes.md`](docs/dev-notes.md)

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

装好之后怎么用：[`docs/plugin-usage.md`](docs/plugin-usage.md)（含首次配置、归档/安装、
重新归档的完整顺序，以及 401 的排查方法）。
仓库格式规范：[`docs/repo-format.md`](docs/repo-format.md)。

---

## 组成

| 目录 | 是什么 | 技术栈 |
|---|---|---|
| `src/PlayniteVault/` | Playnite 插件本体：库来源、安装/卸载控制器、归档菜单、设置页、进度窗 | C# / .NET Framework 4.6.2 |
| `tools/VaultPack/` | 命令行工具：打包上传 / 下载安装 / 远端目录浏览 / 测速 / 备份 Playnite 库 / 导出库元数据 | C# / .NET Framework 4.6.2 |
| `tools/`（Python 部分） | 独立解包器：tkinter 图形界面 + 命令行，可打包成免 Python 环境的单文件 exe；解完可选登记回 Playnite；**还能把插件本身注入进 Playnite 并优雅重启它** | Python 3（标准库 + tkinter） |
| `tools/webdav_mock.py` | 极简本地 WebDAV 服务，用来离线跑端到端验证 | Python 3 |
| `tools/e2e-v3-test.py` | v3 端到端验证：造测试目录 → 打包上传 → 查仓库结构 → 解包还原 → 逐字节比对 → 中断续传 | Python 3 |
| `tools/VaultSelfTest/` | **插件侧自检**：本地假 GitHub / 假 Gitee，把自动更新与自动刷新整条链路真跑一遍，外加改名迁移、主题同步、云存档引擎与嗅探，以及 v1.8 的界面部件 / 整页构造 / 卡片对账 / 删除入口唯一（**303 项断言**） | C# / .NET Framework 4.6.2 |
| `tools/python-selftest/` | **Python 侧自检**：注入插件 / 真实 WM_CLOSE 优雅关闭 / GUI 真建窗口（**109 项断言**），`python tools/python-selftest/run_all.py` 一把跑完（设 `VAULT_TK_PYTHON` 指向带 tkinter 的解释器，GUI 与「真实关闭」两段才会跑） | Python 3 |
| `tools/speedtest.py` | WebDAV 吞吐排查：把「链路 / 服务端 / 客户端」三层分开量 | Python 3 |

---

## 工作原理

```
    本地已安装的目录                      NAS（WebDAV）                        另一台机器
┌──────────────────────┐          ┌────────────────────────┐          ┌──────────────────────┐
│ D:\Games\Brotato     │          │ index.json             │          │ Playnite 库          │
│   ├─ Brotato.exe     │  打包     │ apps/plants-vs-zombies │  解包     │   └─ 未安装条目       │
│   └─ …               │ ───────► │   ├─ manifest.json     │ ───────► │        ↓ 点「安装」   │
│                      │  上传     │   ├─ chunks/<sha1>.bin │  下载     │ D:\Games\Brotato     │
│ + Playnite 元数据     │          │   └─ meta/cover.png    │          │   （目录名保持一致）  │
└──────────────────────┘          └────────────────────────┘          └──────────────────────┘
                                    ▲
                                    └─ vault-admin.json（管理口令，保护删除操作）
```

四条设计上的取舍，决定了它为什么这么简单：

1. **默认不压缩**。现代游戏的体积大头是已压缩的 pak / 视频 / 音频，再压只能省个位数百分比，
   却要吃满 CPU 跑几个小时。所以默认 `store`（裸字节），只对个别条目按需 deflate。
2. **区块是「内容寻址」的**：`chunks/<sha1>.bin`，**文件名就是内容的哈希**。
   这带来三个白拿的好处 —— 「远端是不是已经有这一块」只需一个 HEAD 就能判断（天然可续传）、
   下载完能顺手校验哈希、以及同样内容的文件只存一份。
   代价是区块一旦写出就不可变，所以更新归档时留下的是新块而不是覆盖旧块
   （区块放在 `apps/{id}/` 里面，删应用时一起清掉，不会攒孤儿）。
3. **一个文件可以跨多个区块**（这是 v3 相对 v2 最关键的区别）。
   切块固定 32 MB，`res.pak` 会被切成几十块，于是单个 PUT 永远不会超过 32 MB
   —— 这正是「1.93 GB 的大文件必然超时」那个问题的根治法。
   代价是还原时要按 `PieceEntry.FileOffset` 随机写，不能顺序追加
   （并发下载时同一个文件的后半段完全可能先到）。
4. **元数据随包走**。归档时把 Playnite 库里的元数据写进 `manifest.json`，
   装机端直接读，省掉一次网络刮削。

详细的仓库格式规范见 [`docs/repo-format.md`](docs/repo-format.md)。

---

## 快速开始

### 0. 前置条件

- 一台支持 WebDAV 的 NAS（群晖 / 威联通 / TrueNAS / 飞牛 / 自建都行），或者任何能读写的 HTTP 目录
- Playnite（插件方式需要，用独立解包器则不需要）
- 编译需要 .NET SDK（`net462` 目标）；打解包器 exe 需要带 tkinter 的 Python 3 + PyInstaller

### 1. 装插件

**最省事的路子**：打开解包器 → 底栏 **安装插件到 Playnite…** → 点 **注入并重启**。
它会定位 Playnite、把插件写进 `Extensions\Playnite-Vault\`、优雅重启 Playnite，
旧版本挪去备份（详见 `docs/plugin-usage.md` 第 2 节）。下面这段是从源码手装的流程。

编译：

```bat
dotnet build src\PlayniteVault\PlayniteVault.csproj -c Release -p:PlayniteDir="C:\Playnite"
```

`-p:PlayniteDir` 指向你的 Playnite 安装目录（编译时要引用它自带的 `Playnite.SDK.dll` /
`Newtonsoft.Json.dll`）。不想每次敲也可以设个同名环境变量 `PlayniteDir`。
Playnite **便携版**就是把整个文件夹解压到任意位置，ExtensionsData 就躺在它旁边。

把产物复制到 Playnite 的扩展目录（便携版 `<Playnite>\Extensions\`，
安装版 `%AppData%\Playnite\Extensions\`），**目录名就用 `extension.yaml` 里的 Id**：

```
<Playnite>\Extensions\Playnite-Vault\
    PlayniteVault.dll
    extension.yaml
    icon.png
```

> v1.6.0 之前 Id 是 `VaultDemo_5e76bf50-…`，目录名也就跟着那一串。改名不影响已经配好的
> 设置：插件启动时会自己去旧的 `ExtensionsData\<guid>` 里把 `settings.json` /
> `local-index.json` / `state.json` 搬过来（老目录原地留着，不删）。

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

### 一条命令跑完整的 v3 端到端验证

`tools/e2e-v3-test.py` 自己起本地假 WebDAV，然后把整条链路跑一遍：
造测试目录 → 打包上传 → 查仓库落盘结构 → 解包还原 → **逐字节比对** → 中断后续传。
不需要 NAS，也不会碰真实仓库。

```bat
:: 先编译命令行工具（需要 Playnite 目录提供 SDK DLL）
dotnet build -c Release -p:PlayniteDir="C:\Playnite" tools\VaultPack\VaultPack.csproj

:: 常规用例：约 223 MB，含一个 200 MB 的跨块文件
python tools\e2e-v3-test.py

:: 加上一个 1.93 GB 的单文件 —— 复现「大游戏传不上去」当时的规模
python tools\e2e-v3-test.py --big

:: 顺带验证 deflate 分支
python tools\e2e-v3-test.py --compress
```

它会断言这几件容易写错的事：区块文件名确实是 40 位 sha1、仓库路径里没有非 ASCII 字符、
片段首尾相接且合计等于文件大小、**至少有一个文件真的跨了多个区块**、
空文件被建了出来、中文文件名能还原、临时区块文件下完即删、以及中断后靠日志跳过已完成区块。

### 自检：自动更新与自动刷新（v1.5.0）

```bat
dotnet build -c Release -p:PlayniteDir="C:\Playnite" tools\VaultSelfTest\VaultSelfTest.csproj
tools\VaultSelfTest\bin\Release\VaultSelfTest.exe
```

**不需要联网，也不会碰真实的 GitHub / Gitee** —— 它用 `TcpListener` 在 127.0.0.1 上
起一套可控的假服务，**节流、掉线、404 都是自己控制的**，所以「换了源」「兜底重试」
这类平时根本没法复现的分支可以稳定地跑出来。

**退出码 0 = 89 项断言全过**，1 = 有失败项。失败项会给出实际值（例如
「当前内容 = NEW」）而不是只说一句 false。跑完还会把完整报告写到临时目录。

它会真的执行生成的替换批处理：起一个占位进程假装 Playnite 在运行，
断言**进程还在时绝不动文件**、退出后才替换、然后重新拉起 —— 目标目录刻意用中文名，
顺带验批处理的 OEM 编码。所以跑它的时候会在任务管理器里一闪而过几个
`victim.exe` / `launcher.exe`，那是正常的。

---

## 目录结构

```
playnite-vault/
├── src/PlayniteVault/              Playnite 插件
│   ├── VaultPlugin.cs          插件主类（LibraryPlugin）：菜单、自动更新、自动刷新
│   ├── Controllers/            安装 / 卸载控制器
│   ├── Services/               仓库服务：索引、归档、安装、设置
│   │   ├── VaultUpdater.cs     ★ 自更新：探测两源 / 换源下载 / 校验暂存 / 替换脚本
│   │   ├── LibraryAutoRefresh.cs ★ 自动刷新：定时、暂停、索引指纹
│   │   ├── VaultRuntimeState.cs  运行时状态（独立 state.json，与设置分开）
│   │   ├── VaultDataMigration.cs 插件改名后的数据目录搬家（VaultDemo_<guid> → Playnite-Vault）
│   │   └── SemVersion.cs       版本比较（按段比，认 v 前缀与预发布）
│   ├── Net/                    WebDAV 客户端 + HttpFetch（更新用的极简 HTTP，可切代理）
│   ├── Sync/                   打包、增量同步、进度节流与心跳
│   ├── Models/                 数据模型（含仓库清单 / 随包元数据）
│   └── UI/                     设置页、全局进度窗适配
├── tools/
│   ├── VaultPack/              命令行工具（C#，直接编译插件源码，永远走最新生产代码）
│   ├── VaultSelfTest/          ★ 自更新 / 自动刷新的自检（本地假 GitHub / 假 Gitee）
│   ├── python-selftest/        ★ 注入链路与 GUI 的自检（真关进程、真建窗口）
│   ├── vault_unpacker/         独立解包器（Python 包）
│   ├── core.py             来源抽象 + 清单解析 + v1/v2/v3 三种布局解包 + 自检
│   │                       （v3 = 内容寻址区块：并发下载、按偏移随机写、下完即删、断点日志）
│   │   ├── playnite.py         定位 Playnite / 插件数据目录、读写 local-index.json
│   │   ├── inject.py           ★ 注入插件：定位 Playnite → 取包 → 优雅关 → 写入 → 重启
│   │   ├── config.py           外部配置（不进代码）
│   │   ├── cli.py / gui.py     两个入口
│   │   └── icon.py             内嵌图标（base64 PNG）
│   ├── VaultUnpacker.py        图形界面入口（也是打 exe 的入口）
│   ├── vault-unpack.py         命令行入口
│   ├── build-vault-unpacker.py 打包成单文件 exe
│   ├── build-release.py        ★ 打发布产物（插件 zip / 解包器 exe / 中文说明）
│   ├── make-app-icon.py        生成图标（ico + 内嵌 png）
│   ├── make-test-fixture.py    造离线测试归档
│   ├── webdav_mock.py          本地假 WebDAV（端到端验证用）
│   ├── e2e-v3-test.py          v3 端到端验证（含跨块与续传）
│   ├── speedtest.py            吞吐排查
│   └── config.example.json     配置模板（复制成 config.json 用）
├── CHANGELOG.md                每一版改了什么（README 只写当前版本的功能）
└── docs/                       仓库格式 / 使用说明 / 云存档设计 / 开发约定与踩坑
```

> 带 ★ 的是 v1.5.0 新增的。

**发布产物不进 git**：`tools/build-release.py` 会把 zip / exe / 说明文件写到仓库**外面**的
`../release/v<版本>/`，再用 REST API 挂到两个平台的 Release 上。

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
