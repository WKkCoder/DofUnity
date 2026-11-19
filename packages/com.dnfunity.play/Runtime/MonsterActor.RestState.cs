using Dnf.Game.Act;

namespace Dnf.Play
{
    /// <summary>
    /// 怪物未决簇 1 在怪物宿主上的状态与消费点(客户端证据):
    /// <list type="bullet">
    /// <item>[SET VISIBLITY]:写入 <see cref="ActVisibilityByte"/>(出生为 1,0x007fb7a7),与 [IS VISIBLE] 共享。</item>
    /// <item>[SET GRABABLE]:写入可抓取标记(出生取 .mob [is grabable],缺省 1,0x012a5310);[IS GRABABLE] 读它。</item>
    /// <item>[SET DAMAGE CHECK]:累计受击窗口(0x00d1bc00 / 0x00d1bcb0 / 0x00d1bc60),供 .ai <c>damage check()</c> 消费。</item>
    /// <item>[SET UNBEATABLE]:主动对象无敌与时限(0x0081d8c0 / 0x0081d620 / 0x0081f8bd / 0x00d10af0),冻结受击框与受击,不改 [IS DAMAGEBOX ENABLE]。</item>
    /// <item>[SET WIDTH]:存下尺寸(客户端 +0x254/+0x260),这一版物理碰撞不读。</item>
    /// <item>[CHANGE TEAM]/[DEFAULT TEAM]:保存原队号、改队号、定时倒数与恢复(0x0098c2a0..0x0098c9c0);[SET PASSTYPE] 仅被动对象有效。</item>
    /// </list>
    /// </summary>
    public sealed partial class MonsterActor
    {
        private bool _actVisible = true;
        private bool _actGrabbable = true;

        private bool _dmgCheckActive;
        private int _dmgCheckDurMs;
        private double _dmgCheckElapsedMs;
        private int _dmgCheckAccum;

        private double _actClockMs;
        private bool _actUnbeatable;
        private double _unbeatableExpireClockMs;

        private int _actWidthX;
        private int _actWidthY;

        private bool _changeTeamActive;
        private int _changeTeamOrigTeam;
        private int _changeTeamHpPct;
        private IActEntity _changeTeamSource;
        private double _changeTeamLeftMs;
        private bool _changeTeamTimed;

        public int ActVisibilityByte => _actVisible ? 1 : 0;

        public bool IsActGrabbable => IsAlive && _actGrabbable;

        public bool IsActUnbeatable => _actUnbeatable;

        public bool DamageCheckActive => _dmgCheckActive;

        public int DamageCheckDurationMs => _dmgCheckDurMs;

        public int DamageCheckAccumulated => _dmgCheckAccum;

        public int ActWidthX => _actWidthX;

        public int ActWidthY => _actWidthY;

        /// <summary>从 .mob 的 [is grabable] 初始化出生可抓取标志(缺省为开,0x012a5310)。</summary>
        public void InitActGrabbable(bool grabbable)
        {
            _actGrabbable = grabbable;
        }

        private void AccumulateDamageCheck(int actualDamage)
        {
            if (actualDamage <= 0 || !_dmgCheckActive || _dmgCheckElapsedMs >= _dmgCheckDurMs)
            {
                return;
            }
            long sum = (long)_dmgCheckAccum + actualDamage;
            _dmgCheckAccum = sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        /// <summary>.ai 条件 damage check(threshold)(客户端 0x00d1bc60):不足返回 false;达到则清累计、重启计时器并返回 true。</summary>
        public bool CheckDamageThreshold(int threshold)
        {
            if (_dmgCheckAccum < threshold)
            {
                return false;
            }
            _dmgCheckAccum = 0;
            _dmgCheckElapsedMs = 0;
            return true;
        }

        private void AdvanceChangeTeamWallClock(double deltaMs)
        {
            if (!_changeTeamActive || !_changeTeamTimed)
            {
                return;
            }
            _changeTeamLeftMs -= deltaMs;
            if (_changeTeamLeftMs <= 0)
            {
                _changeTeamActive = false;
                _changeTeamLeftMs = 0;
                _changeTeamTimed = false;
                _changeTeamSource = null;
                SetActTeam(_changeTeamOrigTeam);
            }
        }

        /// <summary>推进随主动对象命中停顿冻结的逻辑状态([SET UNBEATABLE] 与 [SET DAMAGE CHECK] 时钟)。</summary>
        private void AdvanceRestState(double deltaMs)
        {
            _actClockMs += deltaMs;
            if (_dmgCheckActive)
            {
                _dmgCheckElapsedMs += deltaMs;
            }
            if (_actUnbeatable && _unbeatableExpireClockMs != 0 && _unbeatableExpireClockMs < _actClockMs)
            {
                _actUnbeatable = false;
                _unbeatableExpireClockMs = _actClockMs;
            }
        }

        private void NotifyActMoveCollision(int axis)
        {
            if (Phase == MonsterPhase.Attack)
            {
                _actRunner?.OnMoveCollision(axis);
            }
            FamilyOnMoveCollision(axis);
        }

        private sealed partial class ActBody : IActRestStateEntity
        {
            public int ActVisibilityByte => _m.ActVisibilityByte;

            public bool IsGrabbable => _m.IsActGrabbable;

            public bool IsActGhost => false;

            public int ActChargedTimeMs => 0;

            public void SetActVisibility(bool visible)
            {
                _m._actVisible = visible;
            }

            public void SetActGrabbable(bool grabbable)
            {
                _m._actGrabbable = grabbable;
            }

            public void SetActDamageCheck(bool on, int durationMs)
            {
                if (on)
                {
                    _m._dmgCheckActive = true;
                    _m._dmgCheckDurMs = durationMs;
                    _m._dmgCheckElapsedMs = 0;
                    _m._dmgCheckAccum = 0;
                }
                else
                {
                    _m._dmgCheckActive = false;
                    _m._dmgCheckDurMs = 0;
                    _m._dmgCheckElapsedMs = 0;
                    _m._dmgCheckAccum = 0;
                }
            }

            public void SetActUnbeatable(bool on, int durationMs)
            {
                if (on)
                {
                    _m._actUnbeatable = true;
                    _m._unbeatableExpireClockMs = _m._actClockMs + durationMs;
                }
                else
                {
                    _m._actUnbeatable = false;
                    _m._unbeatableExpireClockMs = _m._actClockMs;
                }
            }

            public void SetActPassType(int passType)
            {
                // 客户端 [SET PASSTYPE] 只作用被动对象(vtbl+0x30(0x21))
            }

            public void SetActWidth(int widthX, int widthY)
            {
                _m._actWidthX = widthX;
                _m._actWidthY = widthY;
            }

            public bool TryBeginActChangeTeam(int newTeam, int durationMs, int hpMaxPercent, IActEntity source, out int originalTeam)
            {
                if (_m._changeTeamActive)
                {
                    originalTeam = 0;
                    return false;
                }
                originalTeam = ((IActEntity)this).Team;
                _m._changeTeamActive = true;
                _m._changeTeamOrigTeam = originalTeam;
                _m._changeTeamHpPct = hpMaxPercent;
                _m._changeTeamSource = source;
                _m._changeTeamTimed = durationMs > 0;
                _m._changeTeamLeftMs = durationMs > 0 ? durationMs : 0;
                return true;
            }

            public bool TryEndActChangeTeam(out int originalTeam, out int hpMaxPercent, out IActEntity source)
            {
                if (!_m._changeTeamActive)
                {
                    originalTeam = 0;
                    hpMaxPercent = 0;
                    source = null;
                    return false;
                }
                _m._changeTeamActive = false;
                _m._changeTeamTimed = false;
                _m._changeTeamLeftMs = 0;
                originalTeam = _m._changeTeamOrigTeam;
                hpMaxPercent = _m._changeTeamHpPct;
                source = _m._changeTeamSource;
                _m._changeTeamSource = null;
                return true;
            }
        }
    }
}
