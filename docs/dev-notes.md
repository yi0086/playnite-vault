# 开发约定与踩坑记录

给「下一个接手的人」（包括未来的自己）看的工程约定。改代码时**顺手改这里** ——
它随代码版本化，比放在别处的笔记可靠。用户视角的用法在 [plugin-usage.md](plugin-usage.md)，
仓库格式规范在 [repo-format.md](repo-format.md)。

---

## 1. 仓库布局：必须与开发树同构

- 开发树就是正式仓库：`playnite-vault/`（`src/PlayniteVault/` + `tools/`）。
- 同目录下的 `playnite-vault-demo/` 是**旧开发树残留**，不要动，也不要当参考。
- `tools/VaultPack/VaultPack.csproj` 用相对路径 `..\..\src\PlayniteVault\**\*.cs`
  **直接编译插件源码**，保证打包工具永远走最新生产代码。所以仓库布局不能改成
  `plugin/ + packer/` 之类的花样 —— 一改相对路径就断，报 CS0246。

## 2. 远端与提交

| 项 | 值 |
|---|---|
| 远端 | `git@github.com:yi0086/playnite-vault.git`、`git@gitee.com:yi0086/playnite-vault.git` |
| 推送顺序 | **先 GitHub，后 Gitee** |
| 协议 | GPL-3.0 |
| 提交身份 | `Yi0086 <me@yi0086.cn>` |
| SSH 密钥 | `~/.ssh/id_ed25519_playnite_vault`（无口令），经**仓库本地** `core.sshCommand` 绑定，不动全局 |
| 插件 Id | `Playnite-Vault`（= `ExtensionsData\` 目录名 = 发布包文件夹名） |
| Module | `PlayniteVault.dll`；资产名 `PlayniteVault-<版本>.zip` |
| 老 GUID | `5e76bf50-cb8a-4a87-ad24-1912c746c6f0` 保留（历史数据目录名） |

`extension.yaml` 的 `Author: Yi0086` 是**有意保留的公开署名**，不是泄露。

发布凭据在仓库外：`%USERPROFILE%\.playnite-vault\{github,gitee}-token.txt`。
Release 只认 token（SSH 推不了 release）。

## 3. 配置与数据模型

- **解包器配置**一律放 exe 同目录的 `config.json`，首次启动自动生成（`%APPDATA%` 已废弃）。
  `config.json` 在 `.gitignore` 里（含明文密码），仓库只提供 `tools/config.example.json`。
  测试隔离用环境变量 `VAULT_UNPACKER_CONFIG` 指向临时路径，**绝不覆盖用户真实配置**。
- **解包子目录名**优先用元数据 `InstallDirName`（上传前的原始安装目录名），取不到才回退 appId；
  外部元数据一律经 `core.sanitize_folder_name()` 做路径遍历清洗。
- **`Id` 与文件夹名彻底分离**：
  - `Id`（仓库路径）= 安装目录最后一段压出的 ASCII slug，**只认 a-z0-9**
    （旧实现用 `char.IsLetterOrDigit`，对 CJK 返回 true → 生成出中文 id）。
  - 文件夹名 = 元数据 `InstallDirName`（可能含空格/大小写）。
  - 非 ASCII 名称的兜底 id 必须**确定性**（名字的 SHA-1 前 8 位 `app-xxxxxxxx`）。
    早先退回随机串，每次重新归档都生成新 Id，仓库里攒下一串重复条目。
  - 已是本插件条目的游戏**一律沿用原 Id**（改 Id 会让库条目与已装目录失联）。

## 4. 仓库格式（v3 内容寻址区块）

- 区块 = `apps/{Id}/chunks/<sha1>.bin`，**文件名就是内容哈希**，不可变。
- 默认 32 MB，**一个文件可跨多个区块**（v3 相对 v2 的关键，也是「大文件传不上去」的根治法）。
- 区块放在应用自己的目录里 → 删应用即删区块，**没有孤儿**；但重新归档会留过期区块
  （换块大小、源目录改动都会产生），归档收尾要 `PurgeStaleChunks`。
- 读取端**按字段推断布局，不看 Schema 数值**：`Chunks` 非空→v3、`Packed`+`Parts`→v2、其余 v1。
  `AppManifest.Schema` 默认值特意是 **0**（不是 `Current`），否则旧清单会被误判成 v3。
- v3 落盘必须**随机写**（`r+b` + `seek(FileOffset)`），**不能 append**：并发下载会让区块乱序完成。
- 索引指纹刻意**排除 `UpdatedAt`**：重新归档但内容没变时不该白刷一次本地库。
- 运行时状态放独立 `state.json`，不混进 `settings.json` —— 设置页是「快照 → 改副本 → 保存」，
  共用一个文件会让保存把后台刚写的状态覆盖回去。

## 5. 自更新（v1.5.0 起）

**更新落地必须靠外部脚本**：`PlayniteVault.dll` 被 Playnite 进程锁着，运行期不可能覆盖自己。
顺序固定：下载 → 校验 → 暂存 → 写 `.cmd` → **请 Playnite 退出** → 覆盖 → 脚本重新拉起。

- 退出走反射调 `PlayniteApplication.Quit(bool)`（与托盘「退出」同路，不会进安全模式）。
- **退出与拉起的控制权都不交给 Playnite**：让它自己 Restart 会抢在文件替换前起来，加载到旧 DLL。
- `.bat` 必须按 **OEM 代码页**（中文系统 936）写，并在脚本里 `chcp`，否则中文路径乱码。
- `.bat` 里的外部命令一律写 `%SystemRoot%\System32\xxx.exe` 绝对路径。裸 `find` 会被
  Git for Windows / MSYS2 的 **Unix 版 `find.exe`** 抢走（它不认识 `/I`，直接报错退出），
  而脚本正是用 find 的退出码判断「Playnite 是否已退出」→ 会在 Playnite 还开着时覆盖文件。
- 「太慢/失败」的分类必须用显式 `out bool`，**绝不能用 `note.StartsWith("太慢")`**：
  `note` 前面带着候选描述（`github/直连 (0 ms) → 太慢…`），字符串嗅探永远不成立，
  结果「慢」被当成硬失败、兜底重试从不触发。

## 6. 发布产物与发布流程

`python tools/build-release.py <版本>` → 产物到**仓库外面**的 `../release/v<版本>/`（二进制不进 git）。

- zip 里的文件夹名 = `extension.yaml` 的 `Id`（Playnite 要求 `<Name>_<GUID>` 格式）。
- `使用说明.txt` 由 `docs/plugin-usage.md` **自动生成**，别手工再写一份
  （曾有一份手写的把标题重复了 36 次，而 md 是好的 —— 两份内容各写各的必然出事）。
- `.txt` 必须 **UTF-8 带 BOM + CRLF**（否则记事本按 GBK 解码成乱码）；
  含中文名的 zip 必须用 Python `zipfile`（PowerShell `Compress-Archive` 不设 UTF-8 flag 0x800）。
- **只复制明确列出的文件，绝不整目录拷贝** —— `tools/dist/` 里躺着带真实 NAS 地址与账号的 `config.json`。

`python tools/publish-release.py <版本> --notes-file ../release/release-notes-v<版本>.md --verify-download`
—— 已固化，**别再写临时脚本发 release**。另有两条小命令：`--retitle "Vault X.Y.Z"` 只改标题、
不动正文资产（用来统一历史 release 的命名）；`--delete` 撤掉整条 release 与 tag（两端 + 本地）。

- GitHub 建/更新 release + 传资产（可 `PATCH` 正文）；Gitee 建 release 后**逐个** `attach_files`。
- 回验四层：资产在不在 → 同名项出现几次 → API 记的大小 → **真下载比 sha256**。
- 重跑幂等：同名已存在就跳过；`--force` 才覆盖。
- **Gitee 的 `--force` = 整条 release 删掉重建**：`/releases/tags/{tag}` 的 `assets[]`
  **既没有 `id` 也没有 `size`**，而重复 attach 同名文件**不会覆盖、会挂成两份**，
  下载解析到哪一份不可控。
- **GitHub 换资产后下载地址会短暂喂旧缓存**：先看 API 的 `size`/`created_at` 是否已更新，
  是就隔一会儿重跑回验，别反复重传。
- 版本已发布后**不要为了小修原地换字节**；若确实刚发几分钟、`download_count` 还是 0，
  可以 `--force` 重发并把 tag 挪到新提交，保证 tag 源码与二进制一致。

### 6.1 文档分工（v1.8 起改的，别再写回去）

```
README.md      只写**当前版本**的功能与用法。历史一律不放这儿。
CHANGELOG.md   每一版改了什么（类型标记 + 要点），最新在前。
release/release-notes-v<x>.md   只写「逐条要点 + 引导」（**首行是标题**），别赘述 README 已有的东西。
docs/dev-notes.md   工程取舍、踩坑（给改代码的人看）。
```

`release-notes-v<x>.md` 的骨架（`--notes-file` 指向它）。**首行是标题**，必须是
`Vault X.Y.Z`（写成 `# Vault X.Y.Z` 也行，脚本会把 `#` 去掉）：

```markdown
Vault 1.8.1

- [bugfix] ...
- [feature] ...
- [ui] ...

细节见 [CHANGELOG](https://github.com/yi0086/playnite-vault/blob/main/CHANGELOG.md)。
```

- **标题 = 首行，`Vault X.Y.Z`，后面不接任何说明**。历史上 v1.6.0 写成了
  「Playnite Vault 1.6.0」、v1.7.0/v1.8.0 把 markdown 的 `#` 连整句说明一起带进了标题字段 ——
  1.8.1 期间已用 `--retitle` 全部改回，以后就按这个写。
- **不要**在发布正文里重复项目介绍 / 适用边界 / 许可 / 目录结构 ——
  那些 README 里有，重复一遍就是两处要同步。
- **不要「要点」这种小标题**，直接列点；**每条以 `[类型]` 开头**（`[bugfix]` / `[feature]` /
  `[ui]` / `[perf]` / `[docs]` / `[breaking]` / `[note]`）。**不要**再另起一行写「更新类型」
  （`--kind` 已经删掉了）。
- **「注意事项」只写「升级时必须用户自己动手」的事**（破坏性变更之类）；达不到就整段不写。
- **不写下载区、也不要「下载」这个小标题**。资产名与直链由 `publish-release.py` 拼好，
  以 `<details>` 折叠**放在正文最上面**（`release_body_for`），手写必写成另一个平台的链接。
- 正文里指向 `CHANGELOG.md` 的链接**只写 GitHub 那份**，脚本会把域名改写成目标平台。

### 6.2 tag：一律用附注 tag，且由脚本建

- **轻量 tag** 就是一个指向 commit 的 ref（`ls-remote` 里只有一行）；
  **附注 tag** 是个真实对象，带 tagger、日期、说明（`ls-remote` 里多一行 `^{}`）。
  `git describe` / 带说明的 Release 都靠附注 tag；本项目从 v1.3.0 起一直是附注的。
- **v1.7.0 的 GitHub 侧曾经是轻量的** —— 因为 `POST /releases` 只给 `tag_name` 时，
  GitHub 替你建轻量 tag，**而 Gitee 建的是附注 tag**：同一个脚本，两边类型不一样。
  已由 `ensure_annotated_tag()` 修正（发 release 前先本地建好附注 tag 并推上去，
  API 看到同名 tag 就直接复用）。
- `--tag-message` 给一句摘要。**别把整篇发布说明当 tag 说明** —— Gitee 自动建的 tag
  就是那样，结果 tag 对象里塞了几千字。
- 发布完回验三件事：两边 tag 对象 sha **一致**、`^{}` 解引用到同一个 commit、
  tagger 是本仓库的提交身份（本地 `user.name`/`user.email` 决定 tagger）。

## 7. 解包器注入插件（v1.6.0 起）

`tools/vault_unpacker/inject.py` + GUI 独立窗口「安装插件到 Playnite…」+ CLI `--inject-plugin`。

- **顺序是死的**：取包 → **优雅关 Playnite（WM_CLOSE，等同点 ×，绝不强杀）** →
  写 `Extensions\Playnite-Vault\` → 按原样重启（用原来那个 Desktop/Fullscreen exe）。
  dll 被 Playnite 锁着，先写后关必然 Permission denied。
- 关不掉就**停下来问用户**，不擅自强杀（强杀会让 Playnite 丢未落盘的库改动）。
- 备份放 `%APPDATA%\VaultUnpacker\plugin-backup\<时间戳>\`，**绝不放 Extensions 里**
  （Playnite 会把那儿每个文件夹都当扩展加载）。改名前的 `VaultDemo_*` 一并挪走。
- 插件包来源：exe 内置（`--add-data <zip>;plugin`，`tools/plugin_payload/` 是产物、已 gitignore）
  或 GitHub/Gitee 最新 release。
- **Playnite 的进程名不止一个**：`Playnite.DesktopApp.exe` / `Playnite.FullscreenApp.exe`
  （+ 各自的 `.Admin`）。老代码只认 `Playnite.exe` → 「在不在跑」永远判错，
  会在 Playnite 开着时写 `local-index.json` 然后被覆盖。见 `playnite.PLAYNITE_EXE_NAMES`。
- **关窗口要连「不可见的主窗口」一起算**（v1.7.0 修）。Playnite 常驻托盘时主窗口
  `IsWindowVisible` 就是 `false` —— 只挑可见窗口的话一个都挑不出来，用户看到的是
  「已向 0 个窗口发出关闭请求 → 等满 60 秒 → 请手动退出 Playnite」，而其实给那个
  隐藏主窗口发 `WM_CLOSE` 和点 × 完全等效。现在：可见窗口优先，**全部不可见时
  退一步挑「标题非空且不是已知辅助窗口」的**（`_pick_close_targets`）。
  辅助窗口清单（`GDI+ Window` / `Default IME` / `CiceroUIWndFrame` / `.NET-BroadcastEventWindow`…）
  是照真机枚举出来的，不是猜的。
- **等待期间要能重试补发**。Playnite 还在启动、窗口尚未建出时，第一轮枚举是空的；
  那时若只发一次就干等，必然等满超时。现在 `request_close` 在等待循环里每秒重试一次，
  找到窗口就补发。
- **开了「关闭到托盘」时，WM_CLOSE 不会让它退出**（v1.8.0 实测，卡了 60 秒）。
  Playnite 主窗口收到 WM_CLOSE 后只是**隐藏回托盘**，进程照旧活着 ——
  于是注入器「已发出关闭请求 → 等满 60 秒 → 退出失败」，而它**拒绝强杀**（这是对的）。
  这种情况下唯一的正路是请用户右键托盘图标退出，再重跑；
  别去想办法绕过（强杀会丢未落盘的库改动，而这是注入流程里最不该破的红线）。
  判断当前是不是这一档：`tasklist | grep -i playnite` 里有进程、而窗口列表里没有可见窗口。
  → **给用户的话要写清楚「托盘右键 → 退出」**，只说「请关闭 Playnite」他不会意识到问题在哪。
- **内置插件包必须按版本号挑，不能按文件名排序**（v1.7.0 修）。`sorted(os.listdir())`
  取第一个 zip 是**字符串排序**：目录里 1.6.0 与 1.7.0 并存时会挑中**旧的**；
  而且将来 `1.10.0` 会排在 `1.9.0` 前面。现在 `_payload_version_key` 按数字段比大小，
  解析不出来的排最后（不会被选中）。

## 8. exe 打包的两条硬规矩

1. **`--exclude-module` 是上了膛的枪**。瘦身清单里曾长期排着 `concurrent`，而
   `vault_unpacker/core.py` 顶层就 `from concurrent.futures import ThreadPoolExecutor`
   → **打包出来的 exe 连 core 都导入不了，图形界面和解包全废**，而源码跑得好好的。
   现在 `build-vault-unpacker.py` 打完 exe 会立刻跑 `VaultUnpacker.exe --verify-frozen`
   （逐个 import 依赖 + 真跑线程池 + 真建窗 + 检查内置插件包），**不过就判构建失败**。
   动 `EXCLUDES` 之后务必看这一关的结论。
2. **入口必须先分流命令行**。`tools/VaultUnpacker.py` 以前只认 `--selftest`，其它参数
   一律开 GUI → `VaultUnpacker.exe --inject-plugin` 会一声不吭弹窗，脚本里像「跑完没反应」。
   现在带参数（含 `-h/--help`）走 `cli.main`，不带参数才开界面；命令行下导入失败
   **不许走阻塞 messagebox**，要报错 + 非零退出码。
   另：`vault_unpacker.cli` 只在带参数时才 import，**必须写进 `--hidden-import`**；
   `--windowed` 没控制台时 `sys.stdout` 可能是 `None` → 兜底写 exe 同目录
   `vault-unpack-cli.log`，输出统一 UTF-8。

## 9. 改名与数据迁移（v1.6.0 起）

- **Id 就是数据目录名**：`extension.yaml` 的 `Id` 既决定 `ExtensionsData\<Id>\`，
  又被 `build-release.py` 当成发布包里的文件夹名。
- `VaultUpdater` 里三处命名只认常量：`ExtensionId` / `ModuleFileName` / `AssetStem`。
- **Id 一改，老版本的自更新就接不上了**（它会去找旧资产名）→ 改名后的版本必须用
  **解包器注入**或手工装一次。
- `Services/VaultDataMigration.cs` 负责把老目录里的
  `settings.json` / `local-index.json` / `cache-index.json` / `state.json` /
  `theme-sync-state.json` **以及整棵 `meta-cache/`**（随包图片缓存，丢了下次导入要从 NAS 重下一遍）
  搬到新目录；老目录原地保留，迁移只在新目录空着时做一次。
  **判定「已有数据」时目录也算**（只有 `meta-cache` 也算已有），否则会把用户的旧配置盖掉。
- 日志文件随之改名：`playnite-vault.log`（原 `vault-demo.log`）。

## 10. 主题同步（v1.6.0 起）

- 远端 = **普通文件镜像，与本地同构**：`themes/{Desktop|Fullscreen}/{主题Id}/…`
  \+ `themes/index.json`（每主题指纹）+ `themes/{Mode}/{Id}/manifest.json`（路径→sha1）。
  NAS 上的树可以直接浏览、直接拷回来。**不用**游戏的区块模型（主题是小文件海，不需要）。
- **冲突 = 三方比较**（基准存 `theme-sync-state.json`）：一边变了就覆盖另一边；
  两边都变则 **mtime 新者胜**，输家原地留 `.conflict-local-<ts>` / `.conflict-remote-<ts>`
  （永不静默丢弃）。没有共同基准就按 mtime 定。**远端只增不减**（没有 PruneRemote）——
  本地删除不会镜像到远端，只在 `RemoteOnly` 计数里列出。
- 远端索引的 `Kind` 不是 `playnite-vault-themes` 就**报错停止**（防止把别人的目录当仓库写坏）。
- 引擎（`Services/ThemeSync.cs`）**不依赖 Playnite API**，只吃一个 `WebDavClient`，
  所以命令行和插件界面共用同一条生产代码路径：
  `VaultPack themes-sync --dir … --mode up|down|both [--dry-run] [--force]`；
  插件主菜单「同步主题到 NAS（上传）/ 从 NAS 同步主题（下载）」。
- `theme-sync-state.json` 与 `settings.json` 同目录 → 命令行与界面**共享同一份基准**。
- **主题目录名**：`theme.yaml` 的 `Id:` 是权威标识，**目录名只是给人看的**。
  依据是查用户机器上 Playnite 自己装的 61 个主题：24/25 桌面 + 30/36 全屏同时命中
  API `addonId` 与 `theme.yaml` 的 `Id:`（两者一致），**没有一个只命中 API addonId**，
  而 `Anthem` / `Hero` / `Player` / `TrailerLovers` 四个只命中 `theme.yaml` 的 `Id:`
  （库里的 addonId 已经漂移）。官方自带主题的目录甚至叫 `Default`，而它的 Id 是
  `Playnite_builtin_DefaultDesktop`。
  所以 `fetch-playnite-themes.py` 只在**目录名是裸 GUID**（作者清单里只写了 GUID，
  实测 Light Mode）时才改成 `theme.yaml` 的 `Id:` —— 名字可用就别动，
  因为远端只增不减，本地改个名会在 NAS 上永久多留一份同样内容。
- 识别「已装」时同时认 **目录名 / 清单 AddonId / theme.yaml Id**，带不带 `<Name>_` 前缀
  都按**尾部 GUID** 归一（无 GUID 的名字退回小写全名比对），所以重跑是幂等的。
- `.part` 文件跳过；`.conflict-*` 目录不进扫描；只有含 `theme.yaml` 的目录才算主题。
- 索引指纹刻意排除 `UpdatedAt`（同 §4）。

## 11. 自检（都要退出码 0）

| 工具 | 规模 | 说明 |
|---|---|---|
| `tools/VaultSelfTest/` | C# 303 项 | `TcpListener` 起本地假 GitHub/Gitee，**断言全实跑**，不联网、不碰真实仓库 |
| `tools/python-selftest/` | Python 109 项 | `python tools/python-selftest/run_all.py`；**设 `VAULT_TK_PYTHON` 指向带 tkinter 的解释器**，否则 GUI 与「真实 WM_CLOSE」两段会被跳过 |
| `tools/fetch-playnite-themes.py --selftest` | Python 25 项 | 离线；专盯「认主题 / 定目录名 / 解包防穿越」那套判断 |

`python-selftest` 四组：entry 27 / inject 42 / e2e 13 / gui 31。**e2e 那组会真的
优雅关掉本机正在跑的 Playnite**（它的第 1 段走的就是 `--inject-plugin` 那条真路径，
只是目标目录换成临时目录）—— 跑之前先确认自己没有在游戏里。

自检的价值已被反复证明：换源/兜底重试两个 bug、数据迁移、exe 导入失败、主题目录名、
zip 路径穿越都是它或同类检查抓出来的。**改动对应模块后先跑自检再谈别的。**

跑 C# 自检前先建：
`dotnet build tools/VaultSelfTest/VaultSelfTest.csproj -c Release -p:PlayniteDir=…`

## 12. 侧边栏页（v1.6.0 起）

- 用 `Plugin.GetSidebarItems()` + `SidebarItem.Type = SiderbarItemType.View`。
  Type 为 View 时，Playnite 会把 `Opened()` 返回的控件**直接嵌进主窗口当一页**
  （`DesktopAppViewModel_Sidebar.cs` 里 `model.ActiveView = view`），所以不用自己开窗口，
  界面也自动继承主窗口的主题与缩放。SidebarItem 在桌面端是个 `Button`，
  模板里的 `ContentPresenter` 绑的是 `SidebarWrapperItem.IconObject`。
- **图标必须是 UIElement**。`IconObject => SdkHelpers.ResolveUiItemIcon(SideItem.Icon)`；
  只有**字符串**会被当成主题资源键去查（Playnite 自己的库/统计图标就是
  `Media.xaml` 里的 `SidebarLibraryIcon` 这种 TextBlock 资源），其余对象原样透传。
  所以图标用 `Path` 画矢量，**不用字体字形** —— 换机器、换主题都不会变成方框。
- **`FrameworkElement` 只能在 STA 线程上构造**。Playnite 是在 UI 线程上调
  `GetSidebarItems()` 的，所以生产侧没问题；但自检主线程是 MTA，
  碰这段必须转到一个 STA 线程上（`Program.RunSta`）。这个坑是被自检抓出来的。
- 配色：正文色优先取主题的 `TextBrush`（对比度最稳），明暗按窗口背景亮度判断；
  取不到就用内置调色板兜底。**任何取不到主题资源的地方都必须有非 null 的兜底**，
  否则会出现「深色主题下黑字看不见」。
- **v1.8 起配色走令牌**（`UI/VaultPalette.cs`），页面里不再出现字面颜色值：
  中性色承担版面、`Accent` 只标选中与主操作、`Success/Warning/Danger/Info` 只给状态。
  明暗三档 `auto/light/dark` 存在 `VaultSettings.UiTheme`，**auto 判错时用户必须能手动覆盖**
  （判据是主窗口底色，主题把底色做成半透明或贴图就会判反）。
  自检里有 WCAG 对比度断言（正文/底色、强调色上的文字都要 ≥ 4.5），
  配色算错是静默的，只能靠断言盯。
- **v1.8 起顶栏被整个去掉**。原因不是审美：原来贴在右上角的「已连接 / 刷新」在窗口控制区
  （最小化/最大化/关闭）上方，两者位置重合，点刷新会顺手关掉 Playnite。
  现在只留一个不占版面、无底无框的状态点（`BuildHealthDot`），版本号挪到左栏产品名后面。
- 连通性状态必须**分档**：绿=通、黄=超时/不可达（NAS 没开机这类，等一会儿可能就好）、
  红=服务端明确拒绝或配置写错（401/403/域名解析/证书）、灰=还没配置。
  分档规则只有一处 `VaultPlugin.ClassifyHealthFailure`，自检逐档断言。
  把它糊成一个「红点」会让用户反复重试一个改不动的设置。
- **口令闸门只能有一道**：仓库管理里有不可逆删除。v1.8 起删除入口从「独占窗口」变成
  「侧边栏卡片 + 孤儿条目 + 管理窗口」三处，但**验口令 + 删**被钉在同一个方法里：
  `VaultPlugin.DeleteRepositoryApp`。别的任何地方都不许直接调 `VaultService.RemoveApp`
  —— 自检有一条调用点断言：“RemoveApp 的调用方只能是 DeleteRepositoryApp”，
  并且带两个正对照（`VerifyAdminPassword` 确实有两个调用方，其一个在另一个类型里），
  免得“没找到调用方”和“检测器坏了”分不清。
  检测器看的是 IL：“call/callvirt/newobj 后面跟着目标的 4 字节元数据令牌”，
  连操作码一起匹配，压掉单看令牌字节的误命中。
  另：`VaultAdminWindow` 必须经由 `VaultPlugin.OpenRepositoryManager()` 打开 ——
  侧边栏页直接 `new VaultAdminWindow` 也是开后门，自检从 IL 里搜 `newobj` 令牌盯着它。
- **卡片墙的对账规则抽在 `Services/LocalAppMatcher.cs`**（纯函数，不依赖 Playnite 运行时，
  自检能逐个键断言）。命中顺序：`Game.Id` GUID → `Game.GameId` 库内标识 →
  `MakeAppId` 推的 slug。两个坑：
  1. **没库内 ID 的条目千万不要在桶里占一个空键** —— 否则所有同样没库内 ID 的游戏
     都会被认成它，而这一页上“认错”的后果是用户把别人的归档当成自己的。
     所以建桶时空键直接跳过（自检有专门一条）。
  2. “老条目”的判据是**两把钥匙都没存**（v1.8 之前归档的），不是“没有 GUID” ——
     一个只存了库内 ID 的条目不是老条目，提示语不能张冠李戴。
- **封面加载三个必须**：`CacheOption=OnLoad`（默认的延迟加载会握着文件句柄，
  而游戏正在跑的时候那个文件是活的）；`DecodePixelWidth` 缩到卡片宽度
  （一屏两百张按原图解码要吃掉几百 MB）；解完 `Freeze()` 才能跨线程交给 UI。
- `VaultPanelView` 的构造签名 `(plugin, service, settingsVm, themesRoot)` 也是被自检钉住的契约
  （要加参数得同步改自检）。换配色需要**重建整棵视觉树**（颜色是建控件时烘进去的），
  所以 `VaultSettingsViewModel.SettingsSaved` 会触发 `RebuildForTheme()`。

## 13. 云存档（v1.7.0 起）

设计契约单独写在 **`docs/cloud-save.md`**（九节：复刻对象 / 仓库布局 / 快照与分支 /
保留策略 / 触发策略 / 路径来源与自适应 / 嗅探 / 接口入口 / 「绝不静默丢」红线）。
**改这块之前先读它。** 这里只记代码里看不出来的事。

- **Playnite 自带的云存档是「有骨架、没后端」**：桌面端里 `Game.SavePaths` 数据模型、
  存档管理窗口、嗅探器都在，但**没有云存储 DLL，也没有 `CloudStorage` 目录**。
  所以插件补的不是「接上同一个后端」，而是它缺的那层传输 —— 换成 NAS/WebDAV。
- **`SavePath` 的实测成员**（反射验过，SDK 6.13）：`GameSaveType` / `Path` / `Title`
  可写，`AutoAdaptive` 可写，**`Name` 只读**。别给 `Name` 赋值。
- **`Playnite.SDK.AutoAdaptivePathHelper` 是空壳**：`GetRealPath` / `TransformPath1`
  对 23 种写法（`{WinLocalAppData}`、`%LOCALAPPDATA%`…）**原样返回**，
  `IsAdaptivePath` 恒为 `false`。也就是说**不存在一套「Playnite 的 token 契约」可供对齐**，
  插件只能自持 token 表；`SavePathAdapter.FromPlaynite()` 负责把文档里那些写法搬到自己的表上。
  **别**指望调那个 helper 拿「标准答案」，它不会给。
- 路径来源是**两份合并**：Playnite 的 `Game.SavePaths` 优先，插件自己的 `save-paths.json`
  补充，按 `Title` 归并（`VaultSaveService.MergedPaths()`）。写回时也分开写：
  Playnite 来的行走 `api.Database.Games.Update`，插件来的行落 JSON。
- 对象存储沿用 v3 那套**内容寻址**（`objects/xx/<sha1>`）：同一份内容在不同快照、
  不同分支之间天然只存一份，不需要额外的去重表。
- `manifest.json` 带 `Kind` 守卫：**不是本插件写的存档仓库就直接拒绝**，
  免得把别人（或游戏自己的）manifest 当成存档索引去改。
- 保留策略**按分支独立算**：pinned 永不删、最新一份必留、`0` = 无限。
  任何删除都必须显式给 `AllowDelete`。
- **绝不静默丢**：恢复前先备份；有冲突就落 `.conflict-*`；上传前还会把远端现状推一份
  `before-restore` 快照兜底 —— 那份要用**另一个** `SaveSyncEngine` 实例跑，
  否则上传期的计数器会串进恢复期的统计里。
- 嗅探三层（常见位置 / 会话差分 / PCGamingWiki）+ 手动补充，**全部只产候选、绝不自动写入**。
  会话差分靠 mtime+size 指纹加「向上爬」父目录来定改动范围。
- PCGamingWiki 的 `{{p|...}}` 模板**必须按顶层 `|` 切**：路径里还有 `{{p|userprofile}}`
  这种带内嵌竖线的写法，naive split 一定切错。
- 命令行入口：`VaultPack saves --action list|branches|upload|download|delete|prune|sniff`；
  `delete` / `prune` 必须给 `--allow-delete`。
- 自检在 `RunSaveTests()`（路径自适应 / 同步引擎 / 嗅探三组），引擎全程对着
  **内存版 WebDAV** 跑，不联网。

## 14. 环境坑（本机特有）

- **Bash 工具缺基础命令**：先 `export PATH="/usr/bin:/bin:/usr/local/bin:$PATH"`，
  否则 `ls` / `head` / `dirname` / `rm` 全报 command not found（`rm` 挂了还会连累整条命令 exit 127）。
- **Grep 工具对含反斜杠的正则静默失配**（返回空结果而不是报错）→ 改用 `grep -F -e`。
- **PowerShell 工具的 stdout 可能完全不回显**（连 `Write-Output` 都是空的）→ 优先用 Bash，
  或把结果写文件再读。
- **同一个文件在一条消息里发两个 Edit 会丢改动** —— 两个编辑各自基于同一份快照写回，
  后写的把先写的覆盖掉。实测 `case "themes-sync":` 就这么凭空消失（编译时才暴露）。
  同一文件的多处改动用 MultiEdit，或一条消息只改一个文件。
- **GitHub HTTPS 容易被中断** → 退避重试；直连不通走局域网代理
  （`VAULT_HTTPS_PROXY` 环境变量，本机 `http://192.168.31.55:7890`）。
  未登录的 `api.github.com` 在共享代理出口上会被 IP 限流 → 用 PAT。
- 本地假服务的子进程 **stdout 别接 PIPE**（会死锁），重定向到文件。
- `.csproj` 的注释**不能含双横线** `--`（XML 注释非法，报 MSB4025）。
- **部署插件 dll 前先确认 `Playnite.DesktopApp.exe` 没在跑**，否则写不进去。
- **Gitee 建完 release 不能顺手 `attach_files`**，必须逐个 `POST /releases/{id}/attach_files`。
- Python 的**默认参数在函数定义时求值**：`def f(timeout=TIMEOUT)` 之后再改全局 `TIMEOUT`
  是改不动它的 → 要在函数体里 `timeout=timeout or TIMEOUT` 读全局。
  主题抓取脚本曾因此把每个请求都拖满 180s，一次下载卡了 12 分钟。
- Windows 上 **`os.path.isabs("/x")` 返回 `False`**（ntpath 要求分隔符出现在索引 >0 处），
  所以防路径穿越不能只靠 `isabs`，要自己判前导 `/` 和盘符。
- **Git Bash 里给 Windows 程序传路径要用 Windows 形式**：`python.exe /c/tmp/x.py`
  会被解释成 `C:\c\tmp\x.py`（报「can't open file」）→ 写 `C:/tmp/x.py`。
  同理，脚本里往 `/tmp/a.txt` 写的东西实际落在 **`C:\tmp\a.txt`**；找不到文件先看那儿。
- **`taskkill //F //PID` 在 Git Bash 里会被参数转换搞坏**（报「无效参数/选项 - '//F'」，
  而退出码还是 0，很容易以为杀掉了）→ 改用 PowerShell `Stop-Process -Id <pid> -Force`，
  并用 `Get-NetTCPConnection -LocalPort <port>` 回验端口确实释放。
- **`Popen(DETACHED_PROCESS)` 拉起的 GUI 进程，别在另一条命令里去查**：
  启动它的那条命令一结束，它可能已经被回收 → 看起来像「启动了又立刻退出」。
  把「启动 + sleep + 查进程」放进**同一条命令**里做。
- **PowerShell 工具查进程的 stdout 也可能为空**（`Get-Process | Select` 什么都不回）
  → 别把「没输出」当成「没进程」，用 `tasklist /FO CSV` 落文件再 grep。
- **`.NET` 单文件 exe 的 `PlayniteDir` 默认是 `$(ProgramFiles)\Playnite`**，本机 Playnite
  在 `D:\Game\Playnite` → 三个 csproj 编译都要显式给 `-p:PlayniteDir="D:\Game\Playnite"`
  或设同名环境变量，否则 `VaultPack` 会 CS0246。

## 15. Playnite SDK 的界面能力边界（v1.8 实测，做主题截图 / 云存档界面之前先读）

这些结论是拿 `Playnite.SDK.dll` 的 **XML 文档 + 元数据**逐个问出来的（本机 10.41）。
**别再凭印象写** —— 下面每一条都直接决定「某个功能能不能做」。

**结论先说：Playnite 只为插件开了「整页 / 菜单 / 顶栏 / 侧边栏」这几个口子，
没有给「往游戏详情面板里插按钮」或「往编辑游戏对话框加一栏」开任何口。**

- **读设置可以，写设置不行。** `IPlayniteAPI.ApplicationSettings` 的类型是
  `IPlayniteSettingsAPI`，它把 `DesktopTheme` / `FullscreenTheme` /
  `GridItemWidthRatio` / `GridItemHeightRatio` / `SidebarPosition` **全部暴露成只有 getter**。
  → 插件**无法**通过官方 API 切换主题，也**无法**读写 Playnite 自己的布局设置。
- **运行时应用主题只有一条路（非官方）**：`Playnite.dll` 里
  `Playnite.ThemeManager.ApplyTheme(Application app, ThemeManifest theme, ApplicationMode mode)`
  是 **public static**，还有 `SetCurrentTheme` / `DefaultDesktopThemeId` / `GetThemeRootDir`。
  要用只能反射调用（编译期不引 Playnite.dll 也能调）。
  风险：这是内部 API，Playnite 升级可能改签名 —— **必须 try/catch 兜住并降级**。
- **没有截图 API**。SDK 里搜不到 Screenshot 相关的任何成员。
  要截图只能自己对主窗口做 Win32 `PrintWindow` / `BitBlt`；
  拿窗口句柄用 `IDialogsFactory.GetCurrentAppWindow()` + `WindowInteropHelper`。
- **插件能挂 UI 的地方就这几个**：
  `GetSidebarItems()`（整页/按钮）、`GetMainMenuItems()`、`GetGameMenuItems()`、
  `GetTopPanelItems()`、`GetPlayActions()`、`GetInstallActions()`、`GetSettingsView()`、
  `GetGameViewControl(GetGameViewControlArgs{Mode, Name})`。
- **`GetGameViewControl` 不是「随便插」**：它由**主题**按名字来要（主题里得先有一个
  请求插件控件的占位元素），`Playnite/Themes/Desktop/Default` 里**没有**这种占位
  （只有 `Views/TopPanel.xaml` 里的 `PART_PanelMainPluginItems` 给顶栏用）。
  → 在默认主题/多数第三方主题下，"往「启动游戏」和「编辑游戏详情」之间加一个云朵按钮"
  **落不了地**；只有主题作者主动留了位置才行。
- **游戏详情面板 / 编辑游戏对话框都不可注入**：`Plugin` 基类里没有任何方法能往
  编辑游戏的对话框加一栏（那个对话框是 `Playnite.DesktopApp` 的内部窗口）。
  → 「在编辑游戏详情的『通用』项后加一项『云存档』」**做不到**。
- **但存档路径这件事有现成的原生 UI**：主题里有
  `CustomControls/GameSavePathSelectionBox.xaml`，说明 Playnite 自己的编辑游戏对话框里
  **已经有一处编辑 `Game.SavePaths` 的界面**。`SavePath` 的
  `GameSaveType / Path / Title / AutoAdaptive` 都是可写属性，`Name` 只读。
  → 想「用 Playnite 的窗口管存档路径」，正路是**让插件去读写 `Game.SavePaths`**，
  借 Playnite 那套原生编辑器，而不是自己再画一个。
- 顺带两条与界面无关但同源的事实：
  `Game.SavePaths` 的文档注释被 Playnite 写成了 "list of game related web links"（复制粘贴错误），
  以类型 `ObservableCollection<SavePath>` 为准；
  `SidebarItem.Icon` 是 `object`、`IconPadding` 是 `Thickness`，
  而 `Playnite.SDK.SidebarItem` 里的枚举拼写是 **`SiderbarItemType`**（少个 i，官方拼错的）。
- **`Game` 上没有 `DatabaseId`**（6.13 实测；`IGameDatabase` 也没有按 int 取游戏的入口：
  只有 `GetFilteredGames` / `GetGameMatchesFilter` / `ImportGame`）。
  所以「用数据库自增 id 当第二把钥匙」这件事**做不到**。
  `Game` 上能用来认游戏的键只有两个：`Id`（Guid，库记录身份）与
  `GameId`（string，库内标识：Steam 就是 appid；手动添加的游戏可能为空串）。
  另有一个只读的 `DatabaseReference`（指向 `IGameDatabase`），不是 id。
  → 想多一层对账就存 `Id` + `GameId`，别再去找 `DatabaseId` 了
  （Vault 的 `AppEntry.PlayniteGameId` / `PlayniteLibraryId` 就是这两个）。
