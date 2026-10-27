using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Game.Ai;
using Dnf.Render;

// 证据:控制器的四路脚本(事件、选目的地、动作、移动方式)与思考节拍见 E-052、E-128;冷却按 [cooltime] 毫秒、招式列表与攻击信息同下标见 E-128;
// "冷却中为真"的方向见 E-053;出生时各招的冷却是推断(控制器出生时把每一招的"可用"标志清零,E-128)。
namespace Dnf.Play
{
    /// <summary>数一数某个区域里的活对象个数(.ai 的区域计数函数用);参数含义见 <see cref="IAiHost.CountInArea"/>。</summary>
    public delegate int AreaCounter(float xMin, float xMax, float yMin, float yMax, int teamMode, int typeFilter, int idFilter);

    /// <summary>怪物这一刻的处境:宿主(<see cref="MonsterActor"/>)每次思考前填好,脚本按它读血量、状态、目标距离。</summary>
    public sealed class MonsterAiView
    {
        public double X { get; set; }

        public double Y { get; set; }

        public bool FacingLeft { get; set; }

        public int Hp { get; set; }

        public int HpMax { get; set; }

        public int Level { get; set; }

        /// <summary>自己的状态号(OperatingValue.lst 里的写法:0 站、3 受击、4 倒地、5 死亡、8 出招)。</summary>
        public int State { get; set; }

        /// <summary>自由状态(站着或走着)。</summary>
        public bool Free { get; set; }

        public bool HasTarget { get; set; }

        public double TargetX { get; set; }

        public double TargetY { get; set; }

        /// <summary>目标(主角)的状态号;没有目标时无意义。</summary>
        public int TargetState { get; set; }
    }

    /// <summary>
    /// 一只怪物的 .ai 驱动(证据 E-128):四个脚本(事件、选目的地、动作、移动方式)、各招的冷却、思考节拍。
    /// 每帧 <see cref="Advance"/> 推进计时;到了思考节拍(<see cref="ThinkTermMs"/>)宿主先用 <see cref="DecideAttack"/> 问动作脚本"出哪一招"(-1 不出),
    /// 再按 <see cref="DestinationTermMs"/> 的节拍用 <see cref="DecideDestination"/> 问选目的地脚本"往哪走",最后用 <see cref="DecideMove"/> 问移动方式。
    /// 动作脚本返回的非负数是 <c>[attack action]</c>(或 <c>[action list]</c>)里招式的下标;冷却取 <c>[cooltime]</c> 同下标的毫秒数,出生时每一招都在冷却(推断),
    /// 招式没有动画或冷却中一律当"出不了"。
    /// </summary>
    public sealed class MonsterScript
    {
        public const int RoleEvent = 0;
        public const int RoleDestination = 1;
        public const int RoleAction = 2;
        public const int RoleMove = 3;
        public const int RoleCount = 4;

        public const int DefaultThinkTermMs = 1000;
        public const int DefaultDestinationTermMs = 500;
        public const int DefaultVision = 1000;

        private readonly AiEvaluator _evaluator;
        private readonly string[] _paths;
        private readonly AiScript[] _scripts;
        private readonly int[] _cooltimes;
        private readonly double[] _cooling;
        private readonly Func<int> _rand;
        private readonly AiScratch _scratch = new AiScratch();
        private readonly Host _host;
        private double _thinkClockMs;
        private double _destinationClockMs;
        private double _delayLeftMs;

        /// <param name="loader">按 PVF 路径取已解析的 .ai(给 [import ai] 用);缺文件返回 null。</param>
        /// <param name="rolePaths">四个角色的脚本路径(事件、选目的地、动作、移动方式),缺的为 null。</param>
        /// <param name="roleScripts">与 <paramref name="rolePaths"/> 对应已解析的脚本,缺的为 null。</param>
        /// <param name="cooltimes">[cooltime] 的毫秒数,与招式列表同下标;比招式少的部分按 0(没有冷却)。</param>
        /// <param name="attackCount">招式个数。</param>
        /// <param name="rand">客户端 rand(0) 的替身(31 位非负整数)。</param>
        public MonsterScript(Func<string, AiScript> loader, string[] rolePaths, AiScript[] roleScripts, IReadOnlyList<int> cooltimes, int attackCount, Func<int> rand)
        {
            _evaluator = new AiEvaluator(loader);
            _paths = new string[RoleCount];
            _scripts = new AiScript[RoleCount];
            for (int i = 0; i < RoleCount; i++)
            {
                _paths[i] = rolePaths != null && i < rolePaths.Length ? rolePaths[i] : null;
                _scripts[i] = roleScripts != null && i < roleScripts.Length ? roleScripts[i] : null;
            }
            _rand = rand ?? throw new ArgumentNullException(nameof(rand));
            int n = attackCount < 0 ? 0 : attackCount;
            _cooltimes = new int[n];
            _cooling = new double[n];
            for (int i = 0; i < n; i++)
            {
                int ms = cooltimes != null && i < cooltimes.Count ? cooltimes[i] : 0;
                _cooltimes[i] = ms < 0 ? 0 : ms;
                _cooling[i] = _cooltimes[i];
            }
            _host = new Host(this);
        }

        /// <summary>思考节拍(毫秒):[think term],没写取 <see cref="DefaultThinkTermMs"/>。</summary>
        public int ThinkTermMs { get; set; } = DefaultThinkTermMs;

        /// <summary>重选目的地的最短间隔(毫秒):[destination change term],没写取 <see cref="DefaultDestinationTermMs"/>。</summary>
        public int DestinationTermMs { get; set; } = DefaultDestinationTermMs;

        /// <summary>出完一招之后到下一次出招的间隔(毫秒):[attack delay];没写为 0(只受冷却限制)。</summary>
        public int AttackDelayMs { get; set; }

        /// <summary>选目的地方案 1 用的"与目标保持的水平距离"(像素):[keep distance with target]。</summary>
        public int KeepDistance { get; set; }

        /// <summary>视野(像素):[vision];目标水平距离在它以内怪物才发现主角,发现之后一直盯着(粘住)。0 或负数当 <see cref="DefaultVision"/>。</summary>
        public int Vision { get; set; } = DefaultVision;

        /// <summary>第 i 招有没有动画可播;没设置时全都有。没有动画的招一律当"出不了"。</summary>
        public Func<int, bool> AttackExists { get; set; }

        /// <summary>第 a 个招式的第 t 个触发器现在是不是开着(.act 的 [ENABLE]);没设置时全是关的。</summary>
        public Func<int, int, bool> TriggerEnabled { get; set; }

        /// <summary>区域计数(别的怪物、主角);没设置时恒为 0。</summary>
        public AreaCounter Counter { get; set; }

        /// <summary>招式个数。</summary>
        public int AttackCount => _cooling.Length;

        /// <summary>某个角色的脚本有没有读到。</summary>
        public bool HasRole(int role) => (uint)role < RoleCount && _scripts[role] != null;

        /// <summary>第 <paramref name="index"/> 招还有多少毫秒冷却完;已经可用或没有这一招为 0。</summary>
        public double CoolingMs(int index) => (uint)index < (uint)_cooling.Length ? Math.Max(0.0, _cooling[index]) : 0.0;

        /// <summary>主角的水平距离 <paramref name="dx"/> 在视野里吗(只看水平:房间的纵深很窄)。</summary>
        public bool Notices(double dx)
        {
            int vision = Vision > 0 ? Vision : DefaultVision;
            return Math.Abs(dx) <= vision;
        }

        /// <summary>推进计时:各招的冷却、思考节拍、重选目的地的间隔、出招之后的间隔都按真实时间走(命中停顿里宿主不调用它)。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs <= 0)
            {
                return;
            }
            _thinkClockMs += deltaMs;
            _destinationClockMs += deltaMs;
            if (_delayLeftMs > 0)
            {
                _delayLeftMs -= deltaMs;
            }
            for (int i = 0; i < _cooling.Length; i++)
            {
                if (_cooling[i] > 0)
                {
                    _cooling[i] -= deltaMs;
                }
            }
        }

        /// <summary>到了思考节拍就返回 true 并把节拍清零。</summary>
        public bool TakeThinkTurn()
        {
            int term = ThinkTermMs > 0 ? ThinkTermMs : DefaultThinkTermMs;
            if (_thinkClockMs < term)
            {
                return false;
            }
            _thinkClockMs = 0;
            return true;
        }

        /// <summary>到了重选目的地的间隔就返回 true 并清零。</summary>
        public bool TakeDestinationTurn()
        {
            int term = DestinationTermMs > 0 ? DestinationTermMs : DefaultDestinationTermMs;
            if (_destinationClockMs < term)
            {
                return false;
            }
            _destinationClockMs = 0;
            return true;
        }

        /// <summary>
        /// 问动作脚本"出哪一招":返回招式下标,-1 表示不出。出招之后的间隔没过、返回值越界、没有动画或冷却中都按 -1。
        /// </summary>
        public int DecideAttack(MonsterAiView view)
        {
            if (_delayLeftMs > 0 || !HasRole(RoleAction))
            {
                return -1;
            }
            AiResult result = Evaluate(RoleAction, view);
            if (result.Kind != AiResultKind.Number)
            {
                return -1;
            }
            int index = (int)Math.Round(result.Number);
            return CanUse(index) ? index : -1;
        }

        /// <summary>问选目的地脚本"往哪走"。没有这个脚本、没有目标或结果不是数字时不更新(<see cref="AiDestinationKind.Keep"/>)。</summary>
        public AiDestination DecideDestination(MonsterAiView view)
        {
            if (!HasRole(RoleDestination) || !view.HasTarget)
            {
                return new AiDestination(AiDestinationKind.Keep, (float)view.X, (float)view.Y);
            }
            AiResult result = Evaluate(RoleDestination, view);
            int plan = result.Kind == AiResultKind.Number ? (int)Math.Round(result.Number) : AiDestinations.Keep;
            return AiDestinations.Resolve(plan, (float)view.X, (float)view.Y, (float)view.TargetX, (float)view.TargetY, KeepDistance, _rand);
        }

        /// <summary>
        /// 问移动方式脚本:返回它给的数。-1(脚本写 -1 或根本没有结果)表示不移动,0 起是各种走法;现在所有非负数都按"正常走"处理(各数的差别还没读通,E-128)。
        /// 没有这个脚本时返回 0(正常走)。
        /// </summary>
        public int DecideMove(MonsterAiView view)
        {
            if (!HasRole(RoleMove))
            {
                return 0;
            }
            AiResult result = Evaluate(RoleMove, view);
            return result.Kind == AiResultKind.Number ? (int)Math.Round(result.Number) : -1;
        }

        /// <summary>这一招现在能不能出(有这一招、有动画、冷却完了)。</summary>
        public bool CanUse(int index)
        {
            return (uint)index < (uint)_cooling.Length && (AttackExists == null || AttackExists(index)) && _cooling[index] <= 0;
        }

        /// <summary>开始出第 <paramref name="index"/> 招:这一招进入冷却。</summary>
        public void AttackStarted(int index)
        {
            if ((uint)index < (uint)_cooling.Length)
            {
                _cooling[index] = _cooltimes[index];
            }
        }

        /// <summary>出完一招:开始计 <see cref="AttackDelayMs"/>。</summary>
        public void AttackFinished()
        {
            _delayLeftMs = AttackDelayMs > 0 ? AttackDelayMs : 0;
        }

        private AiResult Evaluate(int role, MonsterAiView view)
        {
            _host.Bind(view);
            _scratch.Clear();
            Fill(view);
            return _evaluator.Evaluate(_paths[role], _scripts[role], _host, _scratch);
        }

        /// <summary>每次思考前把这一刻的情形写进暂存槽(证据 E-052:控制器的填充函数);没有目标时目标距离为 0、目标状态为 -1。</summary>
        private void Fill(MonsterAiView v)
        {
            AiScratch s = _scratch;
            s.Set(AiSlot.Mind, 0f);
            s.Set(AiSlot.Hp, v.Hp, v.HpMax);
            s.Set(AiSlot.State, v.State);
            s.Set(AiSlot.Random, _rand() % 100);
            s.Set(AiSlot.WeaponType, 0f);
            double dx = v.TargetX - v.X;
            double dy = v.TargetY - v.Y;
            s.Set(AiSlot.TargetDistance, v.HasTarget ? (float)Math.Sqrt(dx * dx + dy * dy) : 0f);
            s.Set(AiSlot.TargetState, v.HasTarget ? v.TargetState : -1f);
            s.Set(AiSlot.ComboBonus, 0f);
            s.Set(AiSlot.ActiveStatus, 1f);
            s.Set(AiSlot.MyState, v.State);
            s.Set(AiSlot.MyLevel, v.Level);
        }

        /// <summary>给 .ai 求值器的宿主适配:位置取自当前的 <see cref="MonsterAiView"/>,冷却与触发器开关取自本驱动。</summary>
        private sealed class Host : IAiHost
        {
            private readonly MonsterScript _owner;
            private MonsterAiView _view;

            public Host(MonsterScript owner)
            {
                _owner = owner;
            }

            public void Bind(MonsterAiView view)
            {
                _view = view;
            }

            public int Rand() => _owner._rand();

            public float SelfX => (float)_view.X;

            public float SelfY => (float)_view.Y;

            public bool SelfFacingLeft => _view.FacingLeft;

            public bool HasTarget => _view.HasTarget;

            public float TargetX => (float)_view.TargetX;

            public float TargetY => (float)_view.TargetY;

            public bool IsAttackCooling(int index) => !_owner.CanUse(index);

            public bool IsAttackEnabled(int action, int trigger) => _owner.TriggerEnabled != null && _owner.TriggerEnabled(action, trigger);

            public bool IsCustomEnabled(int action, int trigger) => false;

            public bool IsFreeState() => _view.Free;

            public bool DamageCheck(int mode) => false;

            public int CountInArea(float xMin, float xMax, float yMin, float yMax, int teamMode, int typeFilter, int idFilter)
            {
                return _owner.Counter == null ? 0 : _owner.Counter(xMin, xMax, yMin, yMax, teamMode, typeFilter, idFilter);
            }

            /// <summary>设置类关键字([set target monster] 等,选目标、选友方、划范围)读通了的是"设置并返回真";没做的检查类一律为假。</summary>
            public bool Other(AiNode node, AiScratch scratch)
            {
                return node.Keyword != null && node.Keyword.StartsWith("[set ", StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// 按 .mob 造一只怪物的 .ai 驱动:取当前难度的四个脚本(没有该难度回退到 easy),[cooltime]、[think term] 等参数。
        /// 没有 [ai pattern] 或动作与选目的地脚本都读不到时返回 null(调用方继续用旧的占位行为)。
        /// </summary>
        public static MonsterScript Create(GameData data, MonsterDef def, MonsterModel model, int difficulty, Func<int> rand)
        {
            if (data == null || def == null || def.AiPatterns == null || def.AiPatterns.Count == 0)
            {
                return null;
            }
            string[] names = MonsterDef.AiDifficultyNames;
            string[] paths = def.AiPaths(names[Math.Max(0, Math.Min(names.Length - 1, difficulty))]);
            if (paths.Length == 0)
            {
                paths = def.AiPaths(names[0]);
            }
            if (paths.Length == 0)
            {
                return null;
            }
            var roleScripts = new AiScript[RoleCount];
            var rolePaths = new string[RoleCount];
            for (int i = 0; i < RoleCount && i < paths.Length; i++)
            {
                rolePaths[i] = paths[i];
                roleScripts[i] = data.GetAiScript(paths[i]);
            }
            if (roleScripts[RoleAction] == null && roleScripts[RoleDestination] == null)
            {
                return null;
            }
            int attackCount = Math.Max(def.AttackList.Length, model != null ? model.Attacks.Count : 0);
            var script = new MonsterScript(data.GetAiScript, rolePaths, roleScripts, def.Cooltimes, attackCount, rand)
            {
                ThinkTermMs = def.ThinkTerm > 0 ? def.ThinkTerm : DefaultThinkTermMs,
                DestinationTermMs = def.DestinationChangeTerm > 0 ? def.DestinationChangeTerm : DefaultDestinationTermMs,
                AttackDelayMs = def.Tags.IntOr("[attack delay]", 0),
                KeepDistance = def.KeepDistanceWithTarget,
                Vision = def.Vision > 0 ? def.Vision : DefaultVision,
            };
            if (model != null)
            {
                script.AttackExists = i => i < model.Attacks.Count && model.Attacks[i] != null;
            }
            script.TriggerEnabled = TriggerFlags(data, def, attackCount);
            return script;
        }

        /// <summary>读每个招式 .act 的触发器开关初值:[TRIGGER] 块里写了 [ENABLE] 就取它,没写算开(证据 E-128,运行期的开关还没做)。</summary>
        private static Func<int, int, bool> TriggerFlags(GameData data, MonsterDef def, int attackCount)
        {
            var flags = new bool[attackCount][];
            string[] list = def.AttackList;
            for (int a = 0; a < attackCount; a++)
            {
                flags[a] = Array.Empty<bool>();
                if (a >= list.Length || string.IsNullOrEmpty(list[a]))
                {
                    continue;
                }
                ScriptLine[] lines = data.ReadScript(PvfPath.ResolveRelativeTo(def.Path, list[a]));
                if (lines == null)
                {
                    continue;
                }
                var enabled = new List<bool>();
                foreach (ActNode root in ActParser.Parse(lines).Roots)
                {
                    if (root.Is("[TRIGGER]"))
                    {
                        ActNode e = root.Find("[ENABLE]");
                        enabled.Add(e == null || e.IntAt(0, 1) != 0);
                    }
                }
                flags[a] = enabled.ToArray();
            }
            return (action, trigger) => (uint)action < (uint)flags.Length && (uint)trigger < (uint)flags[action].Length && flags[action][trigger];
        }
    }
}
