using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 一个 .atk 攻击信息的静态部分。<see cref="Data"/> 是按原版加载函数的读法读出的类型化字段(证据 E-047);
    /// <see cref="Tags"/> 是同一份文件按标签行拆开的原始视图,给还没有类型化的用途与测试用。
    /// 基础倍率、硬直时间等由脚本在运行时设置(sq_SetCurrentAttackBonusRate 一类),不在这个文件里。
    /// </summary>
    public sealed class AttackInfoDef
    {
        /// <summary>规范化的 PVF 路径。</summary>
        public string Path { get; }

        public TagTable Tags { get; }

        /// <summary>按原版读法读出的字段;文件不是标记流时为 null。</summary>
        public AttackInfoData Data { get; }

        /// <summary>读取时遇到的不规整之处;<see cref="Data"/> 为 null 时也为 null。</summary>
        public AttackInfoLoadReport Report { get; }

        public AttackInfoDef(string path, ScriptLine[] lines, AttackInfoData data = null, AttackInfoLoadReport report = null)
        {
            Path = path;
            Tags = new TagTable(lines);
            Data = data;
            Report = report;
        }
    }
}
