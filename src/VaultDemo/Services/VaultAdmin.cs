using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using VaultDemo.Net;

namespace VaultDemo.Services
{
    /// <summary>
    /// 仓库管理口令 —— 删除应用这类破坏性操作之前必须先过这一关。
    ///
    /// <para><b>先说清楚它是什么</b>：这是一个<b>防误触闸门</b>，不是访问控制。
    /// 派生值就明文躺在 NAS 的 <c>vault-admin.json</c> 里，任何能读仓库的人都能离线爆破；
    /// 真正的读写权限由 WebDAV 账号决定。它挡的是「手滑点到删除」这类事故，
    /// 以及多个人共用仓库时确认「这个人确实是有意要删」。</para>
    ///
    /// <para><b>存储格式</b>（与 Python 端 <c>tools/vault_unpacker/admin.py</c> 必须一致）：</para>
    /// <code>
    /// {
    ///   "version": 1,
    ///   "algorithm": "pbkdf2",
    ///   "hmac": "sha1",
    ///   "iterations": 200000,
    ///   "salt": "&lt;base64 16 字节&gt;",
    ///   "hash": "&lt;base64 32 字节&gt;",
    ///   "updatedAt": "2026-09-19T12:00:00+08:00"
    /// }
    /// </code>
    ///
    /// <para><b>为什么 HMAC-SHA1 而不是 SHA256</b>：目标框架是 net462，而 .NET Framework 的
    /// <see cref="Rfc2898DeriveBytes"/> 只支持 SHA-1（带 <c>HashAlgorithmName</c> 的重载要
    /// 4.7.2+，改目标框架会牵动整个插件）。200k 次迭代 + 16 字节随机盐，对「防误触」
    /// 这个定位完全够用。口令走的是 UTF-8 字节，Python 端用
    /// <c>hashlib.pbkdf2_hmac('sha1', pw.encode('utf-8'), salt, iterations, dklen=32)</c> 即可对齐。</para>
    /// </summary>
    public class VaultAdmin
    {
        /// <summary>仓库根目录下的文件名。</summary>
        public const string FileName = "vault-admin.json";

        /// <summary>迭代次数。改大能拖慢爆破，改小无意义 —— 200k 在 i5 上约 60ms。</summary>
        public const int DefaultIterations = 200000;

        private const int SaltBytes = 16;
        private const int KeyBytes = 32;

        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("algorithm")]
        public string Algorithm { get; set; } = "pbkdf2";

        [JsonProperty("hmac")]
        public string Hmac { get; set; } = "sha1";

        [JsonProperty("iterations")]
        public int Iterations { get; set; } = DefaultIterations;

        [JsonProperty("salt")]
        public string Salt { get; set; } = string.Empty;

        [JsonProperty("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonProperty("updatedAt")]
        public string UpdatedAt { get; set; } = string.Empty;

        /// <summary>口令是否设置过。没设置时不允许执行删除，只能先去设置。</summary>
        [JsonIgnore]
        public bool IsSet
        {
            get { return !string.IsNullOrWhiteSpace(Salt) && !string.IsNullOrWhiteSpace(Hash); }
        }

        /// <summary>用新口令造一份记录（随机盐 + 派生值，绝不保存明文）。</summary>
        public static VaultAdmin Create(string password)
        {
            var salt = new byte[SaltBytes];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            var admin = new VaultAdmin
            {
                Version = 1,
                Algorithm = "pbkdf2",
                Hmac = "sha1",
                Iterations = DefaultIterations,
                Salt = Convert.ToBase64String(salt)
            };

            admin.Hash = Convert.ToBase64String(Derive(password, salt, admin.Iterations));
            return admin;
        }

        /// <summary>校验口令。用固定时间比较，避免按字节比较泄漏前缀信息。</summary>
        public bool Verify(string password)
        {
            if (!IsSet)
            {
                return false;
            }

            byte[] salt;
            byte[] expected;
            try
            {
                salt = Convert.FromBase64String(Salt);
                expected = Convert.FromBase64String(Hash);
            }
            catch
            {
                return false;
            }

            if (salt.Length == 0 || expected.Length == 0)
            {
                return false;
            }

            var iterations = Iterations <= 0 ? DefaultIterations : Iterations;
            return FixedTimeEquals(Derive(password, salt, iterations), expected);
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            // 显式走 UTF-8 字节重载：字符串重载在 .NET Framework 各版本的编码语义容易记混，
            // 而 Python 端一定会用 encode('utf-8')，这里必须钉死。
            var bytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
            using (var kdf = new Rfc2898DeriveBytes(bytes, salt, iterations))
            {
                return kdf.GetBytes(KeyBytes);
            }
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            var diff = a.Length ^ b.Length;
            var n = a.Length < b.Length ? a.Length : b.Length;
            for (var i = 0; i < n; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }

        /// <summary>从仓库根目录读记录；不存在返回 null（= 还没设置过）。</summary>
        public static VaultAdmin Load(WebDavClient client)
        {
            if (client == null)
            {
                return null;
            }

            try
            {
                if (!client.Exists(FileName))
                {
                    return null;
                }

                var json = client.DownloadString(FileName);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                return JsonConvert.DeserializeObject<VaultAdmin>(json);
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读取 vault-admin.json 失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>写回仓库根目录。</summary>
        public static void Save(WebDavClient client, VaultAdmin admin)
        {
            if (client == null || admin == null)
            {
                return;
            }

            admin.UpdatedAt = DateTimeOffset.Now.ToString("o");
            client.UploadString(
                JsonConvert.SerializeObject(admin, Formatting.Indented),
                FileName);
        }
    }
}
