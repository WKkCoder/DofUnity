using System;
using System.Collections.Generic;
using System.Text;

namespace Dnf.Data
{
    /// <summary>
    /// PVF 路径的规范化。客户端查找文件时先把路径转小写、把 '\' 换成 '/'、折叠 '.' 与 '..'(证据 E-013,
    /// 是从 DNF.exe 的查找代码读出来的,没有逐条验证);脚本里的相对路径(如 .npc 里的 "Animation/PostBox.ani")
    /// 相对哪个目录,这里按"相对引用它的脚本所在目录"处理,这是从 npc/*.npc → npc/animation/*.ani 的实际数据归纳的,E-024。
    /// </summary>
    public static class PvfPath
    {
        /// <summary>转小写、'\' 换成 '/'、折叠 '.' 与 '..'、去掉首部的 '/'。折叠到根以上的 '..' 被丢弃。</summary>
        public static string Normalize(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            string p = path.Replace('\\', '/').ToLowerInvariant();
            string[] parts = p.Split('/');
            var stack = new List<string>(parts.Length);
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }
                if (part == "..")
                {
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    continue;
                }
                stack.Add(part);
            }
            var sb = new StringBuilder(p.Length);
            for (int i = 0; i < stack.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('/');
                }
                sb.Append(stack[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把脚本 <paramref name="scriptPath"/> 里写的相对路径 <paramref name="relative"/> 解析成 PVF 里的规范路径:
        /// 相对脚本所在的目录。<paramref name="relative"/> 以 '/' 开头时视为从 PVF 根开始。
        /// </summary>
        public static string ResolveRelativeTo(string scriptPath, string relative)
        {
            if (relative == null)
            {
                throw new ArgumentNullException(nameof(relative));
            }
            string rel = relative.Replace('\\', '/');
            if (rel.StartsWith("/", StringComparison.Ordinal))
            {
                return Normalize(rel);
            }
            string script = (scriptPath ?? string.Empty).Replace('\\', '/');
            int slash = script.LastIndexOf('/');
            string dir = slash >= 0 ? script.Substring(0, slash + 1) : string.Empty;
            return Normalize(dir + rel);
        }
    }
}
