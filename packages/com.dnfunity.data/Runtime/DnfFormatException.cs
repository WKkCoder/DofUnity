using System;

namespace Dnf.Data
{
    /// <summary>数据不符合已知格式时抛出。消息里带上足够定位问题的数字。</summary>
    public sealed class DnfFormatException : Exception
    {
        public DnfFormatException(string message) : base(message) { }

        public DnfFormatException(string message, Exception inner) : base(message, inner) { }
    }
}
