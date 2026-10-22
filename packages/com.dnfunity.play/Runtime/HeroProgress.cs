using System;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

// 证据:E-111(等级、经验、升级与等级成长;各部分的服务端读法见 Dnf.Game 的 Progress 目录)。
namespace Dnf.Play
{
    /// <summary>
    /// 主角的等级与经验(证据 E-111):升级经验表、技能点表、倍率表、基础经验表与角色成长表都是只读的数据,
    /// 变的只有等级、累计经验与升级得到的点数。<see cref="Gain"/> 走服务端的升级循环(<see cref="Leveling"/>),
    /// <see cref="Stats"/> 给出这个等级的属性(<see cref="CharacterGrowth"/>:初始值加上每升一级的增量;单位是十分之一)。
    /// 一个对象跟着主角走完整个流程:副本里每个房间的战斗会话共用同一个(房间换了、副本换了,等级与经验都留着)。
    /// 没做:觉醒转职(<see cref="Awakening"/> 恒为 0,50 级以后用成长类型本身的块)、成长类型的选择(恒为第 0 类,新角色的缺省)、
    /// 属性点与装备对属性的其余加成、技能点的花费(<see cref="SkillPoints"/> 只累计)。
    /// </summary>
    public sealed class HeroProgress
    {
        /// <summary>新角色的成长类型(服务端缺省 0;转职前一直用第一个成长类型的增量)。</summary>
        public const int DefaultGrowtype = 0;

        public ExpTable Table { get; }
        public SpTable Sp { get; }
        public CharacterGrowth Growth { get; }
        public MobRewardTable Rewards { get; }
        public ExpRates Rates { get; }

        public int Growtype { get; }

        /// <summary>觉醒下标(0 = 没觉醒;1、2 对应 [awakening 1]、[awakening 2] 块)。</summary>
        public int Awakening { get; }

        public int Level { get; private set; }

        /// <summary>累计经验(升级之后等于新等级的门槛再加上滚下来的余量)。</summary>
        public int Exp { get; private set; }

        /// <summary>从创建起一共加过多少经验(含升级时被吞掉的封顶余量之前的全部原始增量)。</summary>
        public long TotalGained { get; private set; }

        /// <summary>从创建起一共涨过几级。</summary>
        public int LevelUps { get; private set; }

        /// <summary>升级得到的技能点累计(没有花费)。</summary>
        public int SkillPoints { get; private set; }

        /// <summary>升级得到的觉醒点累计(新等级 ≥ 50 的每一级一点)。</summary>
        public int AwakeningPoints { get; private set; }

        /// <summary>等级与经验的变化次数:各个房间的战斗会话据此发现"别的房间里升了级",把自己的生命上限与攻击面板换成新等级的。</summary>
        public int Version { get; private set; }

        public HeroProgress(ExpTable table, SpTable sp, CharacterGrowth growth, MobRewardTable rewards, ExpRates rates, int level = 1, int exp = -1,
            int growtype = DefaultGrowtype, int awakening = 0)
        {
            Table = table ?? throw new ArgumentNullException(nameof(table));
            Sp = sp ?? new SpTable(new int[0]);
            Growth = growth ?? CharacterGrowth.Parse(null);
            Rewards = rewards ?? MobRewardTable.Placeholder(table);
            Rates = rates ?? ExpRates.Neutral;
            Growtype = growtype < 0 ? 0 : growtype;
            Awakening = awakening < 0 ? 0 : awakening;
            Level = level < 1 ? 1 : level;
            Exp = exp >= 0 ? exp : table.LevelExp(Level);
        }

        /// <summary>等级是否已经到了经验能涨到的上限(<see cref="Leveling.MaxLevel"/>)。</summary>
        public bool IsCapped => Level >= Leveling.MaxLevel;

        /// <summary>在本级里已经攒的经验(累计经验减去本级的门槛;经验条的分子,推断,见 HudArt)。</summary>
        public int ExpInLevel => unchecked(Exp - Table.LevelExp(Level));

        /// <summary>本级一共要攒的经验(下一级的门槛减去本级的门槛;经验条的分母,推断);表走到头时不大于 0。</summary>
        public int LevelSpan => Table.Section(Level);

        /// <summary>这个等级的属性(十分之一单位的整数,<see cref="CharacterGrowth.StatsAt"/>)。</summary>
        public StatBlock Stats => Growth.StatsAt(Level, Growtype, Awakening);

        /// <summary>
        /// 加经验并按服务端的循环升级。返回这一次的结果(涨了几级、给了多少点)。
        /// <paramref name="exp"/> 不大于 0 什么都不变(也不算一次变化)。
        /// </summary>
        public ExpGainResult Gain(int exp)
        {
            if (exp <= 0)
            {
                return new ExpGainResult(Level, Exp, 0, 0, 0);
            }
            ExpGainResult r = Leveling.Gain(Table, Sp, Level, Exp, exp);
            TotalGained += exp;
            if (r.Level != Level || r.Exp != Exp)
            {
                Version++;
            }
            Level = r.Level;
            Exp = r.Exp;
            LevelUps += r.LevelsGained;
            SkillPoints += r.SkillPoints;
            AwakeningPoints += r.AwakeningPoints;
            return r;
        }

        /// <summary>直接设成某个等级(经验设成这一级的门槛;测试与 --level 之类的开关用),不给点数。</summary>
        public void SetLevel(int level)
        {
            int clamped = level < 1 ? 1 : level;
            if (clamped == Level && Exp == Table.LevelExp(clamped))
            {
                return;
            }
            Level = clamped;
            Exp = Table.LevelExp(clamped);
            Version++;
        }

        /// <summary>
        /// 读真实数据造一个主角的进度:升级经验表、技能点表、倍率表来自数据(缺文件时是空表或中性倍率),
        /// 基础经验表优先用用户导出的(<see cref="MobRewards"/>),没有就用占位表。
        /// </summary>
        public static HeroProgress Create(GameData data, CharacterDef chr, MobRewardTable rewards, int level = 1, int exp = -1)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            ExpTable table = data.ExpTable;
            return new HeroProgress(table, data.SpTable, data.GetGrowth(chr), rewards ?? MobRewardTable.Placeholder(table), data.ExpRates, level, exp);
        }

        /// <summary>
        /// 为本地存档的默认主角(0 号角色,与 <see cref="RoomCombatSession.Create"/> 用的是同一个)造进度:读数据里的各张表,
        /// 基础经验表按 <see cref="MobRewards.Load"/> 的顺序找。读不到这个角色的定义时返回 null(杀怪就不给经验)。
        /// </summary>
        public static HeroProgress CreateForLocalHero(DnfAssets assets, int level = 1, int exp = -1)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            var data = new GameData(new PvfScriptSource(assets.Pvf));
            CharacterDef chr = data.GetCharacter(0);
            if (chr == null)
            {
                return null;
            }
            return Create(data, chr, MobRewards.Load(assets, data.ExpTable), level, exp);
        }
    }
}
