using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 进城明文帧解析器(空壳):输入解码后的帧序列,输出 <see cref="TownEntrySnapshot"/>。
    /// 约定:短包、错位、未知帧号一律跳过,不抛异常;不碰 561 号信封。
    /// </summary>
    public static class TownEntryParser
    {
        /// <summary>解析一段进城回程帧;<paramref name="frames"/> 为空或全部不认识时返回 <see cref="TownEntrySnapshot.Empty"/>。</summary>
        public static TownEntrySnapshot Parse(IReadOnlyList<DecodedFrame> frames)
        {
            return TownEntrySnapshot.Empty;
        }
    }
}
