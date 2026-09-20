using System;
using System.IO;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 插件改名带来的「数据目录搬家」。
    ///
    /// 背景：Playnite 用 extension.yaml 里的 <c>Id</c> 当扩展数据目录名，
    /// 所以 v1.6.0 把 Id 从 <c>VaultDemo_&lt;guid&gt;</c> 改成 <c>Playnite-Vault</c> 之后，
    /// <c>GetPluginUserDataPath()</c> 会指向一个全新的空目录 ——
    /// 不搬的话用户的 WebDAV 地址、口令、本地库索引（哪些游戏解在哪个目录）全丢。
    ///
    /// 策略：**只在新目录还没有任何本插件数据时**搬一次，且只搬已知的几个文件，
    /// 老目录原地留着不动（万一搬错了还能人工翻回去）。
    /// </summary>
    public static class VaultDataMigration
    {
        /// <summary>旧安装目录 / 旧数据目录里那一串 GUID。</summary>
        public const string LegacyGuid = "5e76bf50-cb8a-4a87-ad24-1912c746c6f0";

        /// <summary>
        /// 老目录可能叫的名字：
        ///   <c>VaultDemo_&lt;guid&gt;</c> —— 老安装目录名（有人手工把数据放这儿）
        ///   <c>&lt;guid&gt;</c>          —— Playnite 实际用的数据目录名
        /// </summary>
        public static readonly string[] LegacyDirNames =
        {
            "VaultDemo_" + LegacyGuid,
            LegacyGuid
        };

        /// <summary>要搬的文件。多一个不搬：update/ 里是过期暂存包，搬过去只会白占地方。</summary>
        public static readonly string[] MigratedFiles =
        {
            "settings.json",
            "local-index.json",
            "cache-index.json",
            "state.json"
        };

        /// <summary>
        /// 要整个搬过来的子目录。
        ///
        /// <c>meta-cache</c> 是从仓库拉下来的随包图片缓存（每个 appId 一个子目录，
        /// 放封面 / 背景 / 图标）。丢了不会坏，但下次导入要从 NAS 重下一遍 ——
        /// 这跟「装回去不用重新刮削」的初衷相悖，所以一起搬。
        /// </summary>
        public static readonly string[] MigratedDirs =
        {
            "meta-cache"
        };

        /// <summary>
        /// 需要搬就搬。返回实际搬过来的文件名（0 个表示没搬 / 不需要搬）。
        /// 任何异常都吞掉 —— 搬家失败不该让插件起不来。
        /// </summary>
        public static string[] MigrateIfNeeded(string dataPath)
        {
            var nothing = new string[0];
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                return nothing;
            }

            try
            {
                Directory.CreateDirectory(dataPath);

                // 新目录已经有数据 → 说明早就用上了，绝不能拿老的盖回来
                if (HasData(dataPath))
                {
                    return nothing;
                }

                var parent = Directory.GetParent(dataPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (parent == null || !parent.Exists)
                {
                    return nothing;
                }

                foreach (var legacyName in LegacyDirNames)
                {
                    var legacyDir = Path.Combine(parent.FullName, legacyName);
                    if (!Directory.Exists(legacyDir))
                    {
                        continue;
                    }

                    var moved = new System.Collections.Generic.List<string>();
                    foreach (var name in MigratedFiles)
                    {
                        var src = Path.Combine(legacyDir, name);
                        if (!File.Exists(src))
                        {
                            continue;
                        }

                        var dst = Path.Combine(dataPath, name);
                        File.Copy(src, dst, false);
                        moved.Add(name);
                    }

                    foreach (var name in MigratedDirs)
                    {
                        var src = Path.Combine(legacyDir, name);
                        if (!Directory.Exists(src))
                        {
                            continue;
                        }

                        if (CopyDirectory(src, Path.Combine(dataPath, name)))
                        {
                            moved.Add(name + "/");
                        }
                    }

                    if (moved.Count > 0)
                    {
                        VaultLog.Info("插件改名：已把旧数据目录的数据搬过来 " + legacyDir
                                      + " → " + dataPath + "（" + string.Join(", ", moved.ToArray()) + "）");
                        return moved.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("旧数据目录迁移失败（不影响使用，必要时手工拷贝）：" + ex.Message);
            }

            return nothing;
        }

        /// <summary>新数据目录里是否已经有本插件的东西（有就不搬，避免覆盖）。</summary>
        private static bool HasData(string dataPath)
        {
            foreach (var name in MigratedFiles)
            {
                if (File.Exists(Path.Combine(dataPath, name)))
                {
                    return true;
                }
            }

            foreach (var name in MigratedDirs)
            {
                var dir = Path.Combine(dataPath, name);
                if (Directory.Exists(dir)
                    && Directory.GetFileSystemEntries(dir).Length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>递归拷贝目录，返回是否真的拷到了文件。单文件失败跳过，不中断整体搬家。</summary>
        private static bool CopyDirectory(string src, string dst)
        {
            var copied = false;
            Directory.CreateDirectory(dst);

            foreach (var file in Directory.GetFiles(src))
            {
                try
                {
                    File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), false);
                    copied = true;
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("迁移目录时跳过文件 " + file + "：" + ex.Message);
                }
            }

            foreach (var dir in Directory.GetDirectories(src))
            {
                if (CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir))))
                {
                    copied = true;
                }
            }

            return copied;
        }
    }
}
