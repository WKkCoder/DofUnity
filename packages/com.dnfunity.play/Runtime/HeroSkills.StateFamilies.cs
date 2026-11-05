using Dnf.Render;

namespace Dnf.Play
{
    // 状态 0x21、0x25、0x27 这几个状态族(E-271、E-272)在多段配方之上多出来的放法:逐段的"播完自己接"、按键接段的限制与段首瞬移。
    // 配方上不带这些标记时,这里的判定都等于旧行为(整体自动接续或整体按键接)。
    public sealed partial class HeroSkills
    {
        /// <summary>这一段播完是否自己接下一段(这一段自己标了 <see cref="SkillStageSpec.AutoAdvance"/>);段号越界当没有。</summary>
        public static bool AutoAdvancesFrom(SkillRecipe recipe, int stage)
        {
            return stage >= 0 && stage < recipe.Stages.Count && recipe.Stages[stage].AutoAdvance;
        }

        /// <summary>
        /// 在这一段里再按技能键能不能接下一段:这一段不是"播完自己接"的段、也没被标成不许按键接。段号越界当可以(按旧行为由调用方再判段数)。
        /// </summary>
        public static bool KeyChainsFrom(SkillRecipe recipe, int stage)
        {
            if (stage < 0 || stage >= recipe.Stages.Count)
            {
                return true;
            }
            SkillStageSpec spec = recipe.Stages[stage];
            return !spec.AutoAdvance && spec.KeyChainOut;
        }

        /// <summary>
        /// 一段开头的瞬移(状态 0x21 阶段 1,E-271;<see cref="SkillWarpSpec"/>):距离取 [static data],朝主角的朝向一侧、沿可走区域逐像素探到第一个站不住的点之前,
        /// 横坐标直接设成落点,纵向不变。读不到技能定义时不动。
        /// </summary>
        internal static void ApplyWarp(HeroSkillSlot slot, SkillWarpSpec warp, TownHero hero)
        {
            if (slot.Def == null)
            {
                return;
            }
            slot.RememberWarpOrigin(hero.X, hero.Y, hero.Z);
            int distance = slot.Def.GetStatic(warp.DistanceStaticIndex, false);
            hero.Teleport(SkillWarpSpec.Destination(hero.X, hero.Y, distance, hero.FacingLeft, hero.Walk), hero.Y);
        }
    }

    public sealed partial class HeroSkillSlot
    {
        /// <summary>最近一次段首瞬移发生前主角站的位置有没有记下(<see cref="SkillObjectSpec.FromWarpOrigin"/> 的对象用)。</summary>
        public bool HasWarpOrigin { get; private set; }

        /// <summary>最近一次段首瞬移前主角的世界横坐标。</summary>
        public double WarpOriginX { get; private set; }

        /// <summary>最近一次段首瞬移前主角的世界纵深坐标。</summary>
        public double WarpOriginY { get; private set; }

        /// <summary>最近一次段首瞬移前主角的离地高度。</summary>
        public double WarpOriginZ { get; private set; }

        internal void RememberWarpOrigin(double x, double y, double z)
        {
            HasWarpOrigin = true;
            WarpOriginX = x;
            WarpOriginY = y;
            WarpOriginZ = z;
        }
    }
}
