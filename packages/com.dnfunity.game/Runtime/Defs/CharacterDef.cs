using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 一个 .chr 角色定义里与脚本相关的表。脚本里的 CUSTOM_ANI_* 是 [etc motion] 的下标,
    /// CUSTOM_ATTACK_INFO_* 是 [etc attack info] 的下标(证据 E-036:男魔法师头文件里 97 个动画常量、22 个攻击信息常量全部落在表内,
    /// 其中 86 与 17 个常量名与表项文件名一致,其余的名字只是写法不同)。表项路径相对 .chr 所在的目录。
    /// </summary>
    public sealed class CharacterDef
    {
        public int Job { get; }

        /// <summary>规范化的 PVF 路径,例如 character/mage/atmage.chr。</summary>
        public string Path { get; }

        public TagTable Tags { get; }

        /// <summary>[job] 的取值,如 "[at mage]"。</summary>
        public string JobTag { get; }

        public string[] EtcMotions { get; }
        public string[] EtcAttackInfos { get; }
        public string[] AttackMotions { get; }
        public string[] AttackInfos { get; }

        public CharacterDef(int job, string path, ScriptLine[] lines)
        {
            Job = job;
            Path = path;
            Tags = new TagTable(lines);
            JobTag = Tags.FirstString("[job]");
            EtcMotions = Tags.Strings("[etc motion]");
            EtcAttackInfos = Tags.Strings("[etc attack info]");
            AttackMotions = Tags.Strings("[attack motion]");
            AttackInfos = Tags.Strings("[attack info]");
        }

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

        public string AttackMotionPath(int index)
        {
            return (uint)index < (uint)AttackMotions.Length ? PvfPath.ResolveRelativeTo(Path, AttackMotions[index]) : null;
        }

        public string AttackInfoPath(int index)
        {
            return (uint)index < (uint)AttackInfos.Length ? PvfPath.ResolveRelativeTo(Path, AttackInfos[index]) : null;
        }

        /// <summary>按 .chr 里的单项动画标签(如 "[waiting motion]")取路径;没有则返回 null。</summary>
        public string MotionPath(string tag)
        {
            string s = Tags.FirstString(tag);
            return s == null ? null : PvfPath.ResolveRelativeTo(Path, s);
        }
    }
}
