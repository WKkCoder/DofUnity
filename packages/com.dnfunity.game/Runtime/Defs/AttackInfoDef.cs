using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 一个 .atk 攻击信息的静态部分:攻击类型、元素、受击反应等标签。基础倍率、硬直时间等由脚本在运行时设置
    /// (sq_SetCurrentAttackBonusRate 一类),不在这个文件里。标签的含义这里**不做解释**,只提供按名字取值。
    /// </summary>
    public sealed class AttackInfoDef
    {
        /// <summary>规范化的 PVF 路径。</summary>
        public string Path { get; }

        public TagTable Tags { get; }

        public AttackInfoDef(string path, ScriptLine[] lines)
        {
            Path = path;
            Tags = new TagTable(lines);
        }
    }
}
