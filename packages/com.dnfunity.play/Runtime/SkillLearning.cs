using System;
using System.Collections.Generic;
using Dnf.Game;

// 证据:E-127(学、退、重置的次序与"批量购买全成全不成":服务端静态读码,规则见 docs/flow.md 第 21 节)。
// 本项目自己的设计(推断,非 REA):批量里后一步看得见前一步的结果、失败时报第几步、快捷栏上只能靠快捷键的技能优先占栏(见 SkillLearning.Bar.cs)、升级之后自动学的触发时机。
namespace Dnf.Play
{
    /// <summary>批量里的一步:要学(或退)的技能编号与级数。</summary>
    public readonly struct SkillStep
    {
        public SkillStep(int skillId, int levels)
        {
            SkillId = skillId;
            Levels = levels;
        }

        public int SkillId { get; }

        /// <summary>学(或退)几级;小于 1 的步一律不成(<see cref="SkillOpResult.Unknown"/>,与 <see cref="SkillBook.CheckLearn"/> 的增量检查一致)。</summary>
        public int Levels { get; }
    }

    /// <summary>
    /// 一批学(或退)的结果:要么全成(<see cref="Result"/> 是 <see cref="SkillOpResult.Ok"/>、<see cref="FailedIndex"/> 是 -1),
    /// 要么一步也没动(<see cref="FailedIndex"/> 是第一个不成的那一步的下标,<see cref="Result"/> 是它的原因)。
    /// </summary>
    public readonly struct SkillBatchResult
    {
        public SkillBatchResult(SkillOpResult result, int failedIndex, int sp, int sfp)
        {
            Result = result;
            FailedIndex = failedIndex;
            Sp = sp;
            Sfp = sfp;
        }

        public SkillOpResult Result { get; }

        /// <summary>第一个不成的步的下标;全成是 -1。</summary>
        public int FailedIndex { get; }

        /// <summary>技能点池一共动了多少(学是花掉的、退是还回的,都不为负;失败为 0)。</summary>
        public int Sp { get; }

        /// <summary>特殊点(觉醒点)池一共动了多少(同 <see cref="Sp"/>)。</summary>
        public int Sfp { get; }

        public bool Ok => Result == SkillOpResult.Ok;

        /// <summary><see cref="Result"/> 的枚举名(宿主层不引用游戏数据层,用名字交接)。</summary>
        public string ResultName => Result.ToString();
    }

    /// <summary>一个技能现在能靠什么放出来(<see cref="SkillLearning.ReachOf"/>)。</summary>
    public enum SkillReach
    {
        /// <summary>没学会(或不认识)。</summary>
        NotLearned = 0,

        /// <summary>学会了,是被动技能:不用放。</summary>
        Passive = 1,

        /// <summary>在快捷栏里:按快捷键放;出招指令能放的话也能靠指令放(指令与快捷键共用同一个槽,E-140)。</summary>
        OnBar = 2,

        /// <summary>不在快捷栏里,但靠出招指令放得出来(<see cref="HeroSkills.CommandCasts"/> 且放法实现了)。</summary>
        CommandOnly = 3,

        /// <summary>不在快捷栏里,指令也放不出来(没有指令、指令只是普攻或普通跳跃的写法,或放法没实现):现在放不出来。</summary>
        Unreachable = 4,
    }

    /// <summary>
    /// 技能学习服务(M35 之后的整理,证据 E-127):把"学一个、退一个、重置、一次学一批、自动学"串成一个客户端可以调用的规则入口。
    /// 单个技能的规则(学的判定次序、点数怎么扣怎么还、白送技能是退的下限、被依赖的技能退不破)都在 <see cref="SkillBook"/> 与 <see cref="HeroProgress"/> 里,
    /// 这里一条也不重写,只委托;这一层补的是它们之间的缺口:
    /// 1. 批量:<see cref="LearnAll"/>、<see cref="ForgetAll"/> 整批要么全成要么全不成(服务端"批量购买全成全不成"),后一步看得见前一步的结果,
    ///    失败时说明是第几步、什么原因,并且一点痕迹不留(演算在副本上做,通过之后一次换进去);<see cref="TryLearnAll"/>、<see cref="TryForgetAll"/> 只演算不动手;
    /// 2. 一次能学几级:<see cref="LearnableLevels"/>、<see cref="LearnMax"/>(成长类型最高级、等级间隔、点数、前置技能四条合在一起);
    /// 3. 靠指令放与快捷栏占用的次序:<see cref="ReachOf"/>、<see cref="CommandAware"/>(见 SkillLearning.Bar.cs);
    /// 4. 自动学:<see cref="AutoLearn"/> 打开后,每次升级之后 <see cref="Update"/> 把新得到的点按 <see cref="SkillPlanner"/> 花光。
    /// 宿主层(Unity 程序集不引用 Dnf.Game)用 <see cref="SkillLearn"/> 的名字串入口。
    /// 一个服务跟着一个主角进度;开关(<see cref="CommandAware"/>、<see cref="AutoLearn"/>)都默认关,关着时与直接调用 <see cref="HeroProgress"/> 完全一样。
    /// </summary>
    public sealed partial class SkillLearning
    {
        private readonly HeroProgress _hero;
        private bool _autoLearn;
        private int _seenLevelUps;

        public SkillLearning(HeroProgress hero)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
            _seenLevelUps = hero.LevelUps;
        }

        public HeroProgress Hero => _hero;

        // ---------------- 单个(委托) ----------------

        /// <summary>检查能不能把技能学高 <paramref name="levels"/> 级,不动任何状态;<paramref name="cost"/> 是要花的点数(不成为 0)。规则见 <see cref="SkillBook.CheckLearn"/>。</summary>
        public SkillOpResult CheckLearn(int skillId, int levels, out int cost)
        {
            return _hero.Skills.CheckLearn(skillId, _hero.Level, _hero.Growtype, levels, out cost);
        }

        /// <summary>学高 <paramref name="levels"/> 级(规则见 <see cref="HeroProgress.LearnSkill"/>);<see cref="CommandAware"/> 开着时学成之后整理快捷栏。</summary>
        public SkillOpResult Learn(int skillId, int levels, out int spent)
        {
            SkillOpResult result = _hero.LearnSkill(skillId, levels, out spent);
            if (result == SkillOpResult.Ok && CommandAware)
            {
                ArrangeBar();
            }
            return result;
        }

        /// <summary>检查能不能退掉 <paramref name="levels"/> 级,不动任何状态;<paramref name="refund"/> 是会还回的点数(不成为 0)。规则见 <see cref="SkillBook.CheckRefund"/>。</summary>
        public SkillOpResult CheckForget(int skillId, int levels, out int refund)
        {
            return _hero.Skills.CheckRefund(skillId, levels, out refund);
        }

        /// <summary>退掉 <paramref name="levels"/> 级(规则见 <see cref="HeroProgress.RefundSkill"/>);<see cref="CommandAware"/> 开着时退成之后整理快捷栏(退出来的空格补给够不着的技能)。</summary>
        public SkillOpResult Forget(int skillId, int levels, out int refunded)
        {
            SkillOpResult result = _hero.RefundSkill(skillId, levels, out refunded);
            if (result == SkillOpResult.Ok && CommandAware)
            {
                ArrangeBar();
            }
            return result;
        }

        /// <summary>重置全部技能(规则见 <see cref="HeroProgress.ResetSkills"/>);<see cref="CommandAware"/> 开着时重置之后整理快捷栏。</summary>
        public void Reset()
        {
            _hero.ResetSkills();
            if (CommandAware)
            {
                ArrangeBar();
            }
        }

        // ---------------- 批量:全成全不成 ----------------

        /// <summary>只演算不动手:这一批学法能不能全成。结果与 <see cref="LearnAll"/> 一模一样,主角的状态一点不变(<see cref="HeroProgress.SkillsVersion"/> 也不加)。</summary>
        public SkillBatchResult TryLearnAll(IReadOnlyList<SkillStep> steps)
        {
            return Simulate(steps, true, out _);
        }

        /// <summary>
        /// 一批学法(按给定的次序逐步演算,后一步看得见前一步学到的等级与花掉的点数,所以"先学前置技能、再学依赖它的技能"写在同一批里能成,次序反了就不成)。
        /// 每一步的判定与 <see cref="SkillBook.Learn"/> 一样。全成才一次换进去(<see cref="HeroProgress.SkillsVersion"/> 只加一);
        /// 有一步不成就整批不成,<see cref="SkillBatchResult.FailedIndex"/> 是第一个不成的步,状态一点不变。空批什么也不做、算成功。
        /// </summary>
        public SkillBatchResult LearnAll(IReadOnlyList<SkillStep> steps)
        {
            SkillBatchResult result = Simulate(steps, true, out SkillBook after);
            if (result.Ok && steps.Count > 0)
            {
                Commit(after);
            }
            return result;
        }

        /// <summary>只演算不动手:这一批退法能不能全成(见 <see cref="ForgetAll"/>)。</summary>
        public SkillBatchResult TryForgetAll(IReadOnlyList<SkillStep> steps)
        {
            return Simulate(steps, false, out _);
        }

        /// <summary>
        /// 一批退法(次序规则同 <see cref="LearnAll"/>:先退依赖别人的技能、再退被依赖的技能能成,反过来就不成)。每一步的判定与 <see cref="SkillBook.Refund"/> 一样,
        /// 点数还给各自的池子。全成才一次换进去;有一步不成就整批不成,快捷栏也不动(退到 0 级会清格,失败时那一格必须还在)。
        /// </summary>
        public SkillBatchResult ForgetAll(IReadOnlyList<SkillStep> steps)
        {
            SkillBatchResult result = Simulate(steps, false, out SkillBook after);
            if (result.Ok && steps.Count > 0)
            {
                Commit(after);
            }
            return result;
        }

        private SkillBatchResult Simulate(IReadOnlyList<SkillStep> steps, bool learning, out SkillBook after)
        {
            if (steps == null)
            {
                throw new ArgumentNullException(nameof(steps));
            }
            after = Scratch();
            int sp = 0;
            int sfp = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                SkillStep step = steps[i];
                int moved;
                SkillOpResult result = learning
                    ? after.Learn(step.SkillId, _hero.Level, _hero.Growtype, step.Levels, out moved)
                    : after.Refund(step.SkillId, step.Levels, out moved);
                if (result != SkillOpResult.Ok)
                {
                    return new SkillBatchResult(result, i, 0, 0);
                }
                if (after.Catalog.Find(step.SkillId).IsSpecial)
                {
                    sfp += moved;
                }
                else
                {
                    sp += moved;
                }
            }
            return new SkillBatchResult(SkillOpResult.Ok, -1, sp, sfp);
        }

        /// <summary>演算用的副本:与主角的技能簿同一份目录、白送等级、已学等级、快捷栏与两个点数池;在副本上学、退不会碰真的技能簿。</summary>
        private SkillBook Scratch()
        {
            SkillBook source = _hero.Skills;
            var copy = new SkillBook(source.Catalog);
            var given = new List<SkillGrant>();
            foreach (SkillDef def in source.Catalog.Skills)
            {
                int level = source.GivenLevelOf(def.Id);
                if (level > 0)
                {
                    given.Add(new SkillGrant(def.Id, level));
                }
            }
            copy.SetGiven(given);
            copy.Restore(source.Learned, QuickPairs(source), source.Sp, source.Sfp);
            return copy;
        }

        /// <summary>把演算通过的终态一次换进真的技能簿(与读存档同一条路,<see cref="SkillBook.Restore"/>),然后按开关整理快捷栏。</summary>
        private void Commit(SkillBook after)
        {
            _hero.Skills.Restore(after.Learned, QuickPairs(after), after.Sp, after.Sfp);
            if (CommandAware)
            {
                ArrangeBar();
            }
        }

        private static List<KeyValuePair<int, int>> QuickPairs(SkillBook book)
        {
            var pairs = new List<KeyValuePair<int, int>>();
            for (int slot = 0; slot < SkillBook.QuickSlots; slot++)
            {
                int skillId = book.QuickAt(slot);
                if (skillId != 0)
                {
                    pairs.Add(new KeyValuePair<int, int>(slot, skillId));
                }
            }
            return pairs;
        }

        // ---------------- 一次能学几级 ----------------

        /// <summary>
        /// 此刻这个技能一次最多还能学几级:从 1 级起一级一级问 <see cref="SkillBook.CheckLearn"/>(成长类型最高级、等级间隔、点数、前置技能都在里面),直到第一个不成;
        /// 最高级封顶之后不再问。不认识的技能、已经满级、第一级就不成都是 0。
        /// </summary>
        public int LearnableLevels(int skillId)
        {
            SkillBook book = _hero.Skills;
            SkillDef def = book.Catalog.Find(skillId);
            if (def == null)
            {
                return 0;
            }
            int room = SkillRules.TypeMaxLevel(def, _hero.Growtype) - book.LevelOf(skillId);
            int best = 0;
            for (int levels = 1; levels <= room; levels++)
            {
                if (book.CheckLearn(skillId, _hero.Level, _hero.Growtype, levels, out _) != SkillOpResult.Ok)
                {
                    break;
                }
                best = levels;
            }
            return best;
        }

        /// <summary>一口气学到此刻的上限(<see cref="LearnableLevels"/>);返回学了几级(没得学是 0),<paramref name="spent"/> 是花掉的点数。</summary>
        public int LearnMax(int skillId, out int spent)
        {
            int levels = LearnableLevels(skillId);
            spent = 0;
            if (levels < 1)
            {
                return 0;
            }
            return Learn(skillId, levels, out spent) == SkillOpResult.Ok ? levels : 0;
        }

        // ---------------- 自动学 ----------------

        /// <summary>
        /// 自动学开关(默认关):打开后,<see cref="Update"/> 在主角升级之后把新得到的点按 <see cref="SkillPlanner"/> 的次序花光(本项目自己的自动机策略,不是原版行为)。
        /// 从关到开的这一刻重新对账:打开本身不花点,关着时升过的级也不补算;之后再升级,<see cref="Update"/> 才把手里的点(含之前攒下的)一次花光。
        /// </summary>
        public bool AutoLearn
        {
            get => _autoLearn;
            set
            {
                if (value && !_autoLearn)
                {
                    _seenLevelUps = _hero.LevelUps;
                }
                _autoLearn = value;
            }
        }

        /// <summary>
        /// 宿主在每帧(或升级、回城的时候)调用:自动学开着、而且上次对账之后主角又升了级,就按 <see cref="SkillPlanner.SpendAll"/> 把点数花光
        /// (<see cref="CommandAware"/> 开着时再整理快捷栏),返回学了几级;不然什么都不做、返回 0。一次升好几级只花一次;没升级时哪怕手里有点也不学。
        /// </summary>
        public int Update()
        {
            if (!_autoLearn)
            {
                return 0;
            }
            int ups = _hero.LevelUps;
            bool leveledUp = ups > _seenLevelUps;
            _seenLevelUps = ups;
            if (!leveledUp)
            {
                return 0;
            }
            int levels = SkillPlanner.SpendAll(_hero);
            if (CommandAware)
            {
                ArrangeBar();
            }
            return levels;
        }
    }
}
