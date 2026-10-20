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

        /// <summary>[weapon hit info]:每种武器一项,下标是装备脚本的 [sub type](证据 E-077)。没有这个标签是空数组。</summary>
        public WeaponHitInfo[] WeaponHitInfos { get; }

        /// <summary>[weapon wav] 块:每种武器一块,下标同 <see cref="WeaponHitInfos"/>(证据 E-089)。没有这个标签是空数组。</summary>
        public WeaponSound[] WeaponSounds { get; }

        public CharacterDef(int job, string path, ScriptLine[] lines)
            : base(path, lines)
        {
            Job = job;
            JobTag = Tags.FirstString("[job]");
            AttackMotions = Tags.Strings("[attack motion]");
            AttackInfos = Tags.Strings("[attack info]");
            WeaponHitInfos = WeaponHitInfo.ParseAll(Tags);
            WeaponSounds = WeaponSound.ParseAll(Tags);
        }

        /// <summary>某种武器([sub type] = <paramref name="subType"/>)的挥动与命中声音;下标越界或那一块是空的返回 false。</summary>
        public bool TryGetWeaponSound(int subType, out WeaponSound sound)
        {
            if ((uint)subType < (uint)WeaponSounds.Length && !WeaponSounds[subType].IsEmpty)
            {
                sound = WeaponSounds[subType];
                return true;
            }
            sound = default;
            return false;
        }

        /// <summary>某种武器([sub type] = <paramref name="subType"/>)的命中默认值;下标越界返回 false。</summary>
        public bool TryGetWeaponHitInfo(int subType, out WeaponHitInfo info)
        {
            if ((uint)subType < (uint)WeaponHitInfos.Length)
            {
                info = WeaponHitInfos[subType];
                return true;
            }
            info = default;
            return false;
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
