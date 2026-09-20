using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace VaultSelfTest
{
    /// <summary>造测试用的小仓库素材：release JSON + 插件 zip。</summary>
    public static class Fixtures
    {
        /// <summary>旧版插件的数据目录名（迁移逻辑要认它）。</summary>
        public const string LegacyDataGuid = "5e76bf50-cb8a-4a87-ad24-1912c746c6f0";

        /// <summary>插件 dll 文件名（跟着项目大名走）。</summary>
        public const string DllName = "PlayniteVault.dll";

        /// <summary>发布包顶层目录名 = extension.yaml 的 Id = 扩展数据目录名。</summary>
        public static string PluginFolder
        {
            get { return "Playnite-Vault"; }
        }

        /// <summary>和真实发布一致的 GitHub release JSON 形状。</summary>
        public static string GitHubReleaseJson(string version, string assetUrl, long assetSize,
            string assetName = null)
        {
            assetName = assetName ?? ("PlayniteVault-" + version + ".zip");
            return "{"
                 + "\"tag_name\":\"v" + version + "\","
                 + "\"name\":\"v" + version + "：自动更新 + 自动刷新远端库\","
                 + "\"body\":\"- 启动时自动检查更新\\n- 自动刷新远端库\","
                 + "\"html_url\":\"https://github.com/yi0086/playnite-vault/releases/tag/v" + version + "\","
                 + "\"assets\":["
                 + "{\"name\":\"" + assetName + "\",\"browser_download_url\":\"" + assetUrl
                 + "\",\"size\":" + assetSize + "},"
                 // 同一个 release 里还挂着解包器 exe —— 插件必须挑 zip 而不是它
                 + "{\"name\":\"VaultUnpacker-" + version + ".exe\","
                 + "\"browser_download_url\":\"" + assetUrl + ".exe\",\"size\":12600000}"
                 + "]}";
        }

        /// <summary>Gitee 的 release JSON（字段名和 GitHub 一致，附件里偶尔只给 url）。</summary>
        public static string GiteeReleaseJson(string version, string assetUrl, long assetSize)
        {
            return "{"
                 + "\"id\":1153197,"
                 + "\"tag_name\":\"v" + version + "\","
                 + "\"name\":\"v" + version + "\","
                 + "\"body\":\"Gitee 侧说明\","
                 + "\"assets\":["
                 + "{\"name\":\"PlayniteVault-" + version + ".zip\",\"browser_download_url\":\"" + assetUrl
                 + "\",\"size\":" + assetSize + "},"
                 + "{\"name\":\"PlayniteVault-" + version + ".zip.md5\",\"url\":\"" + assetUrl + ".md5\","
                 + "\"size\":32}"
                 + "]}";
        }

        /// <summary>
        /// 一个结构正确的发布包：外层是 Playnite-Vault 目录，
        /// 里面有 dll / extension.yaml / icon.png / 中文名的使用说明，外加一段填充数据把体积撑起来
        /// （体积要够大，速度地板才可能被触发）。
        /// </summary>
        public static byte[] BuildPluginZip(string version, int fillerBytes, int seed = 1)
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                Add(zip, PluginFolder + "/" + DllName, DllBytes(version));
                Add(zip, PluginFolder + "/extension.yaml", Encoding.UTF8.GetBytes(
                    "Id: " + PluginFolder + "\n"
                    + "Name: Playnite Vault\n"
                    + "Author: Yi0086\n"
                    + "Version: " + version + "\n"
                    + "Module: " + DllName + "\n"
                    + "Type: GameLibrary\n"
                    + "Icon: icon.png\n"));
                Add(zip, PluginFolder + "/icon.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
                Add(zip, PluginFolder + "/使用说明.txt", Encoding.UTF8.GetBytes("Vault 插件 —— 简易使用说明\n"));

                if (fillerBytes > 0)
                {
                    var filler = new byte[fillerBytes];
                    var random = new Random(seed);
                    random.NextBytes(filler);
                    Add(zip, PluginFolder + "/filler.bin", filler);
                }
            }

            return stream.ToArray();
        }

        /// <summary>假 dll：只要前两个字节是 MZ（校验只看 PE 头）。</summary>
        private static byte[] DllBytes(string version)
        {
            var body = new byte[4096];
            body[0] = (byte)'M';
            body[1] = (byte)'Z';
            var tag = Encoding.ASCII.GetBytes("PlayniteVault " + version);
            Array.Copy(tag, 0, body, 64, Math.Min(tag.Length, body.Length - 64));
            return body;
        }

        /// <summary>故意做坏的包，用来验证校验能把住门。</summary>
        public static byte[] BuildZip(string version, string dllEntryName, bool includeDll,
            bool includeYaml)
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                if (includeDll)
                {
                    Add(zip, dllEntryName, DllBytes(version));
                }
                if (includeYaml)
                {
                    Add(zip, PluginFolder + "/extension.yaml",
                        Encoding.UTF8.GetBytes("Version: " + version + "\n"));
                }
            }
            return stream.ToArray();
        }

        /// <summary>用指定的 dll 字节造包（用来测「不是 PE 文件」那条校验）。</summary>
        public static byte[] BuildZipWithRawDll(string version, byte[] dllBytes)
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                Add(zip, PluginFolder + "/PlayniteVault.dll", dllBytes);
                Add(zip, PluginFolder + "/extension.yaml",
                    Encoding.UTF8.GetBytes("Version: " + version + "\n"));
            }
            return stream.ToArray();
        }

        private static void Add(ZipArchive zip, string name, byte[] content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using (var target = entry.Open())
            {
                target.Write(content, 0, content.Length);
            }
        }

        public static string WriteFile(string path, byte[] content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllBytes(path, content);
            return path;
        }
    }
}
