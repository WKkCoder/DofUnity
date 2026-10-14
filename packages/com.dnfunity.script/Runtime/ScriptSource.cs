using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Script
{
    /// <summary>脚本文件的来源。路径是规范化后的 PVF 路径(小写、'/' 分隔、相对 PVF 根,例如 "sqr/loadstate.nut")。</summary>
    public interface IScriptSource
    {
        bool TryRead(string path, out byte[] data);
    }

    /// <summary>从用户自备的 Script.pvf 读脚本。</summary>
    public sealed class PvfScriptSource : IScriptSource
    {
        private readonly PvfArchive _pvf;

        public PvfScriptSource(PvfArchive pvf)
        {
            _pvf = pvf ?? throw new ArgumentNullException(nameof(pvf));
        }

        public bool TryRead(string path, out byte[] data)
        {
            if (_pvf.TryGetEntry(path, out PvfEntry entry))
            {
                data = _pvf.Read(entry);
                return true;
            }
            data = null;
            return false;
        }
    }

    /// <summary>内存里的脚本集合,测试用(里面的脚本是测试自己写的,不是原版内容)。</summary>
    public sealed class MemoryScriptSource : IScriptSource
    {
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public MemoryScriptSource Add(string path, string text)
        {
            _files[PvfPath.Normalize(path)] = ScriptText.ToBytes(text);
            return this;
        }

        public bool TryRead(string path, out byte[] data)
        {
            return _files.TryGetValue(path, out data);
        }
    }
}
