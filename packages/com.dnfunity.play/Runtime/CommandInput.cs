using System;
using System.Collections.Generic;
using Dnf.Game;

// 证据:E-140。判定窗口与"按住算不算"没有读到(客户端的出招判定在受保护节里),按已有的双击窗口(E-072)与 DNF 通用的玩法习惯假设,逐条列在 E-140 末尾。
namespace Dnf.Play
{
    /// <summary>
    /// 出招指令输入:记下最近按过的键(带时间),判断一个技能的 [command] 键序列是不是"刚刚被打出来"(<see cref="SkillCommand"/>)。
    /// <para>
    /// 判定规则(假设,E-140):序列的最后一个键(扳机)必须就在这一步被按下(边沿);它前面的每个键要么在后一个键被按下之前 <see cref="WindowMs"/> 毫秒内
    /// 按过(依次),要么是方向键而且在后一个键被按下那一刻是按着的(按住不放也算,即使后来松开了;但一个方向键按住只能顶一次)。同一个键在序列里出现两次(如双击)要两次独立的按下。
    /// 和后一个键用 "&amp;"(同时)连接的方向键(<see cref="SkillCommand.TogetherWithNext"/>)只认"后一个键被按下那一刻按着",点过一下又松开不算。
    /// 方向键按"相对朝向"解释:RIGHT 是朝前、LEFT 是朝后,所以朝左的主角打 RIGHT 要按左键。
    /// 参照的朝向是打出扳机那一刻主角的朝向(宿主传入时要把这一步方向键造成的转身算进去):主角按下方向键就转向那边,
    /// 所以像"左、右、技能"这样以朝前的键收尾的序列,不论起手时朝哪边都打得出来;全库的指令没有一条以朝后的键收尾(汇总,E-140)。
    /// </para>
    /// 时钟只在 <see cref="Update"/> 里走(命中停顿时宿主不调用,窗口也跟着停)。
    /// </summary>
    public sealed class CommandInput
    {
        /// <summary>相邻两个键最长隔多久(毫秒)。与奔跑的双击窗口(<c>TownHero.DoubleTapWindowMs</c>)同一个假设值。</summary>
        public const double WindowMs = 300.0;

        private const double HistoryMs = 2000.0;
        private const int MaxPresses = 32;
        private const int KeyCount = 8;

        private struct Press
        {
            public CommandKey Key;
            public double TimeMs;
        }

        private readonly List<Press> _presses = new List<Press>(MaxPresses + 1);
        private readonly List<Press> _releases = new List<Press>(MaxPresses + 1);     // 方向键松开的记录(Key 是松开的那个键)
        private readonly bool[] _held = new bool[KeyCount];
        private readonly bool[] _fresh = new bool[KeyCount];
        private double _nowMs;
        private int _prevDirX;
        private int _prevDirY;

        /// <summary>输入时钟(毫秒):每次 <see cref="Update"/> 加上这一步的时长。</summary>
        public double NowMs => _nowMs;

        /// <summary>
        /// 推进一步:按这一步的方向(dirX 向右为正、dirY 向下为正)与刚按下的键,记下按键事件。
        /// 方向键的"按下"是方向从别的值变成这个方向的那一步。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, bool skillPressed, bool buffPressed)
        {
            if (deltaMs > 0)
            {
                _nowMs += deltaMs;
            }
            Array.Clear(_fresh, 0, KeyCount);
            if (dirX != _prevDirX)
            {
                if (_prevDirX != 0) { Release(_prevDirX < 0 ? CommandKey.Left : CommandKey.Right); }
                if (dirX < 0) { Record(CommandKey.Left); }
                else if (dirX > 0) { Record(CommandKey.Right); }
            }
            if (dirY != _prevDirY)
            {
                if (_prevDirY != 0) { Release(_prevDirY < 0 ? CommandKey.Up : CommandKey.Down); }
                if (dirY < 0) { Record(CommandKey.Up); }
                else if (dirY > 0) { Record(CommandKey.Down); }
            }
            _prevDirX = dirX;
            _prevDirY = dirY;
            _held[(int)CommandKey.Left] = dirX < 0;
            _held[(int)CommandKey.Right] = dirX > 0;
            _held[(int)CommandKey.Up] = dirY < 0;
            _held[(int)CommandKey.Down] = dirY > 0;
            if (attackPressed) { Record(CommandKey.Attack); }
            if (jumpPressed) { Record(CommandKey.Jump); }
            if (skillPressed) { Record(CommandKey.Skill); }
            if (buffPressed) { Record(CommandKey.Buff); }
            while (_presses.Count > 0 && (_nowMs - _presses[0].TimeMs > HistoryMs || _presses.Count > MaxPresses))
            {
                _presses.RemoveAt(0);
            }
            while (_releases.Count > 0 && (_nowMs - _releases[0].TimeMs > HistoryMs || _releases.Count > MaxPresses))
            {
                _releases.RemoveAt(0);
            }
        }

        private void Release(CommandKey key)
        {
            _releases.Add(new Press { Key = key, TimeMs = _nowMs });
        }

        private void Record(CommandKey key)
        {
            _fresh[(int)key] = true;
            _presses.Add(new Press { Key = key, TimeMs = _nowMs });
        }

        /// <summary>这个键(绝对方向:Left 就是左键)在这一步被按下。</summary>
        public bool JustPressed(CommandKey key)
        {
            return _fresh[(int)key];
        }

        /// <summary>指令里的键按主角朝向换成实际按的键:朝左时 RIGHT(朝前)是左键、LEFT(朝后)是右键。</summary>
        public static CommandKey Absolute(CommandKey key, bool facingLeft)
        {
            if (!facingLeft)
            {
                return key;
            }
            if (key == CommandKey.Right) { return CommandKey.Left; }
            if (key == CommandKey.Left) { return CommandKey.Right; }
            return key;
        }

        /// <summary>忘掉之前按过的键(放出技能或换房间后用,免得同一串按键触发第二次)。</summary>
        public void Clear()
        {
            _presses.Clear();
            _releases.Clear();
            Array.Clear(_fresh, 0, KeyCount);
        }

        /// <summary>
        /// 这一步有没有刚好打出 <paramref name="command"/>(规则见类说明)。空指令永远不匹配。
        /// <paramref name="facingLeft"/> 是主角此刻的朝向(方向键相对朝向解释)。
        /// </summary>
        public bool Matches(SkillCommand command, bool facingLeft)
        {
            if (command == null || command.IsEmpty)
            {
                return false;
            }
            IReadOnlyList<CommandKey> keys = command.Keys;
            int n = keys.Count;
            CommandKey trigger = Absolute(keys[n - 1], facingLeft);
            if (!_fresh[(int)trigger])
            {
                return false;
            }
            if (n == 1)
            {
                return true;
            }
            uint used = 0;
            bool[] heldUsed = null;
            // 扳机自己的按下占掉它那一条记录(同一个键在序列里重复出现时不能共用)
            int own = Latest(trigger, _nowMs, used);
            if (own >= 0)
            {
                used |= 1u << own;
            }
            double next = _nowMs;
            for (int i = n - 2; i >= 0; i--)
            {
                CommandKey key = Absolute(keys[i], facingLeft);
                if (command.TogetherWithNext(i) && SkillCommand.IsDirection(key))
                {
                    // "同时"(分隔词 &):方向键此刻必须按着,只在以前点过一下不算
                    if (heldUsed == null)
                    {
                        heldUsed = new bool[KeyCount];
                    }
                    if (!HeldAt(key, next) || heldUsed[(int)key])
                    {
                        return false;
                    }
                    heldUsed[(int)key] = true;
                    continue;
                }
                int idx = Latest(key, next, used);
                if (idx >= 0 && next - _presses[idx].TimeMs <= WindowMs)
                {
                    used |= 1u << idx;
                    next = _presses[idx].TimeMs;
                    continue;
                }
                if (SkillCommand.IsDirection(key) && HeldAt(key, next, used))
                {
                    if (heldUsed == null)
                    {
                        heldUsed = new bool[KeyCount];
                    }
                    if (!heldUsed[(int)key])
                    {
                        heldUsed[(int)key] = true;
                        continue;          // 按住不放:满足这个键,不改变"前一个键要在多久之内"的基准
                    }
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// 方向键在 <paramref name="atMs"/> 那一刻是按着的:最近一次不晚于那一刻的按下之后、到那一刻为止没有松开过(同一步里松开又按别的键,算先松开)。
        /// 记录里这个键的按下都晚于那一刻,就是那时还没按;一条按下记录也没有时(按下太久,记录过期了),此刻还按着、
        /// 或者在那一刻之后才松开,都算那时按着。这样"后按下的键"不会倒过来满足序列里更早的位置,先按住、后来才松开的方向键也不会因为松得早而失效。
        /// <paramref name="used"/> 里的按下已经被序列后面的位置占用:占用的那一次按下不能再当"早就按着"的来源(M66,E-164)——
        /// 否则同一个键在序列里出现两次时,点一下就同时满足了两个位置("下、下、攻击"里只点一下下再按攻击也打得出来,而攻击键同时是普攻,会让每次"下 + 攻击"都变成姿态)。
        /// </summary>
        private bool HeldAt(CommandKey key, double atMs, uint used = 0)
        {
            double pressAt = double.NegativeInfinity;
            bool anyPress = false;
            for (int i = _presses.Count - 1; i >= 0; i--)
            {
                if (_presses[i].Key != key)
                {
                    continue;
                }
                anyPress = true;
                if ((used & (1u << i)) != 0)
                {
                    continue;
                }
                if (_presses[i].TimeMs <= atMs)
                {
                    pressAt = _presses[i].TimeMs;
                    break;
                }
            }
            if (double.IsNegativeInfinity(pressAt))
            {
                if (anyPress)
                {
                    return false;
                }
                if (_held[(int)key])
                {
                    return true;
                }
                foreach (Press r in _releases)
                {
                    if (r.Key == key && r.TimeMs > atMs)
                    {
                        return true;
                    }
                }
                return false;
            }
            foreach (Press r in _releases)
            {
                if (r.Key == key && r.TimeMs > pressAt && r.TimeMs <= atMs)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>时间不晚于 <paramref name="atMs"/> 的、没被占用的、这个键最近的一次按下;没有返回 -1。</summary>
        private int Latest(CommandKey key, double atMs, uint used)
        {
            for (int i = _presses.Count - 1; i >= 0; i--)
            {
                if (_presses[i].Key == key && _presses[i].TimeMs <= atMs && (used & (1u << i)) == 0)
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
