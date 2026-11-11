using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

// 证据:r15-growth(进副本门槛的服务端读法见 Dnf.Game 的 DungeonFatigueRules 文件头:DisPatcher_SelectDungeon::process 0x081c8102、CUser::CheckFatigue 0x08656500)。
namespace Dnf.Play
{
    /// <summary>进副本门槛的结论。</summary>
    public enum EntryVerdict
    {
        /// <summary>可以进。</summary>
        Allowed = 0,

        /// <summary>疲劳不足(服务端错误码 0x16):已耗不小于上限,而且这座副本不是免疲劳的。</summary>
        FatigueShort = 1,
    }

    /// <summary>
    /// 进副本的疲劳门槛的造法(与 <see cref="DifficultyGate"/> 同一思路):把主角的疲劳(<see cref="HeroProgress.Fatigue"/>)和副本定义里的免疲劳标志接成流程要的
    /// <c>副本路径 → 结论</c> 函数,宿主接在 <see cref="GameFlow.EntryGate"/> 上。流程在选择界面确认进入与结算"再次挑战"时各问一次;直接调 <see cref="GameFlow.StartDungeon"/> 不问。
    /// </summary>
    public static class FatigueGate
    {
        /// <summary>
        /// 造门槛函数。<paramref name="progress"/> 每次现取(取到 null 时一律放行);<paramref name="assets"/> 用来读副本定义里的免疲劳标志
        /// (为 null 或读不到副本时按"要查疲劳"处理)。每个副本的规则只读一次。
        /// </summary>
        public static Func<string, EntryVerdict> For(Func<HeroProgress> progress, DnfAssets assets)
        {
            return For(progress, assets == null ? (Func<string, ScriptLine[]>)null : assets.ReadScript);
        }

        /// <summary>同上,副本定义由 <paramref name="readScript"/> 按路径给(读不到返回 null;为 null 当一律读不到)。</summary>
        public static Func<string, EntryVerdict> For(Func<HeroProgress> progress, Func<string, ScriptLine[]> readScript)
        {
            var rules = new Dictionary<string, DungeonFatigueRules>(StringComparer.OrdinalIgnoreCase);
            return path =>
            {
                HeroProgress p = progress?.Invoke();
                if (p == null || string.IsNullOrEmpty(path))
                {
                    return EntryVerdict.Allowed;
                }
                if (!rules.TryGetValue(path, out DungeonFatigueRules rule))
                {
                    rule = readScript == null ? DungeonFatigueRules.Default : DungeonFatigueRules.Parse(readScript(path));
                    rules[path] = rule;
                }
                return Check(p, rule);
            };
        }

        /// <summary>
        /// 用主角的疲劳与副本规则判一次:先刷新(过了恢复点就先恢复),免疲劳副本放行,其余看"已耗小于上限"(剩 1 点放行、剩 0 点挡下,与这趟要扣几点无关)。
        /// </summary>
        public static EntryVerdict Check(HeroProgress progress, DungeonFatigueRules rule)
        {
            if (progress == null)
            {
                throw new ArgumentNullException(nameof(progress));
            }
            progress.RefreshFatigue();
            if ((rule ?? DungeonFatigueRules.Default).GateExempt)
            {
                return EntryVerdict.Allowed;
            }
            return progress.Fatigue.CanEnter ? EntryVerdict.Allowed : EntryVerdict.FatigueShort;
        }
    }
}
