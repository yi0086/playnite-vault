# 更新日志

按版本倒序。**新版本的功能说明看 [`README.md`](README.md)**，这里只记「每一版改了什么」。
工程上的取舍与踩坑记录在 [`docs/dev-notes.md`](docs/dev-notes.md)。

类型标记：`feature` 新功能 · `ui` 界面 · `fix` 修 bug · `perf` 性能 · `docs` 文档 · `breaking` 需要用户动手

---

## v1.7.0 —— 2026-09-21

`feature` `ui` `fix`

**云存档**：把游戏存档也存进 NAS（Playnite 自带的云存档只有界面与数据模型，没有云存储实现）。

- `feature` 内容寻址存储（`objects/xx/<sha1>`）+ 快照 + 分支；同一份内容跨快照、跨分支只占一份
- `feature` 路径两份合并：Playnite 的 `Game.SavePaths` 优先，再合插件自己的表，按游戏标题归并
- `feature` 三档嗅探：常见位置启发式 / 本次会话差分（mtime+size 指纹 + 向上爬父目录）/ PCGamingWiki；**只产候选，绝不自动写入**
- `feature` 触发时机三档：手动 / 停止游戏后自动上传 / 停止后上传＋启动时问
- `feature` 保留策略按分支独立：每个分支最多留几份（`0` = 无限）、固定过的永不删、最新一份必留
- `feature` 命令行入口 `VaultPack saves --action list|branches|upload|download|delete|prune|sniff`
- `ui` 侧边栏新增「存档管理」入口；插件设置里新增云存档一节
- `fix` 解包器：**托盘 / 隐藏状态下的 Playnite 关不掉** —— 原来只给「可见」窗口发 `WM_CLOSE`，
  而托盘时主窗口 `IsWindowVisible` 就是 `false`，于是「0 个窗口 → 等满 60 秒 → 请你手动退出」。
  现在可见优先、全不可见时挑隐藏主窗口，并在等待期间重试补发
- `fix` 解包器：**内置插件包按文件名排序取第一个** —— 字符串排序下 1.6.0 与 1.7.0 并存会挑中旧包，
  且将来 `1.10.0` 会被排在 `1.9.0` 前面。改成按版本号比大小

实测结论（免得下次再撞）：Playnite SDK 6.13 的 `AutoAdaptivePathHelper` 是**恒等函数** ——
`{WinLocalAppData}`、`%LOCALAPPDATA%` 等写法一律原样返回、`IsAdaptivePath` 恒 `false`，
所以没有官方 token 契约可对齐，插件自持一张表。

自检：C# 239 项、Python 109 项、主题抓取 25 项，全部退出码 0。

---

## v1.6.0 —— 2026-09-20

`feature` `fix` `ui` `breaking`

- `breaking` 插件改名 `Playnite-Vault`（去掉临时名里那串 GUID）。插件包文件名从
  `VaultDemo-*.zip` 变成 `PlayniteVault-*.zip` —— **≤1.5.0 的自动更新只会找老名字，搜不到这一版**，
  需要用 1.6.0 的解包器装一次；之后自动更新恢复正常
- `feature` 插件数据目录**自动迁移**（`settings.json` / 本地索引 / 缓存索引 / 运行状态 /
  `meta-cache/`）到新目录，WebDAV 配置与账号不用重配，旧目录原地保留不删
- `feature` 解包器新增「安装插件到 Playnite…」：自动定位 Playnite → 选插件包来源
  （内置离线包 / GitHub / Gitee）→ **优雅关闭** Playnite（等同点 ×，不强杀）→ 写入
  `Extensions\Playnite-Vault\` → 按原样重启
- `fix` **打包排除清单误伤 `concurrent`**，导致 exe 一导入核心模块就 `ModuleNotFoundError`
  —— 源码跑得通、冻结后必炸。已修，并加了「打完 exe 立刻跑一次冻结自检」的强制关卡
- `fix` exe 的命令行参数以前不生效（入口只认 `--selftest`，其它参数一律弹窗）
- `fix` 解包器只认 `Playnite.exe`，而实际进程是 `Playnite.DesktopApp.exe` /
  `Playnite.FullscreenApp.exe`（管理员模式还有 `.Admin`）—— 这个 bug 会让它在 Playnite 开着时
  写本地索引，写进去的内容随后被 Playnite 覆盖掉
- `ui` 旧版本备份挪到 `%APPDATA%\VaultUnpacker\plugin-backup\<时间戳>\`，不再堆在 `Extensions\`

---

## v1.5.0 —— 2026-09-20

`feature` `perf`

- `feature` **打开 Playnite 自动检查并安装插件更新**：启动 12 秒后并发探 GitHub 与 Gitee 的
  `releases/latest`，比版本号（`v` 前缀、预发布、`1.10 > 1.9` 都按段比）
- `feature` **国内 / 国外自动换源**：两个源并发探测、按实测延迟排序；下载途中速度掉到地板
  （默认 40 KB/s）以下就换另一个源
- `feature` 所有源都低于地板时，用主源再跑一次**不设地板**的兜底下载 —— 慢网络照样能装完
- `feature` 设置里可开自动刷新远端库，间隔 5 分钟 ~ 24 小时自选
- `perf` 运行游戏时自动暂停刷新，游戏结束后按已流逝时间补上
- `perf` 索引没变就不动本地库（指纹**刻意排除 `UpdatedAt`**，重新归档不会引发无意义的库重写）
- 新增 `tools/VaultSelfTest/`：本地假 GitHub / 假 Gitee，把上面每条都真跑一遍

---

## v1.4.0 —— 2026-09-19

`feature` `fix` `ui` `breaking`

- `breaking` 存储格式升级到 **v3 内容寻址区块**（旧归档读取端仍完全支持，重归档才需要）
- `fix` **大游戏传不上去**（速度从 20 MB/s 掉到 0 然后超时）：旧格式整文件独占一个分片，
  `res.pak` 1.93 GB 就变成一个 1.93 GB 的 PUT；且超时用的是建连超时，必定撞死。
  现在固定 32 MB 切块、一个文件可跨多块，超时拆成建连 / 停滞 / 应答三档，
  「停滞」才是「速度到 0」的正确判据，单块失败自动重试
- `fix` **装回去的目录名变成了 Playnite 里的中文显示名**（有些游戏在中文目录下起不来）：
  原因是目录名直接用了 `appId`，而 slug 生成用 `char.IsLetterOrDigit` —— 它对 CJK 返回 `true`。
  现在 `Id` 与文件夹名彻底分离，文件夹名取「上传前安装目录的最后一段」
- `feature` 游戏列表右键 → **管理仓库应用**（删除需管理口令，口令存在仓库根的 `vault-admin.json`）
- `perf` 下载区块并发 4 路；区块下完立刻解包并删临时文件（峰值临时占用只有「并发数 × 区块大小」）
- `ui` **401 精确诊断**：区分「密码填错」与「服务端认证后端坏了」—— 两者的 401 响应完全一样，
  光看状态码分不出来

---

## v1.3.0 —— 2026-09-18

`feature` `ui` `fix`

- `feature` 插件与解包器**统一版本号**
- `feature` 解包落盘的子目录名优先使用归档前的**原始安装目录名**，取不到才回退 `appId`
- `ui` 解包器配置简化为 **exe 同目录的 `config.json`**，首次启动自动生成
- `fix` 解包器列表里「输出父目录」标签与输入框重叠
- `fix` 加列后状态列写错位置，导致「已安装」显示到分片数那一列
