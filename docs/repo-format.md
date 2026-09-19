# Vault 仓库格式规范

这份文档描述 NAS 上那个仓库的磁盘布局与文件格式。它足够简单，
任何语言都能在几百行内实现一个读取端（`tools/vault_unpacker/` 就是参照实现）。

---

## 1. 目录布局

```
<仓库根>/
├── index.json                       仓库总索引（客户端启动读一次）
├── vault-admin.json                 仓库管理口令（可选，见第 10 节）
└── apps/
    └── {Id}/                        一个应用一个目录，Id 就是目录名
        ├── manifest.json            该应用的清单
        ├── chunks/                  【v3】内容寻址区块，文件名 = 内容 SHA-1
        │   ├── 3b1f0c…a9.bin
        │   └── …
        ├── parts/                   【v2】按序号命名的分片（旧格式，仍可读）
        │   ├── part-0000.bin
        │   └── …
        └── meta/                    随包图片（封面 / 背景 / 图标）
            ├── cover.png
            └── …
```

> **v1 旧布局**：早期版本用 `apps/{Id}/files/<原始相对路径>` 一个文件一份地存，
> 清单里 `Packed = false`。读取端仍然兼容（见第 5 节）。
>
> **当前版本是 v3（内容寻址区块，见第 8 节）**。客户端必须能同时读 v1 / v2 / v3。
>
> ⚠️ **不要按 `Schema` 字段分派**，要按**字段特征**推断：
>
> | 条件 | 布局 |
> |---|---|
> | `Chunks` 非空 | v3（区块） |
> | `Packed == true` 且 `Parts` 非空 | v2（分片） |
> | 其余 | v1（逐文件） |
>
> 原因：`Schema` 是后加的字段，**v1 / v2 的清单里根本没有它**。
> 反序列化时它会拿到「属性默认值」，一旦默认写成当前版本（3），
> 任何旧清单都会被误判成 v3。C# 侧的 `AppManifest.Schema` 因此
> 默认值是 `0`（表示「未标注」），Python 侧则完全忽略这个字段。

---

## 2. 编码与约定

| 项 | 约定 |
|---|---|
| 文本编码 | UTF-8 **无 BOM** |
| JSON 缩进 | 2 空格（与 .NET `Newtonsoft.Json` 的 `Formatting.Indented` 一致） |
| 时间戳 | ISO-8601 UTC，**7 位小数** + `Z`，例 `2026-09-17T18:24:38.1171714Z` |
| 字段命名 | PascalCase，**大小写敏感** |
| 认证 | HTTP Basic（`Authorization: Basic base64(user:password)`） |
| 用到的 WebDAV 方法 | `HEAD` / `GET` / `PUT` / `MKCOL` / `PROPFIND`（`Depth: 1`）/ `DELETE` |
| 路径分隔符 | 清单内一律用 `/` |

---

## 3. `index.json`

仓库总索引。客户端启动时读一次；读不到时要回退到本地缓存（否则库会被清空）。

```jsonc
{
  "Schema": 2,
  "Name": "Vault Store",
  "UpdatedAt": "2026-09-17T18:24:38.1171714Z",
  "Apps": [
    {
      "Id": "Brotato",                 // 应用唯一标识，也是目录名
      "Name": "Brotato",               // 展示名（可含中文）
      "Version": "1.0",
      "TotalBytes": 857000000,         // 原始（未压缩）总字节
      "FileCount": 421,
      "LaunchExe": "Brotato.exe",      // 启动程序，相对应用根目录
      "PartCount": 7,
      "Packed": true,                  // true = 分片布局；false = v1 逐文件布局
      "Metadata": { … }                // 随包元数据，结构见第 6 节
    }
  ]
}
```

---

## 4. `manifest.json`（Schema 2）

> ⚠️ 本节描述的是 **Schema 2**（按序号分片）。当前写入格式是 **Schema 3**，见第 8 节。
> 保留本节是因为读取端仍需兼容旧归档。

单个应用的完整清单。**读取端只需要这一个文件就能还原全部内容**。

```jsonc
{
  "Schema": 2,
  "Id": "Brotato",
  "Name": "Brotato",
  "Version": "1.0",
  "UpdatedAt": "2026-09-17T18:24:38.1171714Z",
  "LaunchExe": "Brotato.exe",
  "WorkingDir": "{InstallDir}",        // 占位符，落地后由客户端按实际安装目录替换
  "TotalBytes": 857000000,             // 原始总字节（= 所有 Files[].Size 之和）
  "Packed": true,
  "PartSize": 268435456,               // 分片目标大小（字节）

  "Parts": [
    {
      "Index": 0,                      // 分片序号，与文件名 part-%04d.bin 对应
      "Path": "parts/part-0000.bin",   // 相对 apps/{Id}/ 的路径
      "StoredBytes": 268435456,        // 该分片实际落盘字节
      "RawBytes": 270000000            // 该分片里所有文件原始大小之和
    }
  ],

  "Files": [
    {
      "Path": "Brotato.exe",           // 相对应用根目录，用 /
      "Size": 1234567,                 // 原始字节（解压后应等于这个值）
      "Part": 0,                       // 落在哪个分片
      "Offset": 0,                     // 在该分片里的起始偏移
      "StoredSize": 1234567,           // 在该分片里占用的字节数
      "Compression": "store",          // "store" | "deflate"
      "IsStored": true                 // Compression == "store" 的冗余标记
    }
  ],

  "Metadata": { … }                    // 见第 6 节
}
```

关键约束：

- **一个文件不会跨分片**。分片按「整文件」装填，`Offset + StoredSize <= StoredBytes`。
- `Parts[]` 与 `Files[]` 用 `Part` / `Index` 关联，`Offset` 按分片内升序排列。

---

## 5. 还原算法（Schema 1 / 2）

> v3 的还原算法见第 8 节末。

```
解一个文件 entry：
    blob = read( parts/part-{entry.Part:04d}.bin,
                 offset = entry.Offset,
                 length = entry.StoredSize )

    # 裸 deflate（RFC 1951，无 zlib 头）—— 注意是 -15 而不是默认窗口
    content = blob                        if entry.Compression 是 "store" 或为空
              raw_inflate(blob)           if entry.Compression == "deflate"

    assert len(content) == entry.Size     # 大小不符即为数据损坏，应当报错停下
    写盘到 <输出目录>/<entry.Path>
```

注意事项：

- **路径必须做越界校验**。`entry.Path` 来自远端 JSON，属于不可信输入：
  `..\..\Windows\System32\...` 这种路径一旦直接拼接就会写到输出目录之外。
  参照实现里的 `_safe_target()` 会把规范化后的路径限制在输出目录之下。
- `deflate` 是**裸流**，没有 zlib/gzip 头，所以要用 `-15` 的窗口位（`zlib.decompressobj(-15)`）。
  用默认参数解会直接报 `incorrect header check`。
- 分片可以边下边解，不必先落盘全部：`StoredBytes` 与 `Offset` 已经足够做随机读取。

**v1 旧布局**（`Packed = false`）：内容不在 `parts/` 里，而是逐个文件放在
`apps/{Id}/files/<entry.Path>`，直接 GET 即可，不存在分片与压缩。

---

## 6. `Metadata`（随包元数据）

归档时把 Playnite 库里的元数据一起写进清单，装机端直接读，省掉一次网络刮削。

```jsonc
{
  "Name": "Brotato",
  "SortingName": "Brotato",
  "Description": "…",
  "Version": "1.0",
  "ReleaseDate": "2023-06-23",
  "Source": "Steam",
  "Developers": ["Blobfish"],
  "Publishers": ["Blobfish"],
  "Genres": ["Roguelike", "Shooter"],
  "Categories": ["…"],
  "Tags": ["…"],
  "Features": ["…"],
  "Series": ["…"],
  "Platforms": ["PC (Windows)"],
  "Regions": ["…"],
  "AgeRatings": ["…"],
  "Links": [ { "Name": "官网", "Url": "https://…" } ],
  "CommunityScore": 88,
  "CriticScore": 84,
  "UserScore": 90,
  "InstallSize": 857000000,
  "Hidden": false,
  "Favorite": true,
  "LaunchArguments": "…",
  "LaunchWorkingDir": "…",
  "InstallDirName": "Brotato",        // ★ 见下
  "Images": {
    "cover": "apps/Brotato/meta/cover.png",
    "background": "apps/Brotato/meta/background.png",
    "icon": "apps/Brotato/meta/icon.png"
  }
}
```

### `InstallDirName` —— 本地子目录名

归档时记下**原始安装目录的最后一级文件夹名**：

```
归档前装在  D:\Games\Brotato   →   InstallDirName = "Brotato"
```

解包时用它当本地子目录名，于是解出来的目录和上传前保持一致，
而不是变成仓库里的 slug（`{Id}`）。旧归档没有这个字段，读取端退回用 `Id`。

**读取端必须把这个字段当不可信输入**：它来自远端 JSON，直接拼路径会被
`..\..\Windows` 之类的名字穿越出去。参照实现 `core.sanitize_folder_name()` 的规则是：

- 拒绝空串、`""` / `"  "`、`.`、`..`
- 拒绝含 `<>:"/\|?*` 或控制字符（`ord < 32`）的名字
- 拒绝 Windows 保留设备名（`CON` / `PRN` / `AUX` / `NUL` / `COM1-9` / `LPT1-9`，不区分大小写）
- 拒绝长度 > 128
- 不合法就退回 `Id`

---

## 7. 写入顺序（对并发安全有意义）

上传一个应用时按这个顺序做，保证任何时刻的仓库都是一致可读的：

```
1. MKCOL  apps/{Id}/              必要时
2. PUT    apps/{Id}/parts/*       逐个分片上传（可并发；同名分片内容相同，可跳过）
3. PUT    apps/{Id}/meta/*        图片
4. PUT    apps/{Id}/manifest.json 清单 —— 最后写，写完这个应用就可读了
5. PUT    index.json              最后更新总索引
```

反过来说，客户端读到 `manifest.json` 就意味着该应用的分片已经齐了，
所以 `manifest.json` 必须在所有分片之后才写。

删除的顺序是反的：先删 `apps/{Id}/` 下的所有文件（再由深到浅删目录），
最后才从 `index.json` 摘掉条目。中间态是「索引里还列着，但读 manifest 会 404」。
读取端应当把这种情况当作**条目暂时不可用**（提示重试 / 跳过），
而不是当成致命错误 —— 这一点对「边删边浏览」的场景很重要。

---

## 8. Schema 3：内容寻址区块（当前版本）

### 为什么换

Schema 2 的分片有两个致命问题，实测都会导致大游戏传不上去：

1. **大文件不切片**。「单个超过 PartSize 的大文件独占一片」这条规则，
   遇到真实游戏会造出巨大的分片。实测：
   `Dead Cells/res.pak` = 2,071,694,886 字节（1.93 GiB）→ 一个分片 1.93 GB。
   单个 PUT 推 1.93 GB，在 NAS 实测写入速率下需要约 100 秒，
   而客户端超时是 30 秒 → **必然失败，而且每次都卡在同一个分片**。
2. **分片名按序号**（`part-0000.bin`），内容与前缀无关。于是
   ① 重传时无法按内容判断「这片已经在远端了」；
   ② 一个文件变化会让后面所有分片重新编号 → 增量形同虚设。

Schema 3 改成**内容寻址**：每个区块的文件名就是它内容的 SHA-1。
区块一旦写出就不可变，名字即校验和。于是

- 大文件被自然切成多个区块，不再出现超大分片；
- 「远端是否已有这个区块」变成一个 `HEAD` 请求，**失败重传天然可续传**；
- 内容没变的部分区块名不变，二次归档自动跳过。

### 目录布局（v3）

```
<仓库根>/
├── index.json
├── vault-admin.json                 管理员密码（哈希，见第 10 节）—— 可选
└── apps/
    └── {Id}/
        ├── manifest.json
        ├── chunks/                  内容寻址区块
        │   ├── 3b1f0c…a9.bin        文件名 = 内容的 SHA-1（40 位小写 hex）
        │   └── …
        └── meta/
```

> **区块不做跨应用去重**。虽然 `chunks/<sha1>` 天然具备全局去重的条件，
> 但跨应用共享需要引用计数才能安全删除，收益（主要是重复的运行时 DLL）
> 远小于复杂度，所以区块**归属于应用**，删应用时整目录删掉即可。

### 切块算法（必须确定性）

确定性是「跳过已传区块」的前提。规则只依赖**单个文件自身的内容**与一个全局参数：

```
ChunkSize = 33554432            # 32 MB，可在设置里改

files = 按 Path 升序（OrdinalIgnoreCase）排序的文件表
buffer = 空；buffered = 0

for file in files:
    remaining = file.Size；fileOffset = 0
    while remaining > 0:
        n = min(ChunkSize - buffered, remaining)
        把 file 的 [fileOffset, fileOffset+n) 追加进 buffer
        记一条 Piece { Chunk=<当前块>, Offset=buffered, Length=n,
                       Size=n, Compression="store", FileOffset=fileOffset }
        buffered += n；fileOffset += n；remaining -= n
        if buffered == ChunkSize: 封块 → 算 SHA-1 → Chunks 里登记 → buffer 清空

封掉最后一个非满块（若有）
```

要点：

- **一个文件可以跨多个区块**（这正是修掉 1.93 GB 超大分片的地方）。
- 小的连续文件会被装进同一个区块，所以 17302 个文件的 MiSide 仍然是
  约 95 个区块，而不是 17302 个。
- 封块时只知道内容、不知道名字，所以实现上是「先把区块写到临时文件并同时
  增量算 SHA-1，封块后按哈希改名/上传」。**不必把 32 MB 全放进内存**。
- 同一个应用内内容相同的区块按哈希去重，只登记与上传一次。

### `manifest.json`（Schema 3）

```jsonc
{
  "Schema": 3,
  "Id": "plants-vs-zombies-rh",
  "Name": "植物大战僵尸融合版",
  "Version": "1.0",
  "UpdatedAt": "2026-09-19T03:20:11.4820513Z",
  "LaunchExe": "PlantsVsZombiesRH.exe",
  "WorkingDir": "{InstallDir}",
  "TotalBytes": 640000000,
  "Packed": true,
  "ChunkSize": 33554432,

  "Chunks": [
    {
      "Id": "9f2a…c41",                        // 内容 SHA-1，40 位小写 hex
      "Path": "chunks/9f2a…c41.bin",           // 相对 apps/{Id}/
      "StoredBytes": 33554432,                 // 网络上的字节数
      "RawBytes": 33554432                     // 还原后字节数
    }
  ],

  "Files": [
    {
      "Path": "data.unity3d",
      "Size": 497600000,
      "Pieces": [                              // 按 FileOffset 升序，拼起来必须正好等于 Size
        { "Chunk": 0, "Offset": 0,        "Length": 33554432, "Size": 33554432, "Compression": "store" },
        { "Chunk": 1, "Offset": 0,        "Length": 33554432, "Size": 33554432, "Compression": "store" },
        { "Chunk": 2, "Offset": 12345678, "Length":  4096,    "Size":  4096,    "Compression": "deflate" }
      ]
    }
  ],

  "Metadata": { … }
}
```

约束：

- `Pieces` 里相邻两项必须**首尾相接**（前一项 `FileOffset + Size` == 后一项 `FileOffset`），
  总和必须等于 `Files[].Size`。读取端应当校验，不符即为清单损坏，**必须报错停下**
  而不是写出半截文件。
- `Chunk` 是 `Chunks[]` 的下标（不是哈希），既省空间也便于读取端建索引。
- `Offset + Length <= Chunks[Chunk].StoredBytes`。
- 压缩是**逐 Piece** 的（不是逐块）：默认全 `store`；开压缩时短 Piece 反而会变大，
  所以实现里对 `< 512` 字节的 Piece 一律强制 `store`。

### `index.json` 的增量

`Schema` 升到 3，条目上 `PartCount` 改名为 `ChunkCount`
（读取端两者都认，取先存在的那个）。

### 还原算法（v3）

区块可以**乱序、并行**地取，取到一块就立刻落盘并删除，磁盘峰值与包体无关：

```
按需下载的区块并行取回
for 每块 chunk in 下载到的区块:
    for 每个引用该块的 Piece p（可能分散在多个文件里）:
        blob = 读 chunk 的 [p.Offset, p.Offset + p.Length)
        content = blob                     if p.Compression 是 "store" 或空
                  raw_inflate(blob)        if p.Compression == "deflate"
        assert len(content) == p.Size
        在目标文件的 p.FileOffset 处写入（随机访问，FileMode.OpenOrCreate + Seek）
    删除该区块
```

因为写入位置由清单给出，**同一个区块里的多个文件片段可以各自落到正确的偏移**，
不需要「一个区块对应一个整文件」。这是 v3 相对于 v2 的额外好处：
v2 必须凑出完整文件才能写，v3 可以按块增量地拼出大文件。

峰值临时占用 ≈ `并发数 × ChunkSize + 64 MB`（v2 是 `(并发数+2) × PartSize`，
默认参数下 2 GB → 现在 192 MB）。

### 断点续传 / 重试（v3 的关键收益）

- 上传前对每个区块 `HEAD chunks/<Id>.bin`，`Content-Length` 等于 `StoredBytes` 就跳过。
  **区块不可变 + 内容寻址，所以这个检查是充分可信的**（v2 做不到这点）。
- 单个区块失败只重试该区块，带指数退避；连续失败到上限才放弃整个归档。
- 整个归档失败后再点一次，已完成区块全部跳过，**不会再从第 0 片重来**。
- 因此「归档」操作是幂等的，可以放心重试。

### 超时语义（v3 一并修掉）

`HttpWebRequest.Timeout` 语义上是 `n × 1000` 毫秒（10 秒起步的公式），**不适合当传输超时**：
它管的是 `GetRequestStream()` / `GetResponse()`，而 PUT 的 `GetResponse()`
要等服务端把整个 body 收完、落盘、回包。一个大分片就必然撞上它。

v3 改成三个独立的量：

| 量 | 含义 | 建议 |
|---|---|---|
| 连接超时 | TCP/TLS 建连 | 15 秒 |
| **停滞超时** | **连续多少秒没有任何字节流动 ** | 30 秒 |
| 应答超时 | body 发完后等服务端回包 | 180 秒 |

停滞检测在读写循环里记录「最后一次成功读写的时间」，超时即主动 `Abort()`
并交给重试逻辑——这才对应「速度逐渐到 0」这种真实故障。

---

## 9. 落盘目录名：`Id` 与文件夹名是两件事

**这两个名字必须分开**，混用就是「中文目录」问题的根源：

| 名字 | 用在哪 | 规则 |
|---|---|---|
| `Id` | 仓库路径 `apps/{Id}/`、`index.json` 的键、Playnite 的 `GameId` | **ASCII slug**，只含 `[a-z0-9-]` |
| `InstallDirName` | 解包 / 安装时的**本地子目录名** | 归档前安装目录的最后一段，原样保留（`Plants Vs Zombies RH`） |

归档时的取值顺序：

```
installDirName = 归档前 game.InstallDirectory 的最后一段    # 例 "Plants Vs Zombies RH"
Id             = slug(installDirName)                      # 例 "plants-vs-zombies-rh"
                 ↓ 取不到安装目录时
                 slug(game.Name)                           # 例 "plant-vs-zombie-fusion"
```

`slug()` 只保留 `[A-Za-z0-9]` 并折叠其余字符为 `-`、转小写、截断到 48 字符；
**中文一律不保留**（中文不是合法 slug，且会带来 URL 编码与跨平台大小写问题）。
若结果为空（例如名字全是符号），退回 `app-<8 位随机>`。

读取端的顺序：

```
folder = sanitize_folder_name(InstallDirName)    # 见第 6 节的清洗规则
         or sanitize_folder_name(Id)             # 旧归档没有该字段
```

**为什么必须要 `InstallDirName` 而不是 `Id`**：有些程序（Unity 打包的老游戏、
写死相对路径的启动器）在中文路径下会启动失败或读不到资源，
而 `Id` 是给仓库用的 slug，不能假定它等于用户原来的目录名。

---

## 10. `vault-admin.json`：仓库管理口令

仓库根下的一份可选文件。**没有它 = 尚未设置管理员口令**，
此时客户端对任何写操作（删除应用等）都应当先提示去设置，而不是直接执行。

```jsonc
{
  "version": 1,
  "algorithm": "pbkdf2",
  "hmac": "sha1",             // 见下方「为什么不是 sha256」
  "iterations": 200000,
  "salt": "base64…",          // 16 字节随机
  "hash": "base64…",          // PBKDF2(口令, salt, iterations) 的 32 字节结果
  "updatedAt": "2026-09-19T03:20:11.4820513Z"
}
```

> **字段名是 camelCase，是本格式里唯一的例外**（第 2 节说全库用 PascalCase）。
> 原因：这份文件由 `VaultAdmin` 类用 `[JsonProperty]` 显式钉死字段名，
> 而它是 v1.4.0 才加的，没有历史包袱，就顺手用了常规 JSON 风格。
> 读取端**不要**用驼峰/帕斯卡互转的宽松解析 —— 直接按上面的名字取。

- **只存派生值，永不存明文或可逆密文。**
- 校验用固定时间比较（先比长度再逐字节 XOR 累积，不做提前返回）。
- 修改口令需要先验证旧口令；旧文件不存在则视为「首次设置」，直接允许设置。
- 这份文件由客户端直接 PUT 到仓库根（WebDAV 账号本身就有写权限）。

### 为什么是 HMAC-SHA1 而不是 SHA256

目标框架是 .NET Framework 4.6.2，而它的 `Rfc2898DeriveBytes` **只支持 SHA-1**
（带 `HashAlgorithmName` 的重载要 4.7.2+，为这一个功能抬目标框架会牵动整个插件）。
口令走 UTF-8 字节，Python 端用下面这行就能对齐：

```python
hashlib.pbkdf2_hmac('sha1', password.encode('utf-8'), salt, iterations, dklen=32)
```

200k 次迭代 + 16 字节随机盐，对「防误触」这个定位完全够用。

> **诚实说明它的边界**：这不是真正的访问控制。WebDAV 账号本身就有删除权限，
> 任何能替换本机 `settings.json` 或其他客户端实现的人都能绕过这个口令
> —— 所以校验**一律读仓库里的这份文件**，不信任本地缓存的那份派生值。
> 它的作用是**防误删的操作闸门**（删错游戏、手滑），不是防攻击者的安全边界。
> 真要隔离权限，得在 NAS 侧给不同账号配不同目录权限。

---

## 11. 清理与过期区块

区块文件放在 `apps/{Id}/chunks/` **里面**（而不是一个全局的 `chunks/` 池），
所以删除一个应用只需要递归删掉 `apps/{Id}/`，它名下所有区块随之消失
（见第 7 节的删除顺序）。

代价是**去重只在单个应用内生效**：两个应用里同样的文件会各存一份。
换来的是「删除」永远是简单且完整的 —— 不需要跨所有应用的清单做引用计数，
也就不会出现「删了应用但区块留着占空间」或「误删了别人还在用的共享区块」。
在一个应用动辄几 GB、重复文件通常只占百分之几的场景下，这个取舍划算。

### 过期区块要显式回收

区块**不可变**（文件名就是内容哈希，改了内容就是另一个文件），所以重新归档
不会覆盖旧块，而是写出新块。下面三种情况会让旧块变成没人引用的垃圾：

- 源目录里的文件被删掉或改动过（最典型：游戏更新后重新归档）
- 归档时换了 `ChunkSizeMB`，切块边界一变，哈希全都不一样了
- 上次归档中途失败，只传了一部分

所以**每次归档在写完新清单之后**，客户端会列一遍 `chunks/`，
把不在新清单 `Chunks[]` 里的文件删掉。这一步放在写清单**之后**是有意的：
从新清单落盘那一刻起，其余区块就是纯垃圾，删它不影响任何读取方。

> 删除操作顺带一提：因为区块只在应用自己的目录里，删应用不需要引用计数，
> 也就不会有孤儿（见第 7 节）。过期回收只是覆盖「同一个应用重新归档」这条路径。

---

## 12. 一致性验证

`tools/e2e-v3-test.py` 是这套格式的回归测试：自己起一个本地假 WebDAV，
跑完「造目录 → 打包上传 → 查落盘结构 → 解包还原 → 逐字节比对 → 中断续传」，
不需要 NAS。它会断言这些容易写错的点：

- 区块文件名确实是 40 位小写 sha1（内容寻址的前提）
- 仓库里任何路径都不含非 ASCII 字符
- 每个文件的片段 `FileOffset` 首尾相接，且合计等于 `Size`
- **至少有一个文件真的跨了多个区块**（否则这条用例根本没测到 v3 的关键点）
- 零字节文件被建了出来（v3 里它不产生任何片段）
- 非 ASCII 文件名能原样还原
- 下载结束后临时目录是空的（「下完即删」）
- 中断后留下续传日志，再跑一次能跳过已完成区块并得到一致结果

已通过的规模：223 MB / 28 区块，以及 **2.10 GB / 270 区块**（含一个 1.93 GB 的单文件，
即当初「大游戏传不上去」的规模）。
