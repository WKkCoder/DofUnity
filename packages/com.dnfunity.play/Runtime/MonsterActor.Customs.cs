using Dnf.Data;
using Dnf.Game.Act;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 怪物的自定义动作(.mob 的 [etc action] 列表;M62-b,证据 E-159):脚本 [SET ACTION] [CUSTOM] n 切进去的、有自己动画与触发器的动作。
    /// <para>
    /// 观察(静态读码,非 REA):客户端把脚本 [SET ACTION] 的关键字码换成"状态种类",[CUSTOM] 的种类由目标对象自己的虚槽给出(不同的对象类各答各的),
    /// 序号 n 由值表达式(字面值、[RANDOM]、[RANDOM SELECT])算出;执行块只是调目标的"设状态"虚槽,真正的切换发生在怪物自己的状态机里(这一侧没读)。
    /// 观察(汇总数,本机脚本扫描,非 REA):怪物脚本里 [SET ACTION] [CUSTOM] n 的 n 有 97% 落在本怪物 [etc action] 列表的范围里;
    /// 列表里的 .act 全读得到、九成带主动画。推断:n 就是 [etc action] 列表的下标。
    /// </para>
    /// <para>
    /// 推断(运行模型,没有对拍的数据):自定义动作住在出招阶段里(<see cref="MonsterPhase.Attack"/>,<see cref="CurrentCustomIndex"/> 不为 −1),
    /// 放它自己的主动画、跑它自己的触发器(整个自定义动作一个运行器,[ENABLE] 开关板按动作各存一份、进出保留);出招速率不作用在自定义动作上;
    /// 动画播完就回待机(循环动画不自己结束,只有脚本 [SET ACTION] 或挨打能把它切走;切招带 [END] 时等播完当前一遍;没有动画的跑一步就结束);
    /// 它不是 [attack action] 里的一招——不通知 .ai 脚本、不走投射物发射;从出招里切进来的(出招 → 自定义),结束时按出招结束算(进攻击冷却、通知脚本),
    /// 从待机或行走里切进来的结束时不进冷却。攻击框沿用最近一次出招的攻击信息(<see cref="Strike"/>)。
    /// </para>
    /// </summary>
    public sealed partial class MonsterActor
    {
        private PassiveAction[] _customs;
        private bool[][] _customEnabled;
        private IActHost _customHost;
        private int _customIndex = -1;
        private int _nextCustom = -1;
        private int _deferredCustom = -1;
        private bool _chainFromAttack;

        /// <summary>自定义动作列表的长度(读不出来的项也占一个位置);没有装为 0。</summary>
        public int CustomActionCount => _customs == null ? 0 : _customs.Length;

        /// <summary>装了自定义动作列表。</summary>
        public bool HasCustomActions => _customs != null;

        /// <summary>此刻正在放的自定义动作序号;不在自定义动作里为 −1。</summary>
        public int CurrentCustomIndex => Phase == MonsterPhase.Attack ? _customIndex : -1;

        /// <summary>此刻在放自定义动作。</summary>
        public bool InCustomAction => CurrentCustomIndex >= 0;

        /// <summary>切进自定义动作的次数(不含被拒绝的)。</summary>
        public int CustomActionsStarted { get; private set; }

        /// <summary>自定义动作自己放完(动画播完或脚本切回站立)的次数;被受击、死亡打断的不算。</summary>
        public int CustomActionsFinished { get; private set; }

        /// <summary>脚本要切的自定义动作不存在(没写 [etc action]、序号越界、那一项的 .act 读不出来)的次数。</summary>
        public int CustomActionsRejected { get; private set; }

        /// <summary>
        /// 装上自定义动作列表(<paramref name="customs"/> 的下标是脚本的序号,读不出来的项是 null)与跑它们的触发器的宿主;传 null 清掉。
        /// 每个有触发器的自定义动作各存一份 [ENABLE] 开关板(<see cref="ActRunner.NewEnabledState"/>),进出这个动作时保留。
        /// </summary>
        public void SetCustomActs(PassiveAction[] customs, IActHost host)
        {
            _customs = null;
            _customEnabled = null;
            _customHost = null;
            _customIndex = -1;
            _nextCustom = -1;
            _deferredCustom = -1;
            if (customs == null || customs.Length == 0)
            {
                return;
            }
            bool any = false;
            var enabled = new bool[customs.Length][];
            for (int i = 0; i < customs.Length; i++)
            {
                if (customs[i] == null)
                {
                    continue;
                }
                any = true;
                _anis.Remember(customs[i].Ani, customs[i].AniPath);       // 画精灵要按路径找图(换文件进来的动画同样登记)
                if (customs[i].HasTriggers)
                {
                    enabled[i] = ActRunner.NewEnabledState(customs[i].Program);
                }
            }
            if (!any)
            {
                return;
            }
            _customs = customs;
            _customEnabled = enabled;
            _customHost = host;
        }

        /// <summary>第 <paramref name="n"/> 个自定义动作存在(列表里有、那一项读得出来)。</summary>
        public bool CustomExists(int n)
        {
            return _customs != null && n >= 0 && n < _customs.Length && _customs[n] != null;
        }

        /// <summary>第 <paramref name="n"/> 个自定义动作的主动画;不存在或没有为 null。</summary>
        public AniFile CustomAni(int n)
        {
            return CustomExists(n) ? _customs[n].Ani : null;
        }

        /// <summary>
        /// 自定义动作 <paramref name="slot"/> 的触发器 <paramref name="trigger"/> 现在开着吗(开关板的读法,[TRIGGER CHECK] [CUSTOM] 与跨动作开关共用);
        /// 没有这个动作、没有这个触发器、那个动作没有触发器时为 null。
        /// </summary>
        public bool? CustomTriggerState(int slot, int trigger)
        {
            bool[] state = CustomBoard(slot);
            return state != null && trigger >= 0 && trigger < state.Length ? state[trigger] : (bool?)null;
        }

        private bool[] CustomBoard(int slot)
        {
            return _customEnabled != null && slot >= 0 && slot < _customEnabled.Length ? _customEnabled[slot] : null;
        }

        /// <summary>脚本开关自定义动作 <paramref name="slot"/> 的触发器(跨动作开关,写法 [TRIGGER] [CUSTOM] a t);正在跑的那个动作要经运行器写(重来的计数一并清)。</summary>
        private void ActSwitchCustomTrigger(int slot, int trigger, ActTriggerCommand command)
        {
            bool[] state = CustomBoard(slot);
            if (state == null || trigger < 0 || trigger >= state.Length)
            {
                ActSwitchMissed++;
                return;
            }
            ActSwitchApplied++;
            if (Phase == MonsterPhase.Attack && _customIndex == slot && _actRunner != null)
            {
                _actRunner.ApplyTrigger(trigger, command);
                return;
            }
            switch (command)
            {
                case ActTriggerCommand.On:
                    state[trigger] = true;
                    break;
                case ActTriggerCommand.Off:
                    state[trigger] = false;
                    break;
            }
        }

        /// <summary>
        /// 切进自定义动作 <paramref name="n"/>(调用方已确认它存在):阶段仍是出招阶段,只是动画、触发器程序换成自定义动作的。
        /// 从出招里切过来的记下"这一串动作由出招起头"(结束时按出招结束算)。
        /// </summary>
        private void EnterCustom(int n)
        {
            _chainFromAttack = Phase == MonsterPhase.Attack && (_customIndex < 0 || _chainFromAttack);
            _nextCustom = n;
            _deferredAttack = -1;
            _deferredCustom = -1;
            CustomActionsStarted++;
            EnterPhase(MonsterPhase.Attack);
        }

        /// <summary>自定义动作自己放完了:由出招起头的按出招结束算(进攻击冷却、通知脚本),待机与行走里切进来的直接回待机。</summary>
        private void FinishCustom()
        {
            CustomActionsFinished++;
            if (_chainFromAttack)
            {
                FinishAttack();
                return;
            }
            if (_actFalling && !_hovering)
            {
                Z = 0;
            }
            EnterPhase(MonsterPhase.Wait);
        }

        /// <summary>这一遍出招阶段要放多久(毫秒):自定义动作看它自己的动画(没有动画立刻结束;循环动画没有切招排队时不自己结束),出招是当前动画一遍。</summary>
        private double AttackSpanMs()
        {
            if (_customIndex < 0)
            {
                return Duration(CurrentAni);
            }
            AniFile ani = _customs[_customIndex].Ani;
            if (ani == null)
            {
                return 1.0;       // 没有动画:跑完进来之后的第一步(触发器得走一次)就结束
            }
            bool queued = _deferredAttack >= 0 || _deferredCustom >= 0;
            return Timeline(ani).Loops && !queued ? double.PositiveInfinity : Duration(ani);
        }

        /// <summary>自定义动作的动画放一遍要多久(毫秒):没有动画是 0;循环动画取一个周期。</summary>
        private double CustomPassMs()
        {
            AniFile ani = _customs[_customIndex].Ani;
            return ani == null ? 0.0 : Duration(ani);
        }

        /// <summary>出招阶段动画推进的倍率:出招吃出招速率,自定义动作不吃(推断,没有对拍的数据)。</summary>
        private double PhaseRate => _customIndex >= 0 ? 1.0 : AttackRate;
    }
}
