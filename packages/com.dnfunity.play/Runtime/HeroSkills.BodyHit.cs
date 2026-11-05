using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    // 身体命中一族(状态 0x3c、0x3e、0x3f)的放法里属于"技能簿"一侧的几条规则:段伤害倍率、按键接段、段尾保持。
    // 数据在 SkillRecipe.BodyHit,纯函数在 SkillBodyHitRules;位移、重武装这些要看每一步主角位置与时间的在 RoomCombatSession。
    public sealed partial class HeroSkills
    {
        /// <summary>
        /// 一段的伤害百分比:配方给这一段标了倍率(<see cref="SkillBodyHitSpec.Scales"/>)且这一段有百分比列时,在被动威力放大之后再乘 [static data] 里那个数 / 100;其余原样。
        /// (客户端是先把倍率换算、再乘列值;被动放大作用在倍率还是列值上没读到,数值上等价,只差取整那一步,最多差一个百分点,推断。)
        /// </summary>
        private static int StageRate(SkillRecipe recipe, int stage, SkillDef def, SkillStageSpec spec, int rate)
        {
            SkillStageScale scale = recipe.BodyHit?.ScaleOf(stage);
            if (scale == null || def == null || spec.DamageRateColumn < 0)
            {
                return rate;
            }
            return SkillBodyHitRules.ScaleRate(rate, def.GetStatic(scale.PercentStaticIndex, false));
        }

        /// <summary>
        /// 按键接段(<see cref="SkillBodyHitSpec.Chains"/>):主角正在放配方技能的主体阶段,这一步按下的键里有当前段的接段规则认的,动画也播到了规则的帧,
        /// 就换成规则的目标段;目标段在更后面时一段一段连着换(渲染层的换段只会加一),最终停在目标段上。不扣蓝、不进冷却,目标段要求的技能没学过不接。
        /// <paramref name="pressed"/> 是这一步刚按下的技能键(快捷键与指令里的技能键),<paramref name="attackPressed"/> 是攻击键刚按下。没接返回 null。
        /// </summary>
        public HeroSkillSlot TryChainBody(SkillKeys pressed, bool attackPressed, TownHero hero, int heldX = 0, int heldY = 0)
        {
            if (hero == null || hero.Motion != HeroMotion.Skill || hero.SkillWindingUp)
            {
                return null;
            }
            HeroSkillSlot slot = SlotOfSkill(hero.SkillId);
            SkillBodyHitSpec body = slot?.Recipe?.BodyHit;
            if (body == null || body.Chains.Count == 0)
            {
                return null;
            }
            SkillChainKeys keys = SkillChainKeys.None;
            if (slot.Key != SkillKeys.None && (pressed & slot.Key) != SkillKeys.None)
            {
                keys |= SkillChainKeys.Own;
            }
            if ((pressed & SkillKeys.CommandSkill) != SkillKeys.None)
            {
                keys |= SkillChainKeys.SkillKey;
            }
            if (attackPressed)
            {
                keys |= SkillChainKeys.AttackKey;
            }
            int from = hero.SkillStage;
            int to = SkillBodyHitRules.ChainTarget(body, from, hero.CurrentFrame, keys);
            if (to < 0)
            {
                return null;
            }
            int need = slot.Recipe.Stages[to].RequiresSkill;
            if (need != 0 && (SkillLevelOf == null || SkillLevelOf(need) <= 0))
            {
                return null;
            }
            for (int stage = from + 1; stage <= to; stage++)
            {
                if (!BeginStage(slot, stage, hero, heldX, heldY, true, MultiHitWindows(slot)))
                {
                    return stage == from + 1 ? null : slot;
                }
            }
            return slot;
        }

        /// <summary>
        /// 段尾保持(<see cref="SkillBodyHitSpec.Holds"/>):这一段的动画已经播完、本来要结束(或自动接下一段)的那一刻问到这里,
        /// 状态计时器还没到保持的毫秒数就返回 true,技能继续占着主角;到了返回 false。保持的基准是整段动画的墙钟时长,所以带结束帧的段不要配保持。
        /// </summary>
        public bool Holding(TownHero hero)
        {
            if (hero == null || hero.Motion != HeroMotion.Skill || hero.SkillWindingUp)
            {
                return false;
            }
            HeroSkillSlot slot = SlotOfSkill(hero.SkillId);
            SkillStageHold hold = slot?.Recipe?.BodyHit?.HoldOf(hero.SkillStage);
            if (hold == null || slot.Def == null)
            {
                return false;
            }
            double animationMs = hero.EtcMotionDurationMs(slot.Recipe.Stages[hero.SkillStage].MotionIndex);
            long until = SkillBodyHitRules.HoldUntilMs(hold.Kind, animationMs, slot.Def.GetStatic(hold.StaticIndex, false));
            return hero.MotionMs < until;
        }
    }
}
