using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    public sealed partial class HeroSkillSlot
    {
        /// <summary>投掷类技能的放法(客户端状态 0x0d 一族);不是投掷类技能为 null。这类技能没有配方,身体不打人,伤害全在阶段 0 放出的对象上。</summary>
        public ThrowRecipe Throw { get; internal set; }

        /// <summary>
        /// 两段姿势动画的 PVF 路径(阶段 0、阶段 1),取角色脚本的两个 <c>[throw motion N-k]</c> 标签;读不到标签时为 null,这时放不出来。
        /// </summary>
        public IReadOnlyList<string> ThrowPoses { get; internal set; }

        /// <summary>这个技能在阶段 0 里有没有霸体:[static data] 里霸体开关那一项非 0;不是投掷类技能或没有开关项为 false。</summary>
        public bool ThrowArmor => Throw != null && Throw.ArmorStaticIndex >= 0 && Def != null && Def.GetStatic(Throw.ArmorStaticIndex, false) != 0;

        /// <summary>
        /// 这一次施放的阶段机(<see cref="ThrowRun"/>):两个阶段的时长取 [static data] 的两个下标,不是投掷类技能返回 null。
        /// 施法速度固定取基准(本仓库没有这个属性)。
        /// </summary>
        public ThrowRun NewThrowRun()
        {
            if (Throw == null || Def == null)
            {
                return null;
            }
            return new ThrowRun(Def.GetStatic(Throw.ChargeStaticIndex, false), Def.GetStatic(Throw.ShootStaticIndex, false), ThrowArmor);
        }
    }

    public sealed partial class HeroSkills
    {
        /// <summary>
        /// 开始放投掷类技能:主角原地摆成阶段 0 的姿势(动画从头播)。要腾得出手(<see cref="TownHero.CanBeginSkill"/>)、两段姿势动画的路径都读到、阶段 0 的动画读得出;
        /// 否则返回 false,主角不动。放出对象与开霸体由房间会话接着做,后续每一步的姿势也由房间会话按阶段摆。
        /// </summary>
        private static bool BeginThrow(HeroSkillSlot slot, TownHero hero)
        {
            if (slot.ThrowPoses == null || slot.ThrowPoses.Count < 2 || !hero.CanBeginSkill)
            {
                return false;
            }
            hero.PlaceSkillMotion(hero.X, hero.Y, hero.FacingLeft, slot.SkillId, slot.ThrowPoses[0], 0);
            return hero.Motion == HeroMotion.Skill && hero.SkillId == slot.SkillId;      // 动画读不到时 PlaceSkillMotion 摆成站立
        }

        /// <summary>
        /// 给投掷类技能建槽:每个对象的攻击信息取对象 .obj 的 [attack info]、伤害百分比与固定值都取 [level info] 当前等级的同一列,再各乘被动威力倍率(E-181,与配方技能同一套),
        /// 两段姿势动画按角色脚本的标签解析成 PVF 路径(两个标签有一个读不到,<see cref="HeroSkillSlot.ThrowPoses"/> 就是 null)。
        /// </summary>
        private static void BuildThrowSlot(HeroSkillSlot slot, GameData data, CharacterDef chr, SkillDef def, ThrowRecipe recipe, Func<int, int> skillLevelOf,
            Func<int, SkillDef> resolveSkill, IReadOnlyList<SkillPassivePowerRate.Link> registeredLinks)
        {
            slot.Throw = recipe;
            IReadOnlyList<SkillPassivePowerRate.Link> powerLinks = SkillPassivePowerRate.RegisteredLinksForActive(registeredLinks, def.Id);
            Func<int, SkillDef> resolve = resolveSkill ?? (id => data.GetSkill(chr.Job, id));
            Func<int, int> levelOf = skillLevelOf ?? (_ => 0);
            float powerRate = SkillPassivePowerRate.Accumulate(1f, def.Id, 0, levelOf, resolve, powerLinks);
            var strikes = new List<HeroStrikeInfo>();
            foreach (SkillObjectSpec o in recipe.Objects)
            {
                Dnf.Game.PassiveObjectDef objectDef = data.GetPassiveObject(o.ObjectId);
                strikes.Add(HeroStrikeInfo.Read(data, objectDef?.AttackInfoPath)
                    .WithSkillDamage(ScaleLevel(def, slot.Level, o.DamageRateColumn, powerRate), ScaleLevel(def, slot.Level, o.DamageFixedColumn, powerRate)));
            }
            slot.ObjectStrikes = strikes;
            string charge = chr.MotionPath(recipe.ChargePoseTag);
            string shoot = chr.MotionPath(recipe.ShootPoseTag);
            slot.ThrowPoses = charge != null && shoot != null ? new[] { charge, shoot } : null;
        }
    }
}
