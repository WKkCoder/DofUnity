using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 探针 <c>client-version</c> 子命令的全部逻辑:读用户自己的原版客户端程序文件自带的文件版本,折成登录请求里的客户端版本号并打印到终端。
    /// 只读、不联网、不读任何本机配置。命令行解析、读文件、折算与输出行都在这里(有单元测试),探针的外壳只把控制台接上来。
    /// <para>
    /// 用法:<c>dnf-netprobe client-version &lt;客户端程序文件&gt;</c>。成功时打两行:<c>文件版本 A.B.C.D</c>(应与选服务器页右下角版本标签里的四段数一致,可借此核对读对了文件)
    /// 与 <c>客户端版本号 N</c>(把 N 写进本机配置 <see cref="LocalDevEntrySettings.KeyClientVersion"/> 即可,台账 E-451、E-453);四段数大到折算超出 32 位时再加一行说明。
    /// </para>
    /// <para>
    /// 输出只含这两个数与固定的说明文字,<b>不回显文件路径</b>、不带异常文字、不带文件里的任何别的内容。取值是用户自己客户端的版本,只打到终端,不要贴进仓库、文档或日志。
    /// 退出码:0 成功;64 用法错误(参数个数不对,或参数像选项,不读文件);65 文件读到了但版本读不出来(类别见 <see cref="ClientVersionFile.Describe"/>);
    /// 66 文件读不到(不存在、没权限、超过 <see cref="MaxFileBytes"/> 等)。
    /// </para>
    /// </summary>
    public static class ClientVersionTool
    {
        /// <summary>退出码:成功。</summary>
        public const int ExitOk = 0;

        /// <summary>退出码:用法错误。</summary>
        public const int ExitUsage = 64;

        /// <summary>退出码:文件读到了但读不出版本。</summary>
        public const int ExitBadImage = 65;

        /// <summary>退出码:文件读不到。</summary>
        public const int ExitUnreadable = 66;

        /// <summary>读进内存的文件大小上限(客户端程序文件远小于它;超过就当读不了,不为一个畸形的大文件分配内存)。</summary>
        public const long MaxFileBytes = 256L * 1024 * 1024;

        /// <summary>用法提示行。</summary>
        public const string UsageLine = "用法: dnf-netprobe client-version <客户端程序文件>";

        /// <summary>
        /// 跑一次。<paramref name="readFile"/> 把路径读成整个文件的字节(读不到时抛 <see cref="IOException"/> 等文件系统异常);
        /// <paramref name="write"/> 是逐行输出。测试里两者换成内存实现。
        /// </summary>
        public static int Run(IReadOnlyList<string> args, Func<string, byte[]> readFile, Action<string> write)
        {
            if (args == null || args.Count != 1 || string.IsNullOrEmpty(args[0]) || args[0].StartsWith("-", StringComparison.Ordinal))
            {
                write(UsageLine);
                return ExitUsage;
            }

            byte[] image;
            try
            {
                image = readFile(args[0]);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                image = null;
            }
            if (image == null)
            {
                write("读不到指定的文件");
                return ExitUnreadable;
            }

            if (!ClientVersionFile.TryReadFileVersion(image, out ClientFileVersion version, out ClientVersionFileProblem problem))
            {
                write(ClientVersionFile.Describe(problem));
                return ExitBadImage;
            }

            write("文件版本 " + version);
            write("客户端版本号 " + version.Fold().ToString(CultureInfo.InvariantCulture));
            if (!version.FoldsExactly())
            {
                write("注意:四段数折算后超出 32 位,上面的取值已按客户端的 32 位算法回绕");
            }
            return ExitOk;
        }

        /// <summary>产品用的入口:从真文件系统读(上限 <see cref="MaxFileBytes"/>)。</summary>
        public static int RunFile(IReadOnlyList<string> args, Action<string> write)
        {
            return Run(args, path => ReadBounded(path, MaxFileBytes), write);
        }

        /// <summary>读整个文件;大于 <paramref name="maxBytes"/> 时在分配之前就抛 <see cref="IOException"/>。</summary>
        public static byte[] ReadBounded(string path, long maxBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > maxBytes)
                {
                    throw new IOException("file too large");
                }
                var buffer = new byte[(int)stream.Length];
                int total = 0;
                while (total < buffer.Length)
                {
                    int read = stream.Read(buffer, total, buffer.Length - total);
                    if (read <= 0)
                    {
                        throw new IOException("file shrank while reading");
                    }
                    total += read;
                }
                return buffer;
            }
        }
    }
}
