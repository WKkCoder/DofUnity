using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>已经实现了放法的技能(客户端的技能编号,技能名按 skill/swordmanskill.lst 里的文件名,证据 E-076)。</summary>
    public static class SkillIds
    {
        /// <summary>鬼斩(HardAttack)。</summary>
        public const int HardAttack = 5;

        /// <summary>上挑(UpperSlash)。</summary>
        public const int UpperSlash = 46;

        /// <summary>后跳(BackStep)。</summary>
        public const int BackStep = 169;
    }

    /// <summary>放技能的结果。</summary>
    public enum SkillCastResult
    {
        /// <summary>放出来了。</summary>
        Cast,

        /// <summary>这个键没有绑技能。</summary>
        NotBound,

        /// <summary>技能还在冷却。</summary>
        Cooldown,

        /// <summary>魔法值不够(证据 E-087)。</summary>
        NoMp,

        /// <summary>主角此刻不能放(正在挥击、腾空或放别的技能,或缺少需要的动作)。</summary>
        Busy,

        /// <summary>技能还没有实现。</summary>
        NotImplemented,
    }

    /// <summary>快捷槽里的一个技能:编号、等级、冷却的满值(取自 .skl)与此刻还剩的冷却。</summary>
    public sealed class HeroSkillSlot
    {
        public SkillKeys Key { get; }

        /// <summary>快捷栏里的位置(0 到 5,对应键 A S D F G H;底栏上从左数第几个槽)。</summary>
        public int Position { get; }

        public int SkillId { get; }
        public int Level { get; }
        public SkillDef Def { get; }

        /// <summary>满冷却时长(毫秒):.skl 的 [cool time] 按等级插值;没有这个标签为 0(例如后跳)。</summary>
        public int CooldownMs { get; }

        /// <summary>.skl 的 [consume MP] 按等级插值;放出来的那一刻从 <see cref="HeroVitals"/> 里扣(证据 E-087)。</summary>
        public int ConsumeMp { get; }

        /// <summary>
        /// 这个技能命中时用的攻击信息与伤害数(鬼斩:取自角色的 [etc attack info] 里同名的 .atk,伤害数取自 .skl 的 [level info] 当前等级);
        /// null 表示沿用放技能时那一段普攻的攻击信息(上挑)。
        /// </summary>
        public HeroStrikeInfo? Strike { get; internal set; }

        /// <summary>
        /// 放出来时在主角位置同时放的特效动画(PVF 路径,按顺序叠画;鬼斩是两层斩击光影)。放的时间点是技能开始那一刻,
        /// 动画自己的第一帧是空帧,等于延后出现(证据 E-076)。
        /// </summary>
        public IReadOnlyList<string> EffectPaths { get; internal set; } = Array.Empty<string>();

        /// <summary>特效放在主角世界 x 的前方多少像素(朝左时往左,朝右时往右);基线里朝左的一次鬼斩量到 40,朝右没有样本(按镜像假设)。</summary>
        public int EffectForwardX { get; internal set; }

        /// <summary>多段技能的配方(证据 E-129);不是配方技能为 null。</summary>
        public SkillRecipe Recipe { get; internal set; }

        /// <summary>配方技能每一段命中用的攻击信息与伤害数,与 <see cref="SkillRecipe.Stages"/> 一一对应;不是配方技能为 null。</summary>
        public IReadOnlyList<HeroStrikeInfo> StageStrikes { get; internal set; }

        /// <summary>配方技能预载的特效动画(PVF 路径,按 <see cref="SkillStageSpec.EffectIndex"/> 取);没有为空。</summary>
        public IReadOnlyList<string> StageEffects { get; internal set; } = Array.Empty<string>();

        /// <summary>第 <paramref name="stage"/> 段命中用的攻击信息:配方技能取这一段的(段号越界取最后一段),别的技能取 <see cref="Strike"/>。</summary>
        public HeroStrikeInfo? StrikeAt(int stage)
        {
            if (StageStrikes == null || StageStrikes.Count == 0)
            {
                return Strike;
            }
            return StageStrikes[Math.Max(0, Math.Min(stage, StageStrikes.Count - 1))];
        }

        /// <summary>此刻还剩的冷却(毫秒);0 表示能放。</summary>
        public double CooldownLeftMs { get; internal set; }

        public bool Ready => CooldownLeftMs <= 0.0;

        /// <summary>冷却剩余的比例(0 到 1);界面画冷却遮罩用。</summary>
        public double CooldownFraction => CooldownMs > 0 ? Math.Min(1.0, Math.Max(0.0, CooldownLeftMs / CooldownMs)) : 0.0;

        public HeroSkillSlot(SkillKeys key, int skillId, int level, SkillDef def)
        {
            Key = key;
            int position = 0;
            for (int bits = (int)key; bits > 1; bits >>= 1)
            {
                position++;
            }
            Position = position;
            SkillId = skillId;
            Level = level < 1 ? 1 : level;
            Def = def;
            CooldownMs = def == null ? 0 : Math.Max(0, def.Interpolate(def.CoolTime, Level));
            ConsumeMp = def == null ? 0 : Math.Max(0, def.Interpolate(def.ConsumeMp, Level));
        }
    }

    /// <summary>
    /// 主角的技能快捷槽与冷却(证据 E-076)。
    /// 槽位:给了技能簿(<see cref="SkillBook"/>,M35)就按它的六格快捷栏排——第 n 格是第 n 个键(A S D F G H),槽里的等级是学到的等级;
    /// 没给时取角色脚本 [initial value] 里的 [skill](初始技能表,"编号 等级"成对)里按出现顺序的主动技能(有 [type] "[active]" 与 [command]),
    /// 依次放进 A、S、D 三个快捷键——新建的剑士得到鬼斩、上挑、后跳,与原版基线 HUD 里三个快捷槽的图标一致。
    /// 放法:鬼斩 = 原地播自己的动作动画(<see cref="TownHero.BeginSkillMotion"/>),命中用自己的攻击信息与 .skl 的伤害数(<see cref="HeroSkillSlot.Strike"/>);
    /// 上挑 = 连招第三段的动画与攻击信息整段打一遍(<see cref="TownHero.BeginSkillStrike"/>);后跳 = 跳跃状态的特殊参数(<see cref="TownHero.BeginBackStep"/>)。
    /// 冷却在放出的那一刻开始(.skl 的 [auto cooltime apply] 1 对应的行为,客户端在进入技能状态时启动冷却),按墙钟倒数。
    /// 耗蓝(证据 E-087):给了 <see cref="HeroVitals"/> 就在技能真正开始的那一刻扣 [consume MP],魔法值不够就放不出来(<see cref="SkillCastResult.NoMp"/>,
    /// 不启动冷却、不扣蓝);没给就不耗蓝(旧行为)。
    /// 没做:施放期间的无敌与霸体、冷却缩减、技能等级成长对伤害与攻击范围的影响。
    /// </summary>
    public sealed class HeroSkills
    {
        /// <summary>快捷槽的个数(A S D F G H)。</summary>
        public const int SlotCount = SkillBook.QuickSlots;

        private readonly List<HeroSkillSlot> _slots;

        /// <summary>按快捷栏位置从小到大;只含放了技能的格(空格没有槽,键没有绑定)。旧的初始技能表创建方式(没给技能簿)只会有 A、S、D 三个。</summary>
        public IReadOnlyList<HeroSkillSlot> Slots => _slots;

        public HeroSkills(IEnumerable<HeroSkillSlot> slots)
        {
            _slots = new List<HeroSkillSlot>(slots ?? throw new ArgumentNullException(nameof(slots)));
        }

        /// <summary>这个技能编号有没有实现放法(三个手写的,加上默认配方表里的)。</summary>
        public static bool IsImplemented(int skillId)
        {
            return skillId == SkillIds.HardAttack || skillId == SkillIds.UpperSlash || skillId == SkillIds.BackStep || SkillRecipes.Default.Find(skillId) != null;
        }

        private static bool IsImplemented(HeroSkillSlot slot)
        {
            return slot.Recipe != null || IsImplemented(slot.SkillId);
        }

        /// <summary>
        /// 某个技能学到的等级(配方里"要求学会某技能才进下一段"的判定用);按技能簿创建时自动接上,没接就都当没学。
        /// </summary>
        public Func<int, int> SkillLevelOf { get; set; }

        /// <summary>
        /// 按职业 <paramref name="job"/> 的角色脚本 <paramref name="chr"/> 的初始技能表排出快捷槽;读不到角色脚本或技能表时没有槽。
        /// </summary>
        public static HeroSkills Create(GameData data, CharacterDef chr, int job)
        {
            var slots = new List<HeroSkillSlot>();
            if (data == null || chr == null)
            {
                return new HeroSkills(slots);
            }
            int[] initial = chr.Tags.Ints("[skill]");
            for (int i = 0; i + 1 < initial.Length && slots.Count < 3; i += 2)
            {
                SkillDef def = data.GetSkill(job, initial[i]);
                if (def == null || def.Type != "[active]" || !def.HasCommand)
                {
                    continue;
                }
                slots.Add(MakeSlot(data, chr, (SkillKeys)(1 << slots.Count), def, initial[i + 1]));
            }
            return new HeroSkills(slots);
        }

        /// <summary>
        /// 按技能簿(M35)的快捷栏排出快捷槽:第 n 格放的技能进第 n 个键(A S D F G H),等级取技能簿里学到的;空格没有槽。
        /// 读不到角色脚本或技能定义的格跳过(技能簿的目录里有定义就先用它)。
        /// </summary>
        public static HeroSkills Create(GameData data, CharacterDef chr, int job, SkillBook book, SkillRecipes recipes = null)
        {
            var slots = new List<HeroSkillSlot>();
            if (data == null || chr == null || book == null)
            {
                return new HeroSkills(slots);
            }
            recipes = recipes ?? SkillRecipes.Default;
            SkillEffectTable effects = job == 0 ? SkillEffectTable.Load(data, SkillEffectTable.SwordmanJob) : SkillEffectTable.Empty;
            for (int position = 0; position < SkillBook.QuickSlots; position++)
            {
                int skillId = book.QuickAt(position);
                if (skillId == 0)
                {
                    continue;
                }
                SkillDef def = book.Catalog.Find(skillId) ?? data.GetSkill(job, skillId);
                if (def == null)
                {
                    continue;
                }
                slots.Add(MakeSlot(data, chr, (SkillKeys)(1 << position), def, book.LevelOf(skillId), recipes, effects));
            }
            return new HeroSkills(slots) { SkillLevelOf = book.LevelOf };
        }

        private static HeroSkillSlot MakeSlot(GameData data, CharacterDef chr, SkillKeys key, SkillDef def, int level, SkillRecipes recipes = null,
            SkillEffectTable effects = null)
        {
            var slot = new HeroSkillSlot(key, def.Id, level, def);
            SkillRecipe recipe = (recipes ?? SkillRecipes.Default).Find(def.Id);
            if (recipe != null)
            {
                // 配方技能:逐段取攻击信息(角色 [etc attack info] 的下标)与伤害数(.skl [level info] 当前等级的两列),特效取技能预载表的下标(证据 E-129)
                slot.Recipe = recipe;
                var strikes = new List<HeroStrikeInfo>();
                foreach (SkillStageSpec stage in recipe.Stages)
                {
                    strikes.Add(HeroStrikeInfo.Read(data, chr.CustomAttackInfoPath(stage.AttackInfoIndex))
                        .WithSkillDamage(def.GetLevel(slot.Level, stage.DamageRateColumn, false), def.GetLevel(slot.Level, stage.DamageFixedColumn, false)));
                }
                slot.StageStrikes = strikes;
                var paths = new List<string>();
                foreach (string rel in effects == null ? Array.Empty<string>() : (IReadOnlyList<string>)effects.EffectsOf(def.Id))
                {
                    paths.Add(PvfPath.ResolveRelativeTo(chr.Path, rel));
                }
                slot.StageEffects = paths;
                return slot;
            }
            if (def.Id == SkillIds.HardAttack)
            {
                // 鬼斩有自己的攻击信息(角色 [etc attack info] 里的 hardattack.atk,按名字找)与伤害数(.skl [level info] 当前等级的第 0、1 列)
                int atk = chr.EtcAttackInfoIndexOf(HardAttackInfoFile);
                slot.Strike = HeroStrikeInfo.Read(data, atk < 0 ? null : chr.CustomAttackInfoPath(atk))
                    .WithSkillDamage(def.GetLevel(slot.Level, 0, false), def.GetLevel(slot.Level, 1, false));
                // 斩击光影:角色目录下 effect/animation/ 里的 hardattack1.ani(普通混合)、hardattack2.ani(加法混合,由帧属性决定)
                slot.EffectPaths = new[]
                {
                    PvfPath.ResolveRelativeTo(chr.Path, HardAttackEffectDir + "hardattack1.ani"),
                    PvfPath.ResolveRelativeTo(chr.Path, HardAttackEffectDir + "hardattack2.ani"),
                };
                slot.EffectForwardX = HardAttackEffectForwardX;
            }
            return slot;
        }

        /// <summary>鬼斩的动作文件名(角色 [etc motion] 里)与攻击信息文件名(角色 [etc attack info] 里)。两个表的下标不同,按名字找。</summary>
        public const string HardAttackMotionFile = "hardattack.ani";

        public const string HardAttackInfoFile = "hardattack.atk";

        /// <summary>鬼斩斩击光影所在的目录(相对角色脚本所在目录)。</summary>
        public const string HardAttackEffectDir = "effect/animation/";

        /// <summary>鬼斩特效放在主角前方多少像素(基线截图与效果图做模板匹配得到:朝左时效果锚点比主角世界 x 再往左 40,纵向没有偏移)。</summary>
        public const int HardAttackEffectForwardX = 40;

        /// <summary>这个技能编号占的槽;没有则 null。</summary>
        public HeroSkillSlot SlotOfSkill(int skillId)
        {
            foreach (HeroSkillSlot s in _slots)
            {
                if (s.SkillId == skillId)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>这个键对应的槽;没有则 null。</summary>
        public HeroSkillSlot SlotOf(SkillKeys key)
        {
            foreach (HeroSkillSlot s in _slots)
            {
                if (s.Key == key)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>把另一份技能表里同编号技能的剩余冷却带过来(换房间时冷却不清零)。</summary>
        public void CopyCooldownsFrom(HeroSkills other)
        {
            if (other == null || ReferenceEquals(other, this))
            {
                return;
            }
            foreach (HeroSkillSlot s in _slots)
            {
                foreach (HeroSkillSlot o in other._slots)
                {
                    if (o.SkillId == s.SkillId)
                    {
                        s.CooldownLeftMs = Math.Min(o.CooldownLeftMs, s.CooldownMs);
                    }
                }
            }
        }

        /// <summary>冷却倒数 <paramref name="deltaMs"/> 毫秒。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs <= 0.0)
            {
                return;
            }
            foreach (HeroSkillSlot s in _slots)
            {
                if (s.CooldownLeftMs > 0.0)
                {
                    s.CooldownLeftMs = Math.Max(0.0, s.CooldownLeftMs - deltaMs);
                }
            }
        }

        /// <summary>
        /// 处理这一步按下的技能键:每个键按槽的次序试一遍,第一个放出来的就是这一步的技能(同一步里最多放一个)。
        /// 返回放出来的技能的槽;没放出来返回 null。
        /// </summary>
        public HeroSkillSlot TryCast(SkillKeys pressed, TownHero hero, HeroVitals vitals = null, int heldX = 0, int heldY = 0)
        {
            LastCastResult = SkillCastResult.NotBound;
            if (pressed == SkillKeys.None || hero == null)
            {
                return null;
            }
            foreach (HeroSkillSlot s in _slots)
            {
                if ((pressed & s.Key) == 0)
                {
                    continue;
                }
                SkillCastResult result = Cast(s, hero, vitals, heldX, heldY);
                LastCastResult = result;
                if (result == SkillCastResult.Cast)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>最近一次 <see cref="TryCast"/> 的结果(放出来了就是 <see cref="SkillCastResult.Cast"/>;按了几个键时是最后一个被试的槽的结果)。用来决定放"没魔法"、"冷却中"的语音。</summary>
        public SkillCastResult LastCastResult { get; private set; } = SkillCastResult.NotBound;

        /// <summary>
        /// 试着放这一个槽的技能;放出来就启动冷却,并(给了 <paramref name="vitals"/> 时)扣掉 [consume MP]。
        /// 检查顺序:没绑定、没实现、冷却、魔法值(不够就不放),最后才是"此刻能不能开始"——技能没能开始(<see cref="SkillCastResult.Busy"/>)不扣蓝、不进冷却。
        /// </summary>
        public SkillCastResult Cast(HeroSkillSlot slot, TownHero hero, HeroVitals vitals = null, int heldX = 0, int heldY = 0)
        {
            if (slot == null)
            {
                return SkillCastResult.NotBound;
            }
            if (!IsImplemented(slot))
            {
                return SkillCastResult.NotImplemented;
            }
            if (!slot.Ready)
            {
                return SkillCastResult.Cooldown;
            }
            if (vitals != null && (!vitals.IsAlive || vitals.Mp < slot.ConsumeMp))
            {
                return SkillCastResult.NoMp;
            }
            bool began;
            switch (slot.SkillId)
            {
                case SkillIds.HardAttack:
                    // 鬼斩:原地播 [etc motion] 里的 hardattack.ani(客户端的状态 20),不滑行(基线里主角整段位置不变,冲出去的样子画在帧图自带的偏移里)
                    int etc = hero.EtcMotionIndexOf(HardAttackMotionFile);
                    began = etc >= 0 && hero.BeginSkillMotion(slot.SkillId, etc, 0.0, 0.0);
                    break;
                case SkillIds.UpperSlash:
                    began = hero.BeginSkillStrike(slot.SkillId, UpperSlashStage);
                    break;
                case SkillIds.BackStep:
                    began = hero.BeginBackStep(slot.SkillId, slot.Def == null ? 0 : slot.Def.GetStatic(0, false));
                    break;
                default:
                    began = slot.Recipe != null && BeginStage(slot, 0, hero, heldX, heldY, false);
                    break;
            }
            if (!began)
            {
                return SkillCastResult.Busy;
            }
            vitals?.TrySpendMp(slot.ConsumeMp);
            slot.CooldownLeftMs = slot.CooldownMs;
            return SkillCastResult.Cast;
        }

        /// <summary>
        /// 配方技能接下一段(证据 E-129):主角正在放某个配方技能的主体阶段,这一步按了它的技能键,当前段的动画已经播过
        /// <see cref="SkillRecipe.ChainFromFrame"/> 帧,还有下一段,下一段要求的技能也学过,就换成下一段。
        /// 不扣蓝、不进冷却(客户端是在逐帧代码里直接请求下一段,不再走起手函数的检查)。返回换段的槽;没换返回 null。
        /// </summary>
        public HeroSkillSlot TryChain(SkillKeys pressed, TownHero hero, int heldX = 0, int heldY = 0)
        {
            if (pressed == SkillKeys.None || hero == null || hero.Motion != HeroMotion.Skill || hero.SkillWindingUp)
            {
                return null;
            }
            HeroSkillSlot slot = SlotOfSkill(hero.SkillId);
            SkillRecipe recipe = slot?.Recipe;
            if (recipe == null || (pressed & slot.Key) == 0)
            {
                return null;
            }
            int next = hero.SkillStage + 1;
            if (next >= recipe.Stages.Count || hero.CurrentFrame < recipe.ChainFromFrame)
            {
                return null;
            }
            int need = recipe.Stages[next].RequiresSkill;
            if (need != 0 && (SkillLevelOf == null || SkillLevelOf(need) <= 0))
            {
                return null;
            }
            return BeginStage(slot, next, hero, heldX, heldY, true) ? slot : null;
        }

        private static bool BeginStage(HeroSkillSlot slot, int stage, TownHero hero, int heldX, int heldY, bool chain)
        {
            SkillStageSpec spec = slot.Recipe.Stages[stage];
            Slide(slot, hero, heldX, heldY, out double speed, out double accel);
            return chain
                ? hero.ChainSkillMotion(slot.SkillId, spec.MotionIndex, speed, accel)
                : hero.BeginSkillMotion(slot.SkillId, spec.MotionIndex, speed, accel);
        }

        /// <summary>
        /// 进入一段那一刻的起滑速度与加速度(证据 E-129):没按方向键取基础的两个数;按着朝向一侧的方向键,再加上"顺着按"的加成;
        /// 按着别的方向(反向、只按上下)就不滑。同时按了左右与上下时只看左右(原版怎么选没读到,按横向优先推断)。
        /// </summary>
        internal static void Slide(HeroSkillSlot slot, TownHero hero, int heldX, int heldY, out double speed, out double accel)
        {
            SkillRecipe r = slot.Recipe;
            SkillDef def = slot.Def;
            speed = r.SlideSpeedIndex >= 0 && def != null ? def.GetStatic(r.SlideSpeedIndex, false) : 0;
            accel = r.SlideAccelIndex >= 0 && def != null ? def.GetStatic(r.SlideAccelIndex, false) : 0;
            heldX = Math.Sign(heldX);
            heldY = Math.Sign(heldY);
            if (heldX == 0 && heldY == 0)
            {
                return;
            }
            bool forward = heldX != 0 && (heldX < 0) == hero.FacingLeft;
            if (!forward)
            {
                speed = 0;
                accel = 0;
                return;
            }
            if (r.ForwardSpeedIndex >= 0 && def != null)
            {
                speed += def.GetStatic(r.ForwardSpeedIndex, false);
            }
            if (r.ForwardAccelIndex >= 0 && def != null)
            {
                accel += def.GetStatic(r.ForwardAccelIndex, false);
            }
        }

        /// <summary>上挑用连招的第几段(从 0 数):第三段(客户端取"连招动作表"的第 2 项,证据 E-076)。</summary>
        public const int UpperSlashStage = 2;
    }
}
