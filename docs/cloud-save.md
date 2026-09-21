# 云存档：设计（v1.7.0 起）

> 这一篇是**设计契约**：改存档相关代码时同步改它。行为上的坑记到 `dev-notes.md`。

## 1. 复刻对象：Playnite 自己那套云存档

Playnite 的云存档**骨架是现成的，缺的是后端**。以下都是从本机 Playnite 10.41 里查出来的，
不是猜的：

| 部件 | 位置（本机证据） |
|---|---|
| 存档路径模型 | `Playnite.SDK.Models.Game.SavePaths` → `SavePath` |
| 路径条目 | `SavePath { GameSaveType GameSaveType, string Path, string Title, bool AutoAdaptive }` |
| 唯一标识 | `SavePath._title` 的注释：「不同的电脑就靠这个来进行对齐，相当于 savepath 的唯一标识」 |
| 元素类型 | `GameSaveType { File, Directory }`；注释：「初步考虑有文件夹，文件，以及注册表」→ **注册表那类没做** |
| 自适应路径 | `Playnite.SDK.AutoAdaptivePathHelper`（**公开类**）：`TransformPath1(path, autoAdaptive, installDirectory)`、`ReplaceStart(path, from, to)` |
| 云端快照管理器 | `Playnite.DesktopApp.Windows.GameSaveCloudManagerWindow` + `GameSaveCloudManagerViewModel`（`LOCGameSaveCloudManagerWindowTitle`） |
| 快照元数据字段 | 管理窗口绑的是 `ShowName / Machine / CreateTime / FileSize / Comment` |
| 分组标识 | `SaveGameGroupId`（同一个游戏跨机器的快照归一组） |
| 路径嗅探器 | `SavePathSniffer` + `SnifferMode{Strict,Loose}` + `ParseGameSaveSnifferLogWindow` + `GetSnifferLogPath` |
| 传输层 | `Playnite.CloudStorage.GameSave` —— 走 Playnite 自己的云。**装目录里既没有 cloud DLL 也没有 `CloudStorage` 目录** |

**结论：数据结构在、UI 窗口在、嗅探器在，但没有可用的后端。**
本插件要做的就是补上传输层（接到用户自己的 NAS/WebDAV），并把其余部件复刻齐全。

## 2. 仓库布局

```
saves/
├── index.json                        # 全局索引：每个游戏几份快照 / 几条分支 / 体积 / 最后更新
└── {gameKey}/                        # gameKey = 游戏 Id 的安全化形式（GUID 原样）
    ├── manifest.json                 # 存档定义 + 分支表 + 快照表（元数据都在这里）
    ├── objects/{sha1[0:2]}/{sha1}    # 文件内容，按内容寻址（自动去重）
    └── snapshots/{branch}/{id}.json  # 每个快照一个清单：files[{p,s,h,m}]
```

- **内容寻址**是这里最关键的一条：存档每次只改几个文件，如果每个快照都整树存一份，
  10 份快照就是 10 倍体积。按内容寻址之后，只有**新内容**才占空间，分支/多周目复用同一批对象。
  这和仓库里游戏用的 v3 区块是同一个思路（见 `repo-format.md`）。
- 快照清单独立成文件（不塞进 `manifest.json`）：有些游戏一个存档目录几千个文件，
  全塞进一个 JSON 会把它撑到几十 MB，而列表页只需要元数据。
- `manifest.json` 只放元数据：路径定义、分支、快照摘要（id/branch/时间/机器/体积/注释/父快照/标星）。

## 3. 快照与分支

**快照**（snapshot）＝ 某一时刻的存档集合。

- `SnapshotId` = `yyyyMMdd-HHmmss-<机器名>`（UTC），机器名进 id 是为了「同一秒两台机器」也能区分。
- 元数据字段**照抄 Playnite 管理窗口那五个**，另加我们需要的：
  `ShowName`、`Machine`、`CreateTime`（UTC）、`FileSize`、`Comment`、`Branch`、`Parent`、`Pinned`。
- `ShowName` 是给人看的名字（默认取时间，可改成「第三章 Boss 前」这种）。
- `Pinned`（标星）**永不被保留策略删掉** —— 手工标记的重要存档不能被自动清理吃掉。

**分支**（branch）＝ 一条独立的存档历史线，用来装「主线 / 二周目 / 打 mod」这类互不相干的进度。

- 每个游戏至少有一个分支，默认名 `main`，可在设置里改默认分支名。
- 新建分支可以**从任意快照分叉**（`Parent` 记下来，方便回溯"从哪来的"）。
- 分支之间不互相影响；保留策略**按分支分别计算**，否则多分支游戏会被别的分支挤掉历史。
- 删除分支：非空分支需要显式确认（把快照一起删）；`main` 和当前默认分支不许删。

## 4. 保留策略

- 设置项 **「每个分支最多保留几份」**：默认 10，**填 0 = 无限**。
- 删除顺序：最旧的先删；`Pinned` 的跳过；**永远至少留最新一份**（除非用户手动删整个分支）。
- 删完快照后做一次**对象回收**：扫该游戏剩下所有快照清单，删掉没人引用的 `objects/`。
  这一步只在该游戏内进行，不会跨游戏误删。

## 5. 触发策略（设置里选，都能关）

| 档位 | 行为 |
|---|---|
| `Manual`（默认） | 只在你点「上传/恢复」时动 |
| `UploadOnStop` | 游戏退出后自动上传一份快照 |
| `UploadOnStopDownloadOnStart` | 再加：游戏启动前，**远端有过比本地新的快照就提示**，默认不覆盖本地 |

- 「启动前下载」**不做静默覆盖**：先弹一个「远端有更新（时间/机器/体积）要不要拉下来」的问询，
  并且拉之前**先把当前本地存档存成一份快照**。存档是玩家最不能丢的东西，宁可多问一句。
- 自动上传只在存档内容**与最新快照不同**时发生（比指纹），内容没变就不产生新快照，免得刷出一堆空历史。
- 游戏正在运行时不碰它的存档目录（`OnGameStarted` 记状态，`OnGameStopped` 才动手）。

## 6. 路径来源与自适应

**来源（Playnite 优先，插件补充）**：

1. 先读 Playnite 的 `Game.SavePaths`（你在 Playnite 游戏编辑里填的那份）；
2. 插件自己也能存一份（存在插件数据目录），用于 Playnite 那份为空、或者你想加插件专属字段
   （忽略规则、压缩级别）的场合；
3. 两边**按 `Title` 对齐**（这就是 Playnite 说的「跨机器对齐用的唯一标识」），同 Title 以 Playnite 为准，
   只有插件有的条目也算数。界面上会标出每条来自哪边。

**自适应路径的 token 表**（`AutoAdaptive = true` 时展开）：

| token | 展开为 |
|---|---|
| `{UserProfile}` | `%USERPROFILE%` |
| `{WinAppData}` | Roaming `AppData`（`%APPDATA%`） |
| `{LocalAppData}` | Local `AppData` |
| `{LocalAppDataLow}` | `%USERPROFILE%\AppData\LocalLow` |
| `{Documents}` | 「文档」 |
| `{SavedGames}` | `%USERPROFILE%\Saved Games` |
| `{ProgramData}` | `%ProgramData%` |
| `{GameDir}` | 该游戏的安装目录（`Game.InstallDirectory`） |

- 写路径时做**反向折叠**：如果实际路径的开头命中某个已知根，就折叠成 token，
  这样一份定义换台机器还能用 —— 这就是「真正可用」的关键。
- **PCGamingWiki 的 `{{p|userprofile}}\AppData\...` 正好能对上这张表**，
  所以从它那儿抓来的路径可以直接落成 token 形式。
- Playnite 的 `AutoAdaptivePathHelper` 我们**只读不写**：把它的结果当参考显示；
  写入的自适应路径一律用我们自己的 token（因为它的 token 集合没有公开契约，
  拿它当风格来源会写出我们展开不了的东西）。遇到不认识的 token → **不猜，标成未解析**，
  在界面上提示，等用户确认。

## 7. 嗅探（三档，全部产出候选，绝不自动写入）

1. **常见位置启发式**（成本最低、命中率不错）
   拿游戏名 + 安装目录名去猜：`<游戏目录>/save`、`Saved Games/<名>`、
   `Documents/<名>`、`AppData/{Local,Roaming,LocalLow}/<名>` 等，
   按「名字相似度 + 是否含 save/存档 关键字 + 最近修改时间 + 内容像不像存档」打分排序。
2. **会话差分**（复刻 Playnite 嗅探器的工程化版本）
   游戏启动前对一组候选根做**轻量指纹**（只记 mtime + size，限制深度、文件数与超时），
   退出后比对出变更集合，取变更文件的**公共父目录**并裁剪到合理层级，作为候选。
   这样不需要驱动/句柄级监听，代价可控，且正好覆盖「存档写在哪儿完全不知道」的情况。
3. **PCGamingWiki 抓取**
   抓 `Save game data location` / `Configuration file(s) location` 两节，
   把 `{{p|...}}` 变量翻成我们的 token。带超时与失败降级（抓不到就只用前两档）。

候选列表交给用户勾选确认后才写成路径条目；勾选时可以顺手标成自适应。

## 8. 接口与入口

- 引擎 `Services/SaveSync.cs`：**不依赖 Playnite UI**，只吃一个 `WebDavClient` +
  一份「存档定义 + 本地根目录」，所以命令行、界面、自检共用同一条生产代码路径。
- 命令行：`VaultPack saves <list|upload|download|delete|branches|prune|extract>`。
- 插件：游戏右键（上传 / 恢复 / 存档管理）、主菜单（存档管理）、设置页的云存档分区、
  侧边栏页的入口。
- 管理窗口 `UI/VaultSaveWindow.cs`：左游戏 → 右「路径编辑 + 快照/分支列表」。

## 9. 绝不静默丢东西（红线）

1. 恢复快照前，**先把当前本地存档存成一份快照**（自动，`Comment` 写「恢复前自动备份」）。
2. 冲突/异常一律**留档**，不删原件：沿用主题同步的 `.conflict-*` 约定。
3. 保留策略不删 `Pinned`；分支至少留最新一份。
4. 远端删除是**显式动作**：只有你点删除、或保留策略裁剪才发 DELETE，别的时候一次都不发。
   （自检里有一条断言盯着「普通同步流程一次 DELETE 都没发过」这个性质。）
