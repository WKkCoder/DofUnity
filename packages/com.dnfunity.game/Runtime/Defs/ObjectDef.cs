using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 带"自定义动画表"与"自定义攻击信息表"的对象定义的共同部分(角色 .chr 与被动对象 .obj)。
    /// 脚本里的 CUSTOM_ANI_* 是 [etc motion] 的下标,CUSTOM_ATTACK_INFO_* 是 [etc attack info] 的下标(证据 E-036);
    /// 被动对象脚本里的 getCustomAnimation(i)、sq_SetCurrentAttackInfo(obj, i) 对 .obj 同理(推断:用法与角色一致)。
    /// 表项路径相对定义文件所在的目录。
    /// </summary>
    public abstract class ObjectDef
    {
        protected ObjectDef(string path, ScriptLine[] lines)
        {
            Path = path;
            Tags = new TagTable(lines);
            EtcMotions = Tags.Strings("[etc motion]");
            EtcAttackInfos = Tags.Strings("[etc attack info]");
        }

        /// <summary>规范化的 PVF 路径。</summary>
        public string Path { get; }

        public TagTable Tags { get; }

        public string[] EtcMotions { get; }

        public string[] EtcAttackInfos { get; }

        /// <summary>CUSTOM_ANI_* 下标对应的动画路径(规范化);下标越界返回 null。</summary>
        public string CustomAnimationPath(int index)
        {
            return (uint)index < (uint)EtcMotions.Length ? PvfPath.ResolveRelativeTo(Path, EtcMotions[index]) : null;
        }

        /// <summary>CUSTOM_ATTACK_INFO_* 下标对应的攻击信息路径(规范化);下标越界返回 null。</summary>
        public string CustomAttackInfoPath(int index)
        {
            return (uint)index < (uint)EtcAttackInfos.Length ? PvfPath.ResolveRelativeTo(Path, EtcAttackInfos[index]) : null;
        }

        /// <summary>按单项动画标签(如 "[waiting motion]"、"[basic motion]")取路径;没有则返回 null。</summary>
        public string MotionPath(string tag)
        {
            string s = Tags.FirstString(tag);
            return string.IsNullOrEmpty(s) ? null : PvfPath.ResolveRelativeTo(Path, s);
        }
    }
}
