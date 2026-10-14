using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 一个 .chr 角色定义里与脚本相关的表。脚本里的 CUSTOM_ANI_* 是 [etc motion] 的下标,
    /// CUSTOM_ATTACK_INFO_* 是 [etc attack info] 的下标(证据 E-036:男魔法师头文件里 97 个动画常量、22 个攻击信息常量全部落在表内,
    /// 其中 86 与 17 个常量名与表项文件名一致,其余的名字只是写法不同)。表项路径相对 .chr 所在的目录。
    /// </summary>
    public sealed class CharacterDef : ObjectDef
    {
        public int Job { get; }

        /// <summary>[job] 的取值,如 "[at mage]"。</summary>
        public string JobTag { get; }

        public string[] AttackMotions { get; }
        public string[] AttackInfos { get; }

        public CharacterDef(int job, string path, ScriptLine[] lines)
            : base(path, lines)
        {
            Job = job;
            JobTag = Tags.FirstString("[job]");
            AttackMotions = Tags.Strings("[attack motion]");
            AttackInfos = Tags.Strings("[attack info]");
        }

        public string AttackMotionPath(int index)
        {
            return (uint)index < (uint)AttackMotions.Length ? PvfPath.ResolveRelativeTo(Path, AttackMotions[index]) : null;
        }

        public string AttackInfoPath(int index)
        {
            return (uint)index < (uint)AttackInfos.Length ? PvfPath.ResolveRelativeTo(Path, AttackInfos[index]) : null;
        }
    }
}
