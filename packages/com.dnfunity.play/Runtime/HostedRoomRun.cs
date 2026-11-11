using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网副本的房间托管(r15-dungeonhost):把一间已经造好的房间战斗会话(<see cref="RoomCombatSession"/>,怪物、战斗、画面、声音全是它自己的,
    /// 不另造一套)包成流程(<see cref="GameFlow"/>)认得的"一局副本"(<see cref="IRunSession"/>)。它只多做两件事:
    /// <list type="number">
    /// <item>每推进一步,用 <see cref="RoomKillWatcher"/> 看房间里有哪些出生点怪新死了,把它们的序号(<see cref="MonsterActor.Index"/>)逐个交给 <c>reportKill</c>——
    /// 每只怪只交一次,脚本召唤的怪不交。序号的含义由造房间的一方保证(联网宿主用 <see cref="RoomSpawnPlan"/> 造房间,序号就是联网对应表里的本地序号);
    /// 这里不认识联网层,<c>reportKill</c> 是宿主给的委托(返回有没有收下)。</item>
    /// <item>外面说"这一局结束了"(<c>finished</c> 返回 true,联网宿主给的是"结算收齐了")之后,再过 <c>returnDelayMs</c> 毫秒,
    /// <see cref="Choice"/> 变成 <see cref="SettlementChoice.ReturnToTown"/>,流程据此回城镇(<see cref="GameFlow.Poll"/> 本来的逻辑,没有为它改一个字)。
    /// 在 <c>finished</c> 为 true 之前 <see cref="Choice"/> 一直是 <see cref="SettlementChoice.None"/>:本地怪杀光不等于可以回城,回不回由外面说了算。</item>
    /// </list>
    /// 其余成员(画面、操作、声音、物品)原样转给房间会话。不是线程安全的,与房间会话一样只在宿主主线程用;两个委托也在主线程被调用。
    /// 主角死亡:房间会话默认没有复活窗口(<see cref="RoomCombatSession.Revive"/> 为 null),<see cref="Failed"/> 只在宿主给了复活窗口并走完失败计时时为真。
    /// </summary>
    public sealed class HostedRoomRun : ICommandSession, ISoundSource, IItemSession, IRunSession
    {
        private readonly RoomCombatSession _room;
        private readonly RoomKillWatcher _watcher;
        private readonly Func<int, bool> _reportKill;
        private readonly Func<bool> _finished;
        private readonly double _returnDelayMs;
        private readonly List<int> _kills = new List<int>();
        private double _waitedMs;

        /// <param name="room">已经造好的房间会话(联网宿主用 <see cref="RoomCombatSession.Create"/> 的 <c>spawnPlan</c> 造)。</param>
        /// <param name="reportKill">交出一只怪的序号;返回 true 表示收下了,false 表示不收(例如联网战斗已经结束)。</param>
        /// <param name="finished">这一局是不是结束了(例如联网结算收齐);每步查一次,为 true 过一次就记住,不会再变回去。</param>
        /// <param name="returnDelayMs">看到结束之后再等多久才回城(毫秒,留给玩家看一眼清空的房间);负数当作 0。</param>
        /// <exception cref="ArgumentNullException">任何一个参数为 null。</exception>
        public HostedRoomRun(RoomCombatSession room, Func<int, bool> reportKill, Func<bool> finished, double returnDelayMs = 0.0)
        {
            _room = room ?? throw new ArgumentNullException(nameof(room));
            _reportKill = reportKill ?? throw new ArgumentNullException(nameof(reportKill));
            _finished = finished ?? throw new ArgumentNullException(nameof(finished));
            _returnDelayMs = returnDelayMs > 0.0 ? returnDelayMs : 0.0;
            _watcher = new RoomKillWatcher(room);
        }

        /// <summary>被托管的房间会话(宿主的日志与底栏读它;战斗本身只在它里面)。</summary>
        public RoomCombatSession Room => _room;

        /// <summary>交给 <c>reportKill</c> 并被收下的击杀数(每只怪最多一次)。</summary>
        public int Reported { get; private set; }

        /// <summary>交给 <c>reportKill</c> 但没被收下的击杀数(例如联网战斗已经结束):观察者不会再交第二遍,它们就此丢掉。</summary>
        public int Dropped { get; private set; }

        /// <summary><c>finished</c> 已经为 true 过(流程即将在 <c>returnDelayMs</c> 之后回城)。</summary>
        public bool Finished { get; private set; }

        /// <summary>结算选定的项:结束之后过了 <c>returnDelayMs</c> 是 <see cref="SettlementChoice.ReturnToTown"/>,在此之前是 <see cref="SettlementChoice.None"/>。</summary>
        public SettlementChoice Choice { get; private set; }

        /// <summary>副本失败:房间会话有复活窗口(宿主给的)并且失败计时走完了;没有复活窗口永远为假。</summary>
        public bool Failed => _room.Revive != null && _room.Revive.Failed;

        // —— IHeroSession / ISceneSource:原样转给房间会话 ——

        public string MapPath => _room.MapPath;

        public TownHero Hero => _room.Hero;

        public TownCamera Camera => _room.Camera;

        public int LayerCount => _room.LayerCount;

        public int NpcCount => _room.NpcCount;

        public List<SceneSprite> Build(long timeMs)
        {
            return _room.Build(timeMs);
        }

        // —— 推进:先让房间会话走一步,再盯击杀与结束 ——

        public void Update(double deltaMs, int dirX, int dirY)
        {
            _room.Update(deltaMs, dirX, dirY);
            Step(deltaMs);
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            _room.Update(deltaMs, dirX, dirY, attackPressed, jumpPressed);
            Step(deltaMs);
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            _room.Update(deltaMs, dirX, dirY, attackPressed, jumpPressed, skillsPressed);
            Step(deltaMs);
        }

        public SkillKeys SkillsHeld
        {
            get { return _room.SkillsHeld; }
            set { _room.SkillsHeld = value; }
        }

        public bool CommandsEnabled
        {
            get { return _room.CommandsEnabled; }
            set { _room.CommandsEnabled = value; }
        }

        public void DrainSounds(List<SoundCue> into)
        {
            _room.DrainSounds(into);
        }

        public UseResult TryUseQuickSlot(int slot)
        {
            return _room.TryUseQuickSlot(slot);
        }

        public UseResult TryUseItem(ItemInfo item)
        {
            return _room.TryUseItem(item);
        }

        private void Step(double deltaMs)
        {
            // 先交这一步新死的怪,再看结束:同一步里"最后一只怪的击杀"一定排在"回城"之前
            _kills.Clear();
            _watcher.Poll(_kills);
            for (int i = 0; i < _kills.Count; i++)
            {
                if (_reportKill(_kills[i]))
                {
                    Reported++;
                }
                else
                {
                    Dropped++;
                }
            }

            if (Choice != SettlementChoice.None)
            {
                return;
            }
            if (!Finished && _finished())
            {
                Finished = true;
            }
            if (Finished)
            {
                _waitedMs += deltaMs > 0.0 ? deltaMs : 0.0;
                if (_waitedMs >= _returnDelayMs)
                {
                    Choice = SettlementChoice.ReturnToTown;
                }
            }
        }
    }
}
