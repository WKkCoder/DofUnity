using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

// 证据:E-125(难度开放的规则见 Dnf.Game 的 DungeonClears 与 UltimateDungeonList)。
namespace Dnf.Play
{
    /// <summary>
    /// 副本选择界面的难度上限的造法(M33):把主角的通关记录(<see cref="HeroProgress.Clears"/>)和"有没有难度 3"清单接成界面要的 <c>副本 → 最高难度</c> 函数。
    /// 返回的函数只用基本类型与 Play 的类型,宿主层(不引用 Dnf.Game)可以直接用。
    /// </summary>
    public static class DifficultyGate
    {
        /// <summary>
        /// 造上限函数。<paramref name="progress"/> 每次现取(主角的进度可能晚于流程造好;取到 null 时每个副本都只有难度 0);
        /// <paramref name="assets"/> 给出 etc/ultimatedungeonlist.etc 与 dungeon/dungeon.lst(副本卡片没有副本编号时按路径查);为 null 时没有难度 3 的副本、也查不到编号。
        /// </summary>
        public static Func<DungeonEntry, int> For(Func<HeroProgress> progress, DnfAssets assets)
        {
            UltimateDungeonList list = assets == null ? UltimateDungeonList.Empty : UltimateDungeonList.Parse(assets.ReadScript(UltimateDungeonList.Path));
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            return entry =>
            {
                HeroProgress p = progress?.Invoke();
                if (p == null || entry == null)
                {
                    return 0;
                }
                int id;
                if (entry.Card != null)
                {
                    id = entry.Card.DungeonId;
                }
                else if (!ids.TryGetValue(entry.Path, out id))
                {
                    id = assets == null ? -1 : DungeonRoomPool.DungeonIdOf(assets, entry.Path);
                    ids[entry.Path] = id;
                }
                return id < 0 ? 0 : p.HighestDifficulty(id, list.Contains(id));
            };
        }
    }
}
