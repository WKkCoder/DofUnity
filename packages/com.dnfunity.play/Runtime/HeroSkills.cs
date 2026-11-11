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

        /// <summary>倒地起身类技能(引擎状态 0x12,躺在地上时按指令键起身,证据 E-140;没有走 <see cref="HeroSkills.Cast"/>,由房间战斗会话直接处理)。</summary>
        public const int QuickStand = 190;
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
    public sealed partial class HeroSkillSlot
    {
        public SkillKeys Key { get; }

        /// <summary>快捷栏里的位置(0 到 5,对应键 A S D F G H;底栏上从左数第几个槽);没有快捷键、只靠出招指令放的槽是 -1。</summary>
        public int Position { get; }

        public int SkillId { get; }
        public int Level { get; }
        public SkillDef Def { get; }

        /// <summary>满冷却时长(毫秒):.skl 的 [cool time] 按等级插值;带 [cooltime level info] 的技能取 [level info] 那一列当前等级的值(E-163);两个标签都没有为 0(例如后跳)。</summary>
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

        /// <summary>增益类技能的放法(E-141);不是增益类技能为 null。增益类技能没有配方(没有多段动作与攻击信息)。</summary>
        public BuffRecipe Buff { get; internal set; }

        /// <summary>
        /// 增益持续时间(毫秒):.skl [level info] 当前等级的 <see cref="BuffRecipe.DurationColumn"/> 列;不是增益类技能或读不到定义为 0。
        /// </summary>
        public double BuffDurationMs => Buff == null || Def == null ? 0.0 : Def.GetLevel(Level, Buff.DurationColumn, false);

        /// <summary>
        /// 增益加在属性上的数:[static data] 的 <see cref="BuffRecipe.AmountStaticIndex"/> 项(下标不小于 0 时)或 [level info] 当前等级的 <see cref="BuffRecipe.AmountColumn"/> 列,
        /// 再乘 <see cref="BuffRecipe.AmountScale"/>(概率类增益把千分比换成 0 到 1,E-142);不是增益类技能或读不到定义为 0。
        /// </summary>
        public double BuffAmount
        {
            get
            {
                if (Buff == null || Def == null)
                {
                    return 0.0;
                }
                int raw = Buff.AmountStaticIndex >= 0 ? Def.GetStatic(Buff.AmountStaticIndex, false) : Def.GetLevel(Level, Buff.AmountColumn, false);
                return raw * Buff.AmountScale;
            }
        }

        /// <summary>防御姿态类技能的放法(E-164);不是防御姿态类技能为 null。这类技能没有配方(没有多段动作与攻击信息),身体不打人。</summary>
        public GuardRecipe Guard { get; internal set; }

        /// <summary>防御姿态进入时的最短保持毫秒:.skl [static data] 的 <see cref="GuardRecipe.MinHoldStaticIndex"/> 项;不是防御姿态类技能或读不到定义为 0。</summary>
        public int GuardMinHoldMs => Guard == null || Def == null ? 0 : Math.Max(0, Def.GetStatic(Guard.MinHoldStaticIndex, false));

        /// <summary>挡下一击后后滑的起滑速度(像素/秒):.skl [level info] 当前等级的 <see cref="GuardRecipe.RecoilSpeedColumn"/> 列;不是防御姿态类技能为 0。</summary>
        public int GuardRecoilSpeed => Guard == null || Def == null ? 0 : Def.GetLevel(Level, Guard.RecoilSpeedColumn, false);

        /// <summary>挡下一击后后滑的持续毫秒:.skl [level info] 当前等级的 <see cref="GuardRecipe.RecoilMsColumn"/> 列(不是正数时 <see cref="TownHero.BlockHit"/> 取默认值);不是防御姿态类技能为 0。</summary>
        public int GuardRecoilMs => Guard == null || Def == null ? 0 : Def.GetLevel(Level, Guard.RecoilMsColumn, false);

        /// <summary>
        /// 挡下这一类攻击时穿过的伤害百分比(M66,证据 E-164):物理攻击 = 100 − [level info] 的 <see cref="GuardRecipe.PhysicalColumn"/> 列、魔法攻击 = 100 − <see cref="GuardRecipe.MagicalColumn"/> 列
        /// (客户端把这个数先转成整数再存;列值比 100 大时读到的代码里没有下限钳制,这里取 0——防御不该变成治疗);无视防御的攻击不折算(100)。不是防御姿态类技能为 100。
        /// </summary>
        public int GuardPassPercent(AttackKind kind)
        {
            if (Guard == null || Def == null)
            {
                return 100;
            }
            int column = kind == AttackKind.Physic ? Guard.PhysicalColumn : kind == AttackKind.Magic ? Guard.MagicalColumn : -1;
            if (column < 0)
            {
                return 100;
            }
            return Math.Max(0, 100 - Def.GetLevel(Level, column, false));
        }

        /// <summary>配方技能每一段命中用的攻击信息与伤害数,与 <see cref="SkillRecipe.Stages"/> 一一对应;不是配方技能为 null。</summary>
        public IReadOnlyList<HeroStrikeInfo> StageStrikes { get; internal set; }

        /// <summary>建槽时用于缩放 level info 的被动倍率( native 状态 0;运行时可用 <see cref="StrikeAt(int,int)"/> 重算)。</summary>
        internal float StagePassivePowerRateAtBuild { get; set; } = 1f;

        internal IReadOnlyList<SkillPassivePowerRate.Link> PassivePowerLinks { get; set; }

        internal Func<int, int> PassiveSkillLevelOf { get; set; }

        internal Func<int, SkillDef> PassiveResolveSkill { get; set; }

        /// <summary>
        /// 蓄力跳跃类技能(<see cref="SkillRecipe.Hop"/>)重武装的命中窗口用的攻击信息与伤害数(证据 E-138);窗口数为 1 时唯一的窗口也用它。
        /// 其它技能为 null。
        /// </summary>
        public HeroStrikeInfo? HopRepeatStrike { get; internal set; }

        /// <summary>配方技能预载的特效动画(PVF 路径,按 <see cref="SkillStageSpec.EffectIndex"/> 取);没有为空。</summary>
        public IReadOnlyList<string> StageEffects { get; internal set; } = Array.Empty<string>();

        /// <summary>
        /// 配方技能放出的被动对象命中用的攻击信息与伤害数,与 <see cref="SkillRecipe.Objects"/> 一一对应
        /// (攻击信息取对象 .obj 的 [attack info],伤害数取 .skl [level info] 当前等级的两列,证据 E-135);没有为空表。
        /// </summary>
        public IReadOnlyList<HeroStrikeInfo> ObjectStrikes { get; internal set; } = Array.Empty<HeroStrikeInfo>();

        /// <summary>
        /// 第 <paramref name="stage"/> 段主角的身体有没有自己的命中:配方技能看这一段的攻击信息下标(波系技能的施法动作没有,伤害全在放出的对象上);
        /// 别的技能(没有配方)有;增益类技能的施法动作没有攻击框,主角的身体不打人;防御姿态类技能同样(姿势动作不设攻击信息,M66)。
        /// </summary>
        public bool StageHasOwnStrike(int stage)
        {
            if (Buff != null || Guard != null || Throw != null)
            {
                return false;
            }
            if (Recipe == null || Recipe.Stages.Count == 0)
            {
                return true;
            }
            return Recipe.Stages[Math.Max(0, Math.Min(stage, Recipe.Stages.Count - 1))].HasOwnStrike;
        }

        /// <summary>第 <paramref name="stage"/> 段命中用的攻击信息:配方技能取这一段的(段号越界取最后一段),别的技能取 <see cref="Strike"/>。</summary>
        public HeroStrikeInfo? StrikeAt(int stage)
        {
            if (StageStrikes == null || StageStrikes.Count == 0)
            {
                return Strike;
            }
            return StageStrikes[Math.Max(0, Math.Min(stage, StageStrikes.Count - 1))];
        }

        /// <summary>按运行时 native 状态重算被动威力列(E-181);与建槽倍率不同时等比调整 SkillRate/Fixed。</summary>
        public HeroStrikeInfo? StrikeAtWithPassiveNativeState(int stage, int passiveNativeState)
        {
            HeroStrikeInfo? strike = StrikeAt(stage);
            if (PassivePowerLinks == null || PassivePowerLinks.Count == 0 || PassiveSkillLevelOf == null || PassiveResolveSkill == null
                || !strike.HasValue || !strike.Value.IsSkillDamage)
            {
                return strike;
            }
            float live = SkillPassivePowerRate.Accumulate(1f, SkillId, passiveNativeState, PassiveSkillLevelOf, PassiveResolveSkill, PassivePowerLinks);
            if (Math.Abs(live - StagePassivePowerRateAtBuild) < 1e-5f)
            {
                return strike;
            }
            HeroStrikeInfo v = strike.Value;
            int rate = (int)Math.Round(v.SkillRatePercent * live / StagePassivePowerRateAtBuild, MidpointRounding.AwayFromZero);
            int fix = (int)Math.Round(v.SkillFixed * live / StagePassivePowerRateAtBuild, MidpointRounding.AwayFromZero);
            return v.WithSkillDamage(rate, fix);
        }

        /// <summary>
        /// 每一段每个关键帧标志事件换成的攻击信息与伤害数(M65,证据 E-163),与 <see cref="SkillStageSpec.FlagEvents"/> 一一对应;
        /// 事件没有换命中的项为 null。不是配方技能为 null。
        /// </summary>
        public IReadOnlyList<IReadOnlyList<HeroStrikeInfo?>> FlagStrikes { get; internal set; }

        private SkillStageSpec StageSpecOf(int stage)
        {
            return Recipe == null || Recipe.Stages.Count == 0 ? null : Recipe.Stages[Math.Max(0, Math.Min(stage, Recipe.Stages.Count - 1))];
        }

        /// <summary>第 <paramref name="stage"/> 段里关键帧标志 <paramref name="flag"/> 对应的事件下标(表里第一项);这一段没有这个标志的事件返回 -1。</summary>
        public int FlagEventIndex(int stage, int flag)
        {
            SkillStageSpec spec = StageSpecOf(stage);
            if (spec == null)
            {
                return -1;
            }
            for (int i = 0; i < spec.FlagEvents.Count; i++)
            {
                if (spec.FlagEvents[i].Flag == flag)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>第 <paramref name="stage"/> 段第 <paramref name="index"/> 个关键帧标志事件换了攻击信息或伤害列(只清命中名单的事件为 false)。</summary>
        public bool FlagEventChangesStrike(int stage, int index)
        {
            SkillStageSpec spec = StageSpecOf(stage);
            return spec != null && (uint)index < (uint)spec.FlagEvents.Count && spec.FlagEvents[index].ChangesStrike;
        }

        /// <summary>
        /// 第 <paramref name="stage"/> 段、已触发到第 <paramref name="variant"/> 个换命中的关键帧标志事件(从 1 数是事件下标加 1,0 是还没触发过)之后命中用的攻击信息;
        /// 这个事件没换命中时与 <see cref="StrikeAt(int)"/> 相同。
        /// </summary>
        public HeroStrikeInfo? StrikeAt(int stage, int variant)
        {
            if (variant > 0 && FlagStrikes != null && FlagStrikes.Count > 0)
            {
                IReadOnlyList<HeroStrikeInfo?> events = FlagStrikes[Math.Max(0, Math.Min(stage, FlagStrikes.Count - 1))];
                if (variant <= events.Count && events[variant - 1] != null)
                {
                    return events[variant - 1];
                }
            }
            return StrikeAt(stage);
        }

        /// <summary>此刻还剩的冷却(毫秒);0 表示能放。</summary>
        public double CooldownLeftMs { get; internal set; }

        public bool Ready => CooldownLeftMs <= 0.0;

        /// <summary>
        /// 算"这个技能的键按着"的键:快捷键,加上指令扳机对应的 <see cref="SkillKeys.CommandSkill"/>、<see cref="SkillKeys.CommandBuff"/> 或 <see cref="SkillKeys.Attack"/>
        /// (蓄力类技能靠指令放出来时,看扳机键是不是还按着,E-140;防御姿态类技能的扳机是攻击键,E-164)。
        /// </summary>
        public SkillKeys HoldKeys
        {
            get
            {
                SkillKeys keys = Key;
                SkillCommand c = Def?.Command;
                if (c != null && !c.IsEmpty)
                {
                    if (c.Trigger == CommandKey.Skill)
                    {
                        keys |= SkillKeys.CommandSkill;
                    }
                    else if (c.Trigger == CommandKey.Buff)
                    {
                        keys |= SkillKeys.CommandBuff;
                    }
                    else if (c.Trigger == CommandKey.Attack)
                    {
                        keys |= SkillKeys.Attack;
                    }
                }
                return keys;
            }
        }

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
            Position = key == SkillKeys.None ? -1 : position;
            SkillId = skillId;
            Level = level < 1 ? 1 : level;
            Def = def;
            // 冷却:一般技能是 [cool time] 的两个端点按等级插值;带 [cooltime level info] 的技能(没有 [cool time])取 [level info] 那一列当前等级的值当毫秒(E-163,推断)
            CooldownMs = def == null ? 0
                : def.CoolTimeColumn >= 0 ? Math.Max(0, def.GetLevel(Level, def.CoolTimeColumn, false))
                : Math.Max(0, def.Interpolate(def.CoolTime, Level));
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
    /// 没做:施放期间的无敌与技能自带的霸体(概率触发类增益带来的霸体已做,E-142)、冷却缩减、技能等级成长对伤害与攻击范围的影响。
    /// </summary>
    public sealed partial class HeroSkills
    {
        /// <summary>快捷槽的个数(A S D F G H)。</summary>
        public const int SlotCount = SkillBook.QuickSlots;

        private readonly List<HeroSkillSlot> _slots;
        private readonly List<HeroSkillSlot> _commandSlots;
        private List<HeroSkillSlot> _commandOrder;

        /// <summary>按快捷栏位置从小到大;只含放了技能的格(空格没有槽,键没有绑定)。旧的初始技能表创建方式(没给技能簿)只会有 A、S、D 三个。</summary>
        public IReadOnlyList<HeroSkillSlot> Slots => _slots;

        /// <summary>
        /// 学会了、有出招指令、没放进快捷栏的技能的槽(<see cref="HeroSkillSlot.Key"/> 是 None):只能靠指令放(<see cref="TryCastCommand"/>),
        /// 不画在底栏上。放进了快捷栏的技能不在这里——指令与快捷键共用同一个槽(同一份冷却)。
        /// </summary>
        public IReadOnlyList<HeroSkillSlot> CommandSlots => _commandSlots;

        public HeroSkills(IEnumerable<HeroSkillSlot> slots)
            : this(slots, null)
        {
        }

        public HeroSkills(IEnumerable<HeroSkillSlot> slots, IEnumerable<HeroSkillSlot> commandSlots)
        {
            _slots = new List<HeroSkillSlot>(slots ?? throw new ArgumentNullException(nameof(slots)));
            _commandSlots = new List<HeroSkillSlot>();
            if (commandSlots != null)
            {
                foreach (HeroSkillSlot c in commandSlots)
                {
                    if (c != null && SlotOfSkill(c.SkillId) == null)
                    {
                        _commandSlots.Add(c);
                    }
                }
            }
        }

        /// <summary>这个技能编号有没有实现放法(三个手写的、倒地起身,加上默认配方表里的配方技能、增益类技能、防御姿态类技能与投掷类技能)。</summary>
        public static bool IsImplemented(int skillId)
        {
            return skillId == SkillIds.HardAttack || skillId == SkillIds.UpperSlash || skillId == SkillIds.BackStep || skillId == SkillIds.QuickStand
                || SkillRecipes.Default.Find(skillId) != null || SkillRecipes.Default.FindBuff(skillId) != null || SkillRecipes.Default.FindGuard(skillId) != null
                || SkillRecipes.Default.FindClassBuff(skillId) != null || SkillRecipes.Default.FindThrow(skillId) != null;
        }

        private static bool IsImplemented(HeroSkillSlot slot)
        {
            return slot.Recipe != null || slot.Buff != null || slot.Guard != null || slot.Throw != null || IsImplemented(slot.SkillId);
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
                slots.Add(MakeSlot(data, chr, (SkillKeys)(1 << slots.Count), def, initial[i + 1], skillLevelOf: _ => initial[i + 1],
                    resolveSkill: id => data.GetSkill(job, id)));
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
            IReadOnlyList<SkillPassivePowerRate.Link> registeredLinks = Dnf.Game.SkillPassivePowerRegistry.CollectForBook(data, job, book);
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
                slots.Add(MakeSlot(data, chr, (SkillKeys)(1 << position), def, book.LevelOf(skillId), recipes, effects, book.LevelOf,
                    id => book.Catalog.Find(id) ?? data.GetSkill(job, id), registeredLinks));
            }
            // 学会了、有出招指令、没放进快捷栏的主动技能:只靠指令放(E-140)。没有放法的技能不进来(按指令也放不出东西)
            var commandSlots = new List<HeroSkillSlot>();
            foreach (KeyValuePair<int, int> learned in book.Learned)
            {
                if (learned.Value <= 0 || book.QuickSlotOf(learned.Key) >= 0)
                {
                    continue;
                }
                SkillDef def = book.Catalog.Find(learned.Key) ?? data.GetSkill(job, learned.Key);
                if (def == null || def.Type != "[active]" || def.Command.IsEmpty)
                {
                    continue;
                }
                HeroSkillSlot slot = MakeSlot(data, chr, SkillKeys.None, def, learned.Value, recipes, effects, book.LevelOf,
                    id => book.Catalog.Find(id) ?? data.GetSkill(job, id), registeredLinks);
                if (IsImplemented(slot))
                {
                    commandSlots.Add(slot);
                }
            }
            return new HeroSkills(slots, commandSlots) { SkillLevelOf = book.LevelOf };
        }

        private static HeroSkillSlot MakeSlot(GameData data, CharacterDef chr, SkillKeys key, SkillDef def, int level, SkillRecipes recipes = null,
            SkillEffectTable effects = null, Func<int, int> skillLevelOf = null, Func<int, SkillDef> resolveSkill = null,
            IReadOnlyList<SkillPassivePowerRate.Link> registeredLinks = null)
        {
            var slot = new HeroSkillSlot(key, def.Id, level, def);
            SkillRecipe recipe = (recipes ?? SkillRecipes.Default).Find(def.Id)?.ExpandFor(def);        // 逐击连段类按这个技能的击数展开(状态 0x27)
            slot.Buff = (recipes ?? SkillRecipes.Default).FindBuff(def.Id) ?? (recipes ?? SkillRecipes.Default).FindClassBuff(def.Id);   // 增益类技能(E-141):没有配方,放法只有一个施法动作与一个附加体
            slot.Guard = (recipes ?? SkillRecipes.Default).FindGuard(def.Id);      // 防御姿态类技能(E-164):没有配方,放法只有一个姿势动作,按住攻击键维持
            if (recipe != null)
            {
                // 配方技能:逐段取攻击信息(角色 [etc attack info] 的下标)与伤害数(.skl [level info] 当前等级的两列),特效取技能预载表的下标(证据 E-129)
                slot.Recipe = recipe;
                IReadOnlyList<SkillPassivePowerRate.Link> recipeLinks = SkillPassivePowerRate.LinksFromRecipes(recipe.PassivePowerLinks, def.Id);
                IReadOnlyList<SkillPassivePowerRate.Link> regForActive = SkillPassivePowerRate.RegisteredLinksForActive(registeredLinks, def.Id);
                IReadOnlyList<SkillPassivePowerRate.Link> powerLinks = SkillPassivePowerRate.MergePassiveLinks(recipeLinks, regForActive);
                Func<int, SkillDef> resolve = resolveSkill ?? (id => data.GetSkill(chr.Job, id));
                Func<int, int> levelOf = skillLevelOf ?? (_ => 0);
                const int slotBuildNativeState = 0;
                float powerRate = SkillPassivePowerRate.Accumulate(1f, def.Id, slotBuildNativeState, levelOf, resolve, powerLinks);
                slot.PassivePowerLinks = powerLinks;
                slot.PassiveSkillLevelOf = levelOf;
                slot.PassiveResolveSkill = resolve;
                slot.StagePassivePowerRateAtBuild = powerRate;
                var strikes = new List<HeroStrikeInfo>();
                foreach (SkillStageSpec stage in recipe.Stages)
                {
                    if (stage.HasOwnStrike)
                    {
                        strikes.Add(HeroStrikeInfo.Read(data, chr.CustomAttackInfoPath(stage.AttackInfoIndex))
                            .WithSkillDamage(StageRate(recipe, strikes.Count, def, stage, ScaleLevel(def, slot.Level, stage.DamageRateColumn, powerRate)),
                                ScaleLevel(def, slot.Level, stage.DamageFixedColumn, powerRate)));
                    }
                    else if (stage.DamageRateColumn >= 0 || stage.DamageFixedColumn >= 0)
                    {
                        // 空中攻击强化等:身体沿用别的攻击信息,但伤害列仍取 .skl(E-150)
                        strikes.Add(new HeroStrikeInfo(0, 0, false).WithSkillDamage(
                            StageRate(recipe, strikes.Count, def, stage, ScaleLevel(def, slot.Level, stage.DamageRateColumn, powerRate)),
                            ScaleLevel(def, slot.Level, stage.DamageFixedColumn, powerRate)));
                    }
                    else
                    {
                        strikes.Add(new HeroStrikeInfo(0, 0, false));
                    }
                }
                slot.StageStrikes = strikes;
                // 关键帧标志事件换的命中(M65,证据 E-163):攻击信息与两个伤害列里没写的沿用这一段原来的
                var flagStrikes = new List<IReadOnlyList<HeroStrikeInfo?>>();
                foreach (SkillStageSpec stage in recipe.Stages)
                {
                    var perEvent = new List<HeroStrikeInfo?>();
                    foreach (SkillFlagEvent ev in stage.FlagEvents)
                    {
                        int atk = ev.AttackInfoIndex >= 0 ? ev.AttackInfoIndex : stage.AttackInfoIndex;
                        if (!ev.ChangesStrike || atk < 0)
                        {
                            perEvent.Add(null);
                            continue;
                        }
                        int rateColumn = ev.DamageRateColumn >= 0 ? ev.DamageRateColumn : stage.DamageRateColumn;
                        int fixedColumn = ev.DamageFixedColumn >= 0 ? ev.DamageFixedColumn : stage.DamageFixedColumn;
                        perEvent.Add(HeroStrikeInfo.Read(data, chr.CustomAttackInfoPath(atk))
                            .WithSkillDamage(ScaleLevel(def, slot.Level, rateColumn, powerRate), ScaleLevel(def, slot.Level, fixedColumn, powerRate)));
                    }
                    flagStrikes.Add(perEvent);
                }
                slot.FlagStrikes = flagStrikes;
                if (recipe.Hop != null)
                {
                    // 蓄力跳跃类技能:重武装窗口的攻击信息取 [etc attack info] 的另一项,伤害数用跳跃段的两列(证据 E-138)
                    SkillStageSpec jump = recipe.Stages[1];
                    slot.HopRepeatStrike = HeroStrikeInfo.Read(data, chr.CustomAttackInfoPath(recipe.Hop.RepeatAttackInfoIndex))
                        .WithSkillDamage(ScaleLevel(def, slot.Level, jump.DamageRateColumn, powerRate), ScaleLevel(def, slot.Level, jump.DamageFixedColumn, powerRate));
                }
                // 放出的被动对象:攻击信息取对象 .obj 的 [attack info],伤害数取 [level info] 当前等级的两列(证据 E-135)
                var objectStrikes = new List<HeroStrikeInfo>();
                foreach (SkillObjectSpec o in recipe.Objects)
                {
                    Dnf.Game.PassiveObjectDef objectDef = data.GetPassiveObject(o.ObjectId);
                    objectStrikes.Add(HeroStrikeInfo.Read(data, objectDef?.AttackInfoPath)
                        .WithSkillDamage(ScaleLevel(def, slot.Level, o.DamageRateColumn, powerRate), ScaleLevel(def, slot.Level, o.DamageFixedColumn, powerRate)));
                }
                slot.ObjectStrikes = objectStrikes;
                var paths = new List<string>();
                foreach (string rel in effects == null ? Array.Empty<string>() : (IReadOnlyList<string>)effects.EffectsOf(def.Id))
                {
                    paths.Add(PvfPath.ResolveRelativeTo(chr.Path, rel));
                }
                slot.StageEffects = paths;
                return slot;
            }
            ThrowRecipe throwRecipe = (recipes ?? SkillRecipes.Default).FindThrow(def.Id);      // 投掷类技能(状态 0x0d 一族):对象的攻击信息与伤害数、两段姿势动画
            if (throwRecipe != null)
            {
                BuildThrowSlot(slot, data, chr, def, throwRecipe, skillLevelOf, resolveSkill, registeredLinks);
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

        /// <summary>取 [level info] 一列再乘被动威力倍率(E-181);列无效为 0。</summary>
        private static int ScaleLevel(SkillDef def, int level, int column, float powerRate)
        {
            if (def == null || column < 0)
            {
                return 0;
            }
            int raw = def.GetLevel(level, column, false);
            if (Math.Abs(powerRate - 1f) < 1e-5f)
            {
                return raw;
            }
            return (int)Math.Round(raw * powerRate, MidpointRounding.AwayFromZero);
        }

        /// <summary>鬼斩的动作文件名(角色 [etc motion] 里)与攻击信息文件名(角色 [etc attack info] 里)。两个表的下标不同,按名字找。</summary>
        public const string HardAttackMotionFile = "hardattack.ani";

        public const string HardAttackInfoFile = "hardattack.atk";

        /// <summary>鬼斩斩击光影所在的目录(相对角色脚本所在目录)。</summary>
        public const string HardAttackEffectDir = "effect/animation/";

        /// <summary>鬼斩特效放在主角前方多少像素(基线截图与效果图做模板匹配得到:朝左时效果锚点比主角世界 x 再往左 40,纵向没有偏移)。</summary>
        public const int HardAttackEffectForwardX = 40;

        /// <summary>这个技能编号占的槽(先找快捷槽,再找只靠指令放的槽);没有则 null。</summary>
        public HeroSkillSlot SlotOfSkill(int skillId)
        {
            foreach (HeroSkillSlot s in _slots)
            {
                if (s.SkillId == skillId)
                {
                    return s;
                }
            }
            if (_commandSlots != null)
            {
                foreach (HeroSkillSlot s in _commandSlots)
                {
                    if (s.SkillId == skillId)
                    {
                        return s;
                    }
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

        /// <summary>把另一份技能表里同编号技能的剩余冷却带过来(换房间时冷却不清零);空中攻击强化动作的交替模式也跟着带过来(E-150,客户端字段跟着主角对象走)。</summary>
        public void CopyCooldownsFrom(HeroSkills other)
        {
            if (other == null || ReferenceEquals(other, this))
            {
                return;
            }
            JumpAttackMode = other.JumpAttackMode;
            foreach (HeroSkillSlot s in AllSlots())
            {
                foreach (HeroSkillSlot o in other.AllSlots())
                {
                    if (o.SkillId == s.SkillId)
                    {
                        s.CooldownLeftMs = Math.Min(o.CooldownLeftMs, s.CooldownMs);
                    }
                }
            }
        }

        /// <summary>快捷槽加上只靠指令放的槽。</summary>
        private IEnumerable<HeroSkillSlot> AllSlots()
        {
            foreach (HeroSkillSlot s in _slots)
            {
                yield return s;
            }
            foreach (HeroSkillSlot s in _commandSlots)
            {
                yield return s;
            }
        }

        /// <summary>冷却倒数 <paramref name="deltaMs"/> 毫秒。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs <= 0.0)
            {
                return;
            }
            AdvanceCooldowns(_slots, deltaMs);
            AdvanceCooldowns(_commandSlots, deltaMs);
        }

        private static void AdvanceCooldowns(List<HeroSkillSlot> slots, double deltaMs)
        {
            foreach (HeroSkillSlot s in slots)
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

        /// <summary>最近一次 <see cref="TryCastCommand"/> 里有没有哪个技能的指令被打出来(不论后来放没放成)。</summary>
        public bool CommandMatched { get; private set; }

        /// <summary>技能与增益这两个键当扳机(按下就判定);跳跃只在指令多于一个键时当扳机(只有一个跳跃键的指令会和普通跳跃撞车,留给专门的处理);攻击键当扳机的是普攻与冲刺攻击,不在这里。</summary>
        private static bool IsPressTrigger(CommandKey key)
        {
            return key == CommandKey.Skill || key == CommandKey.Buff;
        }

        private static bool IsCommandCastable(HeroSkillSlot s)
        {
            return s.SkillId != SkillIds.QuickStand && IsImplemented(s) && CommandCasts(s.Def?.Command);
        }

        /// <summary>
        /// 这条指令能不能由指令层放出技能:扳机是技能或增益键,或者是跳跃键、攻击键而且指令不止一个键(只有一个跳跃键的指令会和普通跳跃撞车、只有一个攻击键的是普攻,
        /// 都留给专门的处理;攻击键收尾的多键指令如防御姿态的"下、下、攻击"在 M66 起由指令层放,E-164)。
        /// </summary>
        public static bool CommandCasts(SkillCommand c)
        {
            return c != null && !c.IsEmpty && (IsPressTrigger(c.Trigger) || ((c.Trigger == CommandKey.Jump || c.Trigger == CommandKey.Attack) && c.Length >= 2));
        }

        /// <summary>能靠指令放的槽,指令越长越先试(长指令包含短指令,同长度按快捷槽在前、技能编号小的在前)。</summary>
        private List<HeroSkillSlot> CommandOrder()
        {
            if (_commandOrder == null)
            {
                var all = new List<HeroSkillSlot>();
                foreach (HeroSkillSlot s in AllSlots())
                {
                    if (IsCommandCastable(s))
                    {
                        all.Add(s);
                    }
                }
                var ordered = new List<HeroSkillSlot>(all.Count);
                for (int length = 8; length >= 1; length--)
                {
                    foreach (HeroSkillSlot s in all)
                    {
                        if (s.Def.Command.Length == length)
                        {
                            ordered.Add(s);
                        }
                    }
                }
                _commandOrder = ordered;
            }
            return _commandOrder;
        }

        /// <summary>
        /// 按出招指令放技能(E-140):逐个试"打出了自己指令"的槽(指令长的在前),放出来的第一个就是这一步的技能;
        /// 指令打出来了但没放成(冷却、魔法值不够)就接着试后面短一点的指令——例如"上 + 技能"的技能在冷却,同一下按键会落到单键"技能"的技能上(推断)。
        /// 一个都没放出来时,<see cref="CommandMatched"/> 告诉有没有指令被打出来,<see cref="LastCastResult"/> 是最后一个打出指令却没放成的结果
        /// (没有指令被打出来时不改它)。放出来后清空输入历史,同一串按键不会再放第二次。
        /// 方向键的前后按"这一步转身之后"的朝向算:<paramref name="heldX"/> 不为 0 就是主角这一步转向的方向(按左键转向左),为 0 时用主角现在的朝向。
        /// </summary>
        public HeroSkillSlot TryCastCommand(CommandInput input, TownHero hero, HeroVitals vitals = null, int heldX = 0, int heldY = 0)
        {
            CommandMatched = false;
            if (input == null || hero == null)
            {
                return null;
            }
            bool facingLeft = heldX != 0 ? heldX < 0 : hero.FacingLeft;
            foreach (HeroSkillSlot s in CommandOrder())
            {
                if (!input.Matches(s.Def.Command, facingLeft))
                {
                    continue;
                }
                CommandMatched = true;
                if (heldX != 0)
                {
                    hero.TurnTo(heldX < 0);       // 按着方向键放技能:先转向那边(与走路、跳跃、普攻一致),技能朝着按的方向出
                }
                SkillCastResult result = Cast(s, hero, vitals, heldX, heldY);
                LastCastResult = result;
                if (result == SkillCastResult.Cast)
                {
                    input.Clear();
                    return s;
                }
            }
            return null;
        }

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
                    began = slot.Recipe != null
                        ? (slot.Recipe.Dive != null ? BeginDive(slot, hero)
                            : (slot.Recipe.JumpAttack != null ? BeginJumpAttack(slot, hero) : BeginStage(slot, 0, hero, heldX, heldY, false, MultiHitWindows(slot))))
                        : slot.Throw != null ? BeginThrow(slot, hero)      // 投掷类:原地摆阶段 0 的姿势,对象与霸体由房间会话放(状态 0x0d 一族)
                        : slot.Guard != null ? hero.BeginGuard(slot.SkillId, slot.Guard.MotionIndex, slot.GuardMinHoldMs)   // 防御姿态类:原地摆姿势、动画定在第 0 帧,按住攻击键维持(M66,E-164)
                        : slot.Buff != null && hero.BeginBuffMotion(slot.SkillId);   // 增益类:原地播 [buff motion],施法正常结束时才挂增益(E-141,由房间会话挂)
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
            if (slot?.Recipe == null || (pressed & slot.Key) == 0)
            {
                return null;
            }
            return ChainSlot(slot, hero, heldX, heldY);
        }

        /// <summary>
        /// 同 <see cref="TryChain"/>,只是"再按一下技能键"按出招指令算:主角正在放的配方技能带指令时,这一步按下了它指令的扳机键就接下一段
        /// (只看扳机,不要求把整串指令再打一遍——客户端逐帧代码查的是技能键是否按下,指令在起手时已经判过,推断,E-140)。
        /// 只认技能与增益两种扳机。
        /// </summary>
        public HeroSkillSlot TryChainCommand(CommandInput input, TownHero hero, int heldX = 0, int heldY = 0)
        {
            if (input == null || hero == null || hero.Motion != HeroMotion.Skill || hero.SkillWindingUp)
            {
                return null;
            }
            HeroSkillSlot slot = SlotOfSkill(hero.SkillId);
            SkillCommand command = slot?.Def?.Command;
            if (slot?.Recipe == null || command == null || command.IsEmpty || !IsPressTrigger(command.Trigger) || !input.JustPressed(command.Trigger))
            {
                return null;
            }
            return ChainSlot(slot, hero, heldX, heldY);
        }

        private HeroSkillSlot ChainSlot(HeroSkillSlot slot, TownHero hero, int heldX, int heldY)
        {
            SkillRecipe recipe = slot.Recipe;
            if (recipe.Hop != null)
            {
                return null;           // 蓄力跳跃类技能:两段之间按"松开或蓄满"自动衔接,再按技能键不接段(证据 E-138)
            }
            if (recipe.AutoChain)
            {
                return null;           // 自动接续类技能:每一段播完自己接下一段,不看按键(证据 E-154)
            }
            if (recipe.BodyHit != null && recipe.BodyHit.Chains.Count > 0)
            {
                return null;           // 有按键接段表的配方:只走 TryChainBody(再按自己的键不一定接段)
            }
            if (!KeyChainsFrom(recipe, hero.SkillStage))
            {
                return null;           // 这一段标了"播完自己接"或"不许按键接"(状态 0x27 的起手段与被锁的打击段)
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
            return BeginStage(slot, next, hero, heldX, heldY, true, MultiHitWindows(slot)) ? slot : null;
        }

        /// <summary>
        /// 自动接段(证据 E-154;<see cref="TownHero.SkillFollowUp"/> 的实现):主角正在放一个自动接续的配方技能(<see cref="SkillRecipe.AutoChain"/>),
        /// 这一段的动画播完、本来要结束技能的那一刻问到这里。后面还有一段、那一段要求的技能也学过,就换成下一段(新动画从头播、新的一击)并返回 true;
        /// 否则(没有下一段、不是自动接续的技能、没在放主体阶段)返回 false,技能照常结束。接段不扣蓝、不进冷却:魔法值与冷却在起手那一刻已经算过。
        /// </summary>
        public bool FollowUp(TownHero hero)
        {
            if (hero == null || hero.Motion != HeroMotion.Skill || hero.SkillWindingUp)
            {
                return false;
            }
            HeroSkillSlot slot = SlotOfSkill(hero.SkillId);
            SkillRecipe recipe = slot?.Recipe;
            if (recipe == null || !(recipe.AutoChain || AutoAdvancesFrom(recipe, hero.SkillStage)))
            {
                return false;
            }
            int next = hero.SkillStage + 1;
            if (next >= recipe.Stages.Count || !SkillBodyHitRules.AutoChainAllowed(recipe.BodyHit, hero.SkillStage))
            {
                return false;
            }
            int need = recipe.Stages[next].RequiresSkill;
            if (need != 0 && (SkillLevelOf == null || SkillLevelOf(need) <= 0))
            {
                return false;
            }
            return BeginStage(slot, next, hero, 0, 0, true, MultiHitWindows(slot));
        }

        /// <summary>
        /// 这个技能这一次放出来的多段命中次数(证据 E-139):配方标了多段命中的被动技能、主角学过它(等级大于 0)、
        /// [static data] 里取得到次数且次数大于 0 时返回这个次数,否则 0(只命中一轮)。客户端还要求不在 PVP 模式,这里没有 PVP。
        /// </summary>
        private int MultiHitWindows(HeroSkillSlot slot)
        {
            SkillRecipe r = slot.Recipe;
            if (r == null || r.MultiHitPassiveSkillId <= 0 || r.MultiHitCountIndex < 0 || slot.Def == null
                || SkillLevelOf == null || SkillLevelOf(r.MultiHitPassiveSkillId) <= 0)
            {
                return 0;
            }
            return Math.Max(0, slot.Def.GetStatic(r.MultiHitCountIndex, false));
        }

        private static bool BeginStage(HeroSkillSlot slot, int stage, TownHero hero, int heldX, int heldY, bool chain, int multiHitWindows = 0)
        {
            SkillStageSpec spec = slot.Recipe.Stages[stage];
            Slide(slot, hero, heldX, heldY, out double speed, out double accel);
            if (!spec.Slides || !SkillBodyHitRules.SlidesInStage(slot.Recipe.BodyHit, stage))
            {
                speed = 0;        // 这一段的进入块把滑行清零,不带配方上的起滑(状态 0x25 阶段 1、状态 0x27 阶段 0;身体命中一族只有首段起滑;四段连击技能的前三段,M105)
                accel = 0;
            }
            int hold = CastHoldMs(slot, spec, hero);
            if (!chain && stage == 0 && slot.Recipe.Hop != null)
            {
                hold = HopHoldMs(slot);
            }
            SkillRisePlan rise = RisePlanOf(slot, spec);
            if (rise != null)
            {
                // 升空段:动画第 0 帧(带攻击框)停到升空时间结束(动画时间 = 墙钟 × 播放倍率,向上取整保证墙钟不短于升空时间),之后接着播剩下的帧
                hold = (int)Math.Ceiling(rise.DurationMs * (hero.AttackAnimationRate > 0.0 ? hero.AttackAnimationRate : 1.0));
            }
            SkillLeapPlan leap = LeapPlanOf(slot, spec);
            bool began = chain
                ? hero.ChainSkillMotion(slot.SkillId, spec.MotionIndex, speed, accel, hold, spec.EndFrame, rearmWindows: multiHitWindows, rise: rise, leap: leap)
                : hero.BeginSkillMotion(slot.SkillId, spec.MotionIndex, speed, accel, hold, spec.EndFrame, multiHitWindows, rise, leap);
            if (began && spec.Warp != null)
            {
                ApplyWarp(slot, spec.Warp, hero);
            }
            return began;
        }

        /// <summary>
        /// 这一段的脚本升空计划(M65,证据 E-163):高度、升空毫秒、后滑像素都取 [static data] 的下标;这一段不是升空段、读不到技能定义时返回 null。
        /// </summary>
        internal static SkillRisePlan RisePlanOf(HeroSkillSlot slot, SkillStageSpec spec)
        {
            SkillRiseSpec r = spec.Rise;
            if (r == null || slot.Def == null)
            {
                return null;
            }
            SkillDef def = slot.Def;
            return new SkillRisePlan(def.GetStatic(r.HeightStaticIndex, false), def.GetStatic(r.DurationStaticIndex, false), def.GetStatic(r.BackStaticIndex, false));
        }

        /// <summary>
        /// 蓄力姿势的动画至少要播多久(毫秒):最长蓄力时间再加一秒余量——姿势动画的第 0 帧停留不能比蓄力先结束,
        /// 蓄力什么时候结束由会话看按键决定(<see cref="BeginHop"/>)。
        /// </summary>
        internal static int HopHoldMs(HeroSkillSlot slot)
        {
            SkillHopSpec hop = slot.Recipe.Hop;
            int max = slot.Def == null ? 0 : slot.Def.GetStatic(hop.HoldMaxIndex, false);
            return Math.Max(0, max) + 1000;
        }

        /// <summary>
        /// 蓄力跳跃类技能出招(证据 E-138):按蓄力档位 <paramref name="charge"/>(0 到 100)在 [static data] 的各对数之间插值出距离、高度、
        /// 动画变速与命中窗口数,把蓄力姿势换成跳跃段(新的一击、命中窗口重新数),跳跃由 <see cref="TownHero"/> 按动画时间脚本化。
        /// 主角不在放这个技能的蓄力段、或读不到跳跃动画时返回 false。
        /// </summary>
        internal static bool BeginHop(HeroSkillSlot slot, TownHero hero, int charge)
        {
            SkillRecipe recipe = slot.Recipe;
            SkillHopSpec hop = recipe?.Hop;
            if (hop == null || slot.Def == null || hero.SkillStage != 0)
            {
                return false;
            }
            SkillDef def = slot.Def;
            int distance = SkillHopPlan.IntLerp(def.GetStatic(hop.DistanceMinIndex, false), def.GetStatic(hop.DistanceMaxIndex, false), charge, 100);
            int height = SkillHopPlan.IntLerp(def.GetStatic(hop.HeightMinIndex, false), def.GetStatic(hop.HeightMaxIndex, false), charge, 100);
            int hits = SkillHopPlan.IntLerp(def.GetStatic(hop.HitsMinIndex, false), def.GetStatic(hop.HitsMaxIndex, false), charge, 100);
            float rate = SkillHopPlan.FloatLerp(def.GetStatic(hop.SpeedMinIndex, false) * 0.01f, def.GetStatic(hop.SpeedMaxIndex, false) * 0.01f, charge, 100);
            var plan = new SkillHopPlan(distance, height, hits, hop.RiseFrame, hop.LandFrame, hop.HitStartFrame, hop.HitEndFrame);
            SkillStageSpec jump = recipe.Stages[1];
            return hero.ChainSkillMotion(slot.SkillId, jump.MotionIndex, 0.0, 0.0, -1, jump.EndFrame, plan, rate);
        }

        /// <summary>
        /// 腾空悬停后俯冲类技能起手(证据 E-149):主角正在腾空的跳跃状态里才放得出来(在地面、起跳蹲姿、落地段、空中挥击、后跳都不行,返回 false,
        /// 不扣蓝、不进冷却)。悬停毫秒、水平与竖直速度取配方,落地放对象所需的最小离地高度取 [static data] 的第 <see cref="SkillDiveSpec.ImpactHeightIndex"/> 个数
        /// (读不到技能定义时取最大值,等于不放)。
        /// </summary>
        internal static bool BeginDive(HeroSkillSlot slot, TownHero hero)
        {
            SkillDiveSpec dive = slot.Recipe?.Dive;
            if (dive == null)
            {
                return false;
            }
            int minHeight = slot.Def == null ? int.MaxValue : slot.Def.GetStatic(dive.ImpactHeightIndex, false);
            return hero.BeginSkillDive(slot.SkillId, new SkillDivePlan(dive.HoverMs, dive.SpeedX, dive.SpeedZ, minHeight));
        }

        /// <summary>
        /// "上一次放的是哪种空中攻击强化动作"(证据 E-150,客户端主角字段):-1 没放过、0 刚放了第一种、1 刚放了第二种。
        /// 下一次放的种类:这个字段为 0 就放第二种,否则放第一种(所以两种交替);只在放出来(<see cref="Cast"/> 或攻击键接管)时更新。
        /// 客户端的字段跟着主角对象走、跨跳跃跨房间保留,所以换房间时带过去(<see cref="CopyCooldownsFrom"/>)。
        /// </summary>
        public int JumpAttackMode { get; private set; } = -1;

        private int NextJumpAttackVariant()
        {
            return JumpAttackMode == 0 ? 1 : 0;
        }

        /// <summary>
        /// 空中攻击强化类技能起手(快捷键放,证据 E-150):只在腾空的跳跃状态里、这一跳还没用过空中攻击时放得出来(返回 false,不扣蓝、不进冷却、不翻交替模式)。
        /// 放出来的动作按交替模式取配方的第一种或第二种。
        /// </summary>
        private bool BeginJumpAttack(HeroSkillSlot slot, TownHero hero)
        {
            SkillJumpAttackSpec spec = slot.Recipe?.JumpAttack;
            if (spec == null)
            {
                return false;
            }
            int variant = NextJumpAttackVariant();
            if (!hero.BeginJumpAttackSkill(slot.SkillId, variant == 0 ? spec.MotionIndexA : spec.MotionIndexB))
            {
                return false;
            }
            JumpAttackMode = variant;
            return true;
        }

        /// <summary>这条指令正好是单独一个攻击键(攻击键当扳机、前面没有别的键)。</summary>
        private static bool IsAttackKeyOnly(SkillCommand c)
        {
            return c != null && !c.IsEmpty && c.Length == 1 && c.Trigger == CommandKey.Attack;
        }

        /// <summary>
        /// 学过的、指令是单独攻击键的空中攻击强化技能(先找快捷槽,再找只靠指令放的槽);没有返回 null。攻击键在空中按下时由它接管普通的空中攻击。
        /// </summary>
        public HeroSkillSlot AttackKeyJumpSkill()
        {
            foreach (HeroSkillSlot s in AllSlots())
            {
                if (s.Recipe?.JumpAttack != null && s.Level > 0 && IsAttackKeyOnly(s.Def?.Command))
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>
        /// 攻击键在空中按下、普通的空中攻击要开始的那一刻,看有没有强化技能可以接管(证据 E-150;<see cref="TownHero.TakeOverJumpAttack"/> 的实现):
        /// 技能学过、冷却好了、魔法值够(没给 <paramref name="vitals"/> 当够)、要播的动作读得到,就扣魔法值、进冷却、翻交替模式,返回 true 并给出技能号与动作下标;
        /// 否则返回 false,这一击仍是普通的空中攻击(魔法值不够时退回普通攻击是推断:客户端的"可用"检查在受保护区里)。
        /// </summary>
        public bool TakeOverJumpAttack(TownHero hero, HeroVitals vitals, out int skillId, out int etcIndex)
        {
            skillId = 0;
            etcIndex = -1;
            HeroSkillSlot slot = hero == null ? null : AttackKeyJumpSkill();
            if (slot == null || !slot.Ready || (vitals != null && (!vitals.IsAlive || vitals.Mp < slot.ConsumeMp)))
            {
                return false;
            }
            SkillJumpAttackSpec spec = slot.Recipe.JumpAttack;
            int variant = NextJumpAttackVariant();
            int etc = variant == 0 ? spec.MotionIndexA : spec.MotionIndexB;
            if (!hero.HasEtcMotion(etc))
            {
                return false;
            }
            vitals?.TrySpendMp(slot.ConsumeMp);
            slot.CooldownLeftMs = slot.CooldownMs;
            JumpAttackMode = variant;
            skillId = slot.SkillId;
            etcIndex = etc;
            return true;
        }

        /// <summary>
        /// 施法蓄力要停多久(毫秒,动画时间):施法时间(.skl [casting time] 按等级取)减去动作第 1 到第 n 帧的时长,不小于 0(证据 E-135,静态读码);
        /// 客户端最后还按施法速度(千分比)缩放,这里没有施法速度属性,按 1000 算。这一段没有蓄力返回 -1(动画照帧时长走)。
        /// 标了 <see cref="SkillStageSpec.CastHoldWholeTime"/> 的段不减任何帧(下面的循环一次也不走),蓄力就是整段施法时间。
        /// </summary>
        internal static int CastHoldMs(HeroSkillSlot slot, SkillStageSpec spec, TownHero hero)
        {
            if (spec.CastMode != SkillCastMode.Placeholder)
            {
                return CastModeHoldMs(slot, spec, hero);
            }
            if (spec.CastHoldFrames <= 0 && !spec.CastHoldWholeTime)
            {
                return -1;
            }
            int castMs = slot.Def == null ? 0 : slot.Def.Interpolate(slot.Def.CastingTime, slot.Level);
            for (int frame = 1; frame <= spec.CastHoldFrames; frame++)
            {
                castMs -= hero.EtcFrameDelayMs(spec.MotionIndex, frame);
            }
            return Math.Max(0, castMs);
        }

        /// <summary>动画帧数的上限(<see cref="CastModeHoldMs"/> 累加各帧时长时的循环界;帧号越界的时长是 0,所以界只要不小于真实帧数即可)。</summary>
        private const int MaxMotionFrames = 256;

        /// <summary>
        /// 另两种施法时间写法的第 0 帧停留(M105,<see cref="SkillCastMode"/>,静态读码):施法时间(.skl [casting time] 按等级插值)不大于 0 时返回 -1(动画照原样走);
        /// <see cref="SkillCastMode.Stretch"/> = max(0, 施法时间 − 第 1 帧起各帧时长之和);<see cref="SkillCastMode.Pause"/> = 施法时间 + 第 0 帧自己的时长;
        /// <see cref="SkillCastMode.StaticHold"/> = [static data] 指定那一项的毫秒数(不看施法时间,取不到那一项时按 0;配方没写下标或技能数据缺失返回 -1)。
        /// 客户端最后还按施法速度缩放,这里没有施法速度属性,按 1000 算(同 <see cref="CastHoldMs"/>)。
        /// </summary>
        private static int CastModeHoldMs(HeroSkillSlot slot, SkillStageSpec spec, TownHero hero)
        {
            if (spec.CastMode == SkillCastMode.StaticHold)
            {
                return slot.Def == null || spec.HoldStaticIndex < 0 ? -1 : Math.Max(0, slot.Def.GetStatic(spec.HoldStaticIndex, false));
            }
            int castMs = slot.Def == null ? 0 : slot.Def.Interpolate(slot.Def.CastingTime, slot.Level);
            if (castMs <= 0)
            {
                return -1;
            }
            if (spec.CastMode == SkillCastMode.Pause)
            {
                return castMs + hero.EtcFrameDelayMs(spec.MotionIndex, 0);
            }
            for (int frame = 1; frame < MaxMotionFrames; frame++)
            {
                castMs -= hero.EtcFrameDelayMs(spec.MotionIndex, frame);
            }
            return Math.Max(0, castMs);
        }

        /// <summary>
        /// 进入一段那一刻的起滑速度与加速度(证据 E-129):没按方向键取基础的两个数;按着朝向一侧的方向键,再加上"顺着按"的加成;
        /// 按着别的方向(反向、只按上下)就不滑。同时按了左右与上下时只看左右(原版怎么选没读到,按横向优先推断)。
        /// 配方标了 <see cref="SkillRecipe.SlideFollowsHeldDirection"/> 为 false 时不看方向键,一律取基础的两个数;
        /// 标了 <see cref="SkillRecipe.ForwardReplacesBase"/> 时,顺着按是把基础的两个数整个换成"顺着按"的两个数(证据 E-139)。
        /// </summary>
        internal static void Slide(HeroSkillSlot slot, TownHero hero, int heldX, int heldY, out double speed, out double accel)
        {
            SkillRecipe r = slot.Recipe;
            SkillDef def = slot.Def;
            speed = r.SlideSpeedIndex >= 0 && def != null ? def.GetStatic(r.SlideSpeedIndex, false) : 0;
            accel = r.SlideAccelIndex >= 0 && def != null ? def.GetStatic(r.SlideAccelIndex, false) : 0;
            if (!r.SlideFollowsHeldDirection)
            {
                return;   // 状态进入块里不读按住的方向:每一段都按基础的两个数朝主角的朝向滑(证据 E-131)
            }
            if (r.BodyHit != null && r.BodyHit.HorizontalHeldSlide)
            {
                SkillBodyHitRules.HeldSlide(speed, accel, r.ForwardSpeedIndex >= 0 && def != null,
                    r.ForwardSpeedIndex >= 0 && def != null ? def.GetStatic(r.ForwardSpeedIndex, false) : 0, heldX, hero.FacingLeft, out speed, out accel);
                return;
            }
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
            if (r.ForwardReplacesBase)
            {
                // 状态 0x1d 的进入块(证据 E-139):顺着按时起滑速度与加速度整个换成"顺着按"的两个数,没有的下标当 0
                speed = r.ForwardSpeedIndex >= 0 && def != null ? def.GetStatic(r.ForwardSpeedIndex, false) : 0;
                accel = r.ForwardAccelIndex >= 0 && def != null ? def.GetStatic(r.ForwardAccelIndex, false) : 0;
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

    /// <summary>空中攻击强化取缩放后的 level 列(E-150/E-181);供 RoomCombat 与单测共用。</summary>
    public static class HeroJumpStrikeResolve
    {
        /// <summary>把基础空中攻击攻击信息换成技能列;无 stage 列时退回 <paramref name="baseJump"/>。</summary>
        public static HeroStrikeInfo Apply(HeroStrikeInfo baseJump, HeroSkillSlot slot)
        {
            if (slot?.Recipe?.JumpAttack == null || slot.Def == null)
            {
                return baseJump;
            }
            HeroStrikeInfo? strike = slot.StrikeAtWithPassiveNativeState(0, SkillPassivePowerRate.JumpAttackNativeState);
            if (strike == null || !strike.Value.IsSkillDamage)
            {
                return baseJump;
            }
            return baseJump.WithSkillDamage(strike.Value.SkillRatePercent, strike.Value.SkillFixed);
        }
    }
}
