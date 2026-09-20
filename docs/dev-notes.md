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

`python tools/publish-release.py <版本> --verify-download` —— 已固化，**别再写临时脚本发 release**。

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
- **主题目录名 = `theme.yaml` 的 `Id:`**（Playnite 认的就是它），**不是**清单里的 `AddonId`
  —— 后者有时是裸 GUID（实测 Light Mode），照抄会得到 Playnite 认不出的目录名。
- `.part` 文件跳过；`.conflict-*` 目录不进扫描；只有含 `theme.yaml` 的目录才算主题。
- 索引指纹刻意排除 `UpdatedAt`（同 §4）。

## 11. 自检（都要退出码 0）

| 工具 | 规模 | 说明 |
|---|---|---|
| `tools/VaultSelfTest/` | C# 143 项 | `TcpListener` 起本地假 GitHub/Gitee，**断言全实跑**，不联网、不碰真实仓库 |
| `tools/python-selftest/` | Python 85 项 | `python tools/python-selftest/run_all.py`；GUI 那项需 tkinter，可设 `VAULT_TK_PYTHON` |
| `tools/fetch-playnite-themes.py --selftest` | Python 22 项 | 离线；专盯「认主题 / 定目录名 / 解包防穿越」那套判断 |

自检的价值已被反复证明：换源/兜底重试两个 bug、数据迁移、exe 导入失败、主题目录名、
zip 路径穿越都是它或同类检查抓出来的。**改动对应模块后先跑自检再谈别的。**

跑 C# 自检前先建：
`dotnet build tools/VaultSelfTest/VaultSelfTest.csproj -c Release -p:PlayniteDir=…`

## 12. 环境坑（本机特有）

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
