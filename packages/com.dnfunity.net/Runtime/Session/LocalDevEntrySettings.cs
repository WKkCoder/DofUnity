using System;
using System.Collections.Generic;
using System.IO;
using Dnf.Net.Gateway;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 仅供本地开发的联网入参读取:从仓库根的 local.env(DNF_DIR)、数据目录里的网关配置(server_url 与 aes_key)、
    /// 本地机密文件(local.secrets.env 的账号/口令/鼠标口令密文)组出 <see cref="OnlineEntrySettings"/>。
    /// 这些文件都不入库,值只在进程内使用;正式分发不要用这条路径(凭据应来自登录界面输入)。
    /// 失败时 <paramref name="problem"/> 只说缺了哪一项,不带任何值。
    /// </summary>
    public static class LocalDevEntrySettings
    {
        /// <summary>实测能通过的数据版本默认值(要与服务端参数脚本一致,E-328)。</summary>
        public const uint DefaultDataVersion = 20080418;

        /// <summary>从 <paramref name="repoRoot"/> 读本地开发入参;缺项返回 false 并说明是哪一项。</summary>
        public static bool TryLoad(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            settings = null;
            problem = null;
            if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
            {
                problem = "仓库根目录不存在";
                return false;
            }

            Dictionary<string, string> localEnv;
            try
            {
                localEnv = ParseEnvFile(Path.Combine(repoRoot, "local.env"));
            }
            catch (IOException)
            {
                problem = "读不到 local.env";
                return false;
            }
            if (!localEnv.TryGetValue("DNF_DIR", out string dnfDir) || string.IsNullOrEmpty(dnfDir))
            {
                problem = "local.env 里没有 DNF_DIR";
                return false;
            }

            string gatewayUrl = null;
            string aesKeyHex = null;
            try
            {
                foreach (string raw in File.ReadAllLines(Path.Combine(dnfDir, "Config.toml")))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("server_url"))
                    {
                        gatewayUrl = TomlStringValue(line);
                    }
                    else if (line.StartsWith("aes_key"))
                    {
                        aesKeyHex = TomlStringValue(line);
                    }
                }
            }
            catch (IOException)
            {
                problem = "读不到数据目录里的网关配置";
                return false;
            }
            if (gatewayUrl == null || aesKeyHex == null)
            {
                problem = "网关配置里缺 server_url 或 aes_key";
                return false;
            }
            if (!GatewayOptions.TryCreate(gatewayUrl, aesKeyHex, TimeSpan.FromSeconds(15), out GatewayOptions gateway, out _))
            {
                problem = "网关配置形状不对";
                return false;
            }

            Dictionary<string, string> secrets;
            try
            {
                secrets = ParseEnvFile(Path.Combine(repoRoot, "local.secrets.env"));
            }
            catch (IOException)
            {
                problem = "读不到 local.secrets.env";
                return false;
            }
            if (!secrets.TryGetValue("DNF_ACCOUNT", out string account) || string.IsNullOrEmpty(account)
                || !secrets.TryGetValue("DNF_PASSWORD", out string password) || string.IsNullOrEmpty(password))
            {
                problem = "本地机密文件里没有账号或口令";
                return false;
            }

            byte[] mouseCipher = null;
            if (secrets.TryGetValue("DNF_MOUSE_CIPHER_HEX", out string mouseHex) && mouseHex != null && mouseHex.Length == 2 * LoginRequest.MouseCipherBytes)
            {
                mouseCipher = new byte[LoginRequest.MouseCipherBytes];
                for (int i = 0; i < mouseCipher.Length; i++)
                {
                    int hi = HexValue(mouseHex[2 * i]);
                    int lo = HexValue(mouseHex[2 * i + 1]);
                    if (hi < 0 || lo < 0)
                    {
                        mouseCipher = null;
                        break;
                    }
                    mouseCipher[i] = (byte)((hi << 4) | lo);
                }
            }

            settings = new OnlineEntrySettings
            {
                Gateway = gateway,
                Account = account,
                Password = password,
                MouseCipher = mouseCipher,
                DataVersion = DefaultDataVersion,
            };
            return true;
        }

        private static Dictionary<string, string> ParseEnvFile(string path)
        {
            var result = new Dictionary<string, string>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("export "))
                {
                    line = line.Substring(7);
                }
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                string value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
                {
                    value = value.Substring(1, value.Length - 2);
                }
                result[line.Substring(0, eq).Trim()] = value;
            }
            return result;
        }

        private static string TomlStringValue(string line)
        {
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                return null;
            }
            string value = line.Substring(eq + 1).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2);
            }
            return value;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }
            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }
            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }
            return -1;
        }
    }
}
