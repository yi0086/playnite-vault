# Vault 仓库格式规范

这份文档描述 NAS 上那个仓库的磁盘布局与文件格式。它足够简单，
任何语言都能在几百行内实现一个读取端（`tools/vault_unpacker/` 就是参照实现）。

---

## 1. 目录布局

```
<仓库根>/
├── index.json                       仓库总索引（客户端启动读一次）
└── apps/
    └── {Id}/                        一个应用一个目录，Id 就是目录名
        ├── manifest.json            该应用的清单
        ├── parts/
        │   ├── part-0000.bin        分片：若干文件字节的顺次拼接，无任何头部
        │   ├── part-0001.bin
        │   └── …
        └── meta/                    随包图片（封面 / 背景 / 图标）
            ├── cover.png
            └── …
```

> **v1 旧布局**：早期版本用 `apps/{Id}/files/<原始相对路径>` 一个文件一份地存，
> 清单里 `Packed = false`。读取端仍然兼容（见第 5 节）。

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

## 4. `manifest.json`

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

## 5. 还原算法

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
