using System;

// 服务端静态读码:CCreature(实例)Reset 0x083370b0、SetItemId 0x083375ae、SetState 0x0833715a、Equip 0x08337330、Dismantle 0x08337382、
// UseFeed 0x083373d4、GetExpLevel 0x083374d2、GainExp 0x083378fc、GainExpCreature 0x08337bbc、CalculateExp 0x083377b6、GetAddExp 0x08337724、
// IsDieCreature 0x08338478、LoadCreatureItem 0x083385aa;CCreatureMgr::TurnStomach 0x0833c28a。规则的依据与"未知"清单见各成员的说明。
namespace Dnf.Game
{
    /// <summary>喂食的结果。</summary>
    public enum PetFeedResult
    {
        /// <summary>喂进去了。</summary>
        Fed = 0,

        /// <summary>喂进去了,而且喂之前宠物是饿死状态(服务端此时多发一条"复活"通知)。</summary>
        Revived = 1,

        /// <summary>这只宠物不吃饲料(选择成长型)。</summary>
        NotFeedable = 2,

        /// <summary>现在没有装备宠物(由 <c>PetSession</c> 这一层返回;宠物实例自己不会给出它)。</summary>
        NoPet = 3,
    }

    /// <summary>加经验为什么成功或没成功。</summary>
    public enum PetGainOutcome
    {
        /// <summary>加上了。</summary>
        Gained = 0,

        /// <summary>这种宠物不吃普通经验(选择成长型走物品喂养,这一版没有实现)。</summary>
        NotGainable = 1,

        /// <summary>经验量不大于 0(服务端不拦,这里拒绝)。</summary>
        InvalidAmount = 2,

        /// <summary>已经是满级(服务端满级不再涨经验)。</summary>
        AtMaxLevel = 3,

        /// <summary>饿死状态不涨经验。</summary>
        Starved = 4,

        /// <summary>现在没有装备宠物(由 <c>PetSession</c> 这一层返回;服务端没有装备宠物时根本不会调加经验)。</summary>
        NoPet = 5,
    }

    /// <summary>一次加经验的结果。</summary>
    public readonly struct PetGainResult
    {
        public PetGainResult(PetGainOutcome outcome, bool leveledUp, int added)
        {
            Outcome = outcome;
            LeveledUp = leveledUp;
            Added = added;
        }

        public PetGainOutcome Outcome { get; }

        /// <summary>这次加完等级变高了。</summary>
        public bool LeveledUp { get; }

        /// <summary>总经验实际增加了多少(含神器加成;总经验到了整数上限时少于应加的量)。</summary>
        public int Added { get; }

        public bool Gained => Outcome == PetGainOutcome.Gained;
    }

    /// <summary>一次状态切换带出来的事件。</summary>
    public readonly struct PetStateChange
    {
        public PetStateChange(bool revived, int deathCheckDelay)
        {
            Revived = revived;
            DeathCheckDelay = deathCheckDelay;
        }

        /// <summary>切到 <see cref="PetState.Charging"/> 时宠物原本是饿死状态,饱食被置成 <see cref="PetRules.RevivedStomach"/>(服务端发"复活"通知)。</summary>
        public bool Revived { get; }

        /// <summary>
        /// 切到 <see cref="PetState.Draining"/> 时服务端要排一条死亡检查,延迟 = <c>(int)截断(饱食 × 60.0)</c>;没排时是 0。
        /// <b>延迟的单位没有读到</b>(定时器队列的单位),这里只给出整数;"检查时才判饿死"这件事本身由 <see cref="PetInstance.IsStarved"/> 随时可查。
        /// </summary>
        public int DeathCheckDelay { get; }
    }

    /// <summary>
    /// 一只宠物实例:等级、经验、饱食(随时间结算)、状态,以及喂食、加经验、装备与卸下、战斗开始与结束带来的状态切换。
    /// <para>
    /// <b>时间</b>:所有会读饱食的方法都带当前秒数 <c>now</c>(单调递增的整数秒,由宿主给);读取时会把离上次结算的时间按当前状态折算进饱食并写回
    /// (服务端的读取也是这样,<see cref="PetRules.Settle"/>)。<b>饱食数值不会为负,也不会超过满值</b>:消耗时夹到 0,回补时夹到 100,时钟倒退按 0 秒算。
    /// </para>
    /// <para>
    /// <b>规则来源(服务端读码,观察)</b>:消耗中每 60 秒 −1、否则每 360 秒 +1;饱食严格小于 1 为饿死;喂食 +30 夹到 100、不判"已满";
    /// 饿死的宠物不涨经验;切到"装备且不在战斗"(<see cref="PetState.Charging"/>)会把饿死的宠物复活成饱食 1;新建的宠物 1 级、经验 0、饱食 100、未装备;
    /// 等级总是由累计经验按全局经验表换算(夹在脚本的 <c>[max level]</c> 以内),<b>升级不扣经验</b>(经验是累计值);存档只存经验与整数饱食,等级读档时重算。
    /// </para>
    /// <para>
    /// <b>未知或没做</b>:升级后的自动进化、选择成长型的物品喂养与进化选择、改名、过期时间、死亡检查定时器的延迟单位、
    /// 通用配置里"吃饲料所需"一项的含义(服务端喂食用的是写死的增量)。
    /// </para>
    /// </summary>
    public sealed class PetInstance
    {
        private readonly PetExpTable _table;
        private float _stomach;
        private int _lastSettle;
        private int _exp;
        private int _level;
        private PetState _state;

        private PetInstance(int uid, int itemCode, PetDef def, PetExpTable table)
        {
            Uid = uid;
            ItemCode = itemCode;
            Def = def ?? throw new ArgumentNullException(nameof(def));
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _state = PetState.Stored;
        }

        /// <summary>实例编号(存档里认的键,宠物之间不重复)。</summary>
        public int Uid { get; }

        /// <summary>这只宠物对应的宠物物品的物品编号。</summary>
        public int ItemCode { get; }

        public PetDef Def { get; }

        public PetKind Kind => Def.Kind;

        /// <summary>等级(累计经验换算而来,不低于 1、不高于脚本的 <c>[max level]</c>)。</summary>
        public int Level => _level;

        /// <summary>累计经验(只增不减;升级不扣)。</summary>
        public int Exp => _exp;

        public PetState State => _state;

        /// <summary>孵化出一只新宠物:1 级、经验 0、饱食 <see cref="PetRules.HatchStomach"/>、未装备。</summary>
        public static PetInstance Hatch(int uid, int itemCode, PetDef def, PetExpTable table, int now)
        {
            var pet = new PetInstance(uid, itemCode, def, table);
            pet._level = PetRules.StartLevel;
            pet._exp = 0;
            pet._stomach = PetRules.HatchStomach;
            pet._lastSettle = now;
            return pet;
        }

        /// <summary>
        /// 从存档还原(服务端 <c>LoadCreatureItem</c>):经验与整数饱食照存,等级由经验重算,状态是未装备,计时起点是 <paramref name="now"/>(离线时间不算);
        /// 之后装备的宠物再走一遍 <see cref="Equip"/>。经验夹到不低于 0,饱食夹进 0 到满值。
        /// </summary>
        public static PetInstance Load(int uid, int itemCode, PetDef def, PetExpTable table, int exp, int stomach, int now)
        {
            var pet = new PetInstance(uid, itemCode, def, table);
            pet._exp = exp < 0 ? 0 : exp;
            pet._stomach = stomach < 0 ? 0f : stomach > PetRules.MaxStomach ? PetRules.MaxStomach : stomach;
            pet._lastSettle = now;
            pet._level = pet.LevelOfExp(pet._exp);
            return pet;
        }

        /// <summary>
        /// 累计经验对应的等级(服务端 <c>GetExpLevel</c>):普通型查全局经验表,选择成长型是"已喂物品数(经验的高 16 位)÷ 每级物品数 + 1"
        /// (每级物品数写 0 按 <see cref="PetRules.DefaultGrowItemsPerLevel"/>);再与脚本的 <c>[max level]</c> 取小。服务端不夹下限,这里夹到至少 1。
        /// </summary>
        private int LevelOfExp(int exp)
        {
            int level;
            if (Kind == PetKind.Normal)
            {
                level = _table.LevelOfExp(exp);
            }
            else
            {
                int perLevel = Def.Grow.ItemsPerLevel > 0 ? Def.Grow.ItemsPerLevel : PetRules.DefaultGrowItemsPerLevel;
                level = (int)(((uint)exp >> 16) / (uint)perLevel) + 1;
            }
            level = Math.Min(level, Def.MaxLevel);
            return Math.Max(level, 1);
        }

        /// <summary>
        /// 结算并返回此刻的饱食(会写回,与服务端读取一样)。选择成长型恒为 <see cref="PetRules.GrowKindStomach"/>、什么也不改。
        /// </summary>
        public float Settle(int now)
        {
            if (Kind != PetKind.Normal)
            {
                return PetRules.GrowKindStomach;
            }
            _stomach = PetRules.Settle(_stomach, (long)now - _lastSettle, _state);
            _lastSettle = now;
            return _stomach;
        }

        /// <summary>此刻的整数饱食(存档里存的就是它;向零取整)。</summary>
        public int Stomach(int now)
        {
            return (int)Settle(now);
        }

        /// <summary>饿死状态:普通型的饱食严格小于 <see cref="PetRules.StarvedBelow"/>;选择成长型恒不饿死。</summary>
        public bool IsStarved(int now)
        {
            return Kind == PetKind.Normal && PetRules.IsStarved(Settle(now));
        }

        /// <summary>
        /// 喂一次(服务端 <c>UseFeed</c>):普通型饱食加 <see cref="PetRules.FeedPoints"/>、夹到满值,不判"已满"也不判消耗品是否够(那是调用方的事);
        /// 选择成长型不吃。喂的是哪件消耗品由调用方判断(服务端是一个写死的物品编号,仓库里不放真实编号)。
        /// </summary>
        public PetFeedResult Feed(int now)
        {
            if (Kind != PetKind.Normal)
            {
                return PetFeedResult.NotFeedable;
            }
            bool wasStarved = PetRules.IsStarved(Settle(now));
            float fed = _stomach + PetRules.FeedPoints;
            _stomach = fed > PetRules.MaxStomach ? PetRules.MaxStomach : fed;
            return wasStarved ? PetFeedResult.Revived : PetFeedResult.Fed;
        }

        /// <summary>
        /// 加经验(服务端 <c>GainExp</c> → <c>GainExpCreature</c>):<paramref name="amount"/> 是调用方给的基础量,
        /// <paramref name="bonusPercent"/> 是三个神器格的经验加成(百分数)之和,实际增加 <c>(int)截断(float(量 × 加成 / 100)) + 量</c>。
        /// 满级、饿死、选择成长型都加不上;总经验到整数上限为止;加完按累计经验重算等级,<b>不扣经验</b>。
        /// </summary>
        public PetGainResult GainExp(int amount, int now, float bonusPercent)
        {
            if (Kind != PetKind.Normal)
            {
                return new PetGainResult(PetGainOutcome.NotGainable, false, 0);
            }
            if (amount <= 0)
            {
                return new PetGainResult(PetGainOutcome.InvalidAmount, false, 0);
            }
            if (_level >= Def.MaxLevel)
            {
                return new PetGainResult(PetGainOutcome.AtMaxLevel, false, 0);
            }
            if (PetRules.IsStarved(Settle(now)))
            {
                return new PetGainResult(PetGainOutcome.Starved, false, 0);
            }
            long total = (long)_exp + PetRules.ExpGain(amount, bonusPercent);
            int before = _exp;
            _exp = total > int.MaxValue ? int.MaxValue : total < 0 ? 0 : (int)total;
            int level = LevelOfExp(_exp);
            bool leveledUp = level > _level;
            _level = level;
            return new PetGainResult(PetGainOutcome.Gained, leveledUp, _exp - before);
        }

        /// <summary>
        /// 切换状态(服务端 <c>SetState</c>)。先把旧状态下的时间结算进饱食;状态真的变了才触发特殊动作(都只对普通型):
        /// 进入消耗中记下死亡检查延迟;进入"装备且回补"时若饿死则复活成饱食 <see cref="PetRules.RevivedStomach"/>。
        /// 进入未装备(卸下)不复活。
        /// </summary>
        public PetStateChange SetState(PetState next, int now)
        {
            float settled = Settle(now);
            bool revived = false;
            int delay = 0;
            if (_state != next && Kind == PetKind.Normal)
            {
                if (next == PetState.Draining)
                {
                    delay = PetRules.DeathCheckDelay(settled);
                }
                else if (next == PetState.Charging && PetRules.IsStarved(settled))
                {
                    _stomach = PetRules.RevivedStomach;
                    revived = true;
                }
            }
            _state = next;
            return new PetStateChange(revived, delay);
        }

        /// <summary>装备(服务端 <c>CCreature::Equip</c>):用户在战斗里进 <see cref="PetState.Draining"/>,否则进 <see cref="PetState.Charging"/>。</summary>
        public PetStateChange Equip(bool userFighting, int now)
        {
            return SetState(userFighting ? PetState.Draining : PetState.Charging, now);
        }

        /// <summary>卸下(服务端 <c>CCreature::Dismantle</c>):进 <see cref="PetState.Stored"/>。</summary>
        public PetStateChange Dismantle(int now)
        {
            return SetState(PetState.Stored, now);
        }

        /// <summary>
        /// 战斗开始或结束(服务端 <c>CCreatureMgr::TurnStomach</c>):开始时 <see cref="PetState.Charging"/> → <see cref="PetState.Draining"/>,
        /// 结束时 <see cref="PetState.Draining"/> → <see cref="PetState.Charging"/>;在别的状态(没装备)什么也不做。
        /// </summary>
        public PetStateChange SetFighting(bool fighting, int now)
        {
            if (fighting && _state == PetState.Charging)
            {
                return SetState(PetState.Draining, now);
            }
            if (!fighting && _state == PetState.Draining)
            {
                return SetState(PetState.Charging, now);
            }
            return default;
        }
    }
}
