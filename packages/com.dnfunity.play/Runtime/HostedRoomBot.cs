using System;

namespace Dnf.Play
{
    /// <summary>联网房间自动机(<see cref="HostedRoomBot"/>)停手的原因。</summary>
    public enum HostedBotStop
    {
        /// <summary>还没停手,在打。</summary>
        None = 0,

        /// <summary>联网结算收齐(<see cref="HostedRoomRun.Finished"/>)或结算选项已选定:这一局该做的做完了。自动机不再出手,时间照走,流程在回城延迟之后回城。</summary>
        Settled = 1,

        /// <summary>副本失败:宿主给了复活窗口、失败计时走完了(<see cref="HostedRoomRun.Failed"/>)。流程据此让主角回城。</summary>
        Failed = 2,

        /// <summary>主角倒下而房间没有复活窗口(<see cref="RoomCombatSession.Revive"/> 为 null):再也站不起来,打不下去了。</summary>
        HeroDown = 3,

        /// <summary>整段时间用完了(<see cref="HostedRoomBot.TotalLimitMs"/>),结算还没来。</summary>
        TimedOut = 4,

        /// <summary>房间里还有活着的怪,但连续 <see cref="HostedRoomBot.NoProgressLimitMs"/> 没有任何进展(没打中、没打死、没上报、没打翻物件):走不到、打不着。</summary>
        NoProgress = 5,

        /// <summary>
        /// 本地的怪已经全部打死(也都交了),但联网结算连续 <see cref="HostedRoomBot.NoProgressLimitMs"/> 没有来:服务端没有清图
        /// (多个房间的副本只在最后一间清图,而这里只托管一间)或这边与服务端对不上(起始地图的怪记录有本地造不出来的)。
        /// </summary>
        NoSettlement = 6,
    }

    /// <summary>
    /// 联网房间(<see cref="HostedRoomRun"/>)里的无人值守自动机(r16-onlinebot):让既有的自动机(<see cref="DungeonBot"/>)在联网房间里也能打,
    /// 免得批量对真服跑 <c>-dnfbot</c> 时房间里没有人动、击杀包一个也发不出去。
    /// <list type="bullet">
    /// <item>决策不新写:找最近打得到的怪、走位、寻路、出手间隔、主角死了的处理都是 <see cref="DungeonBot.NextInRoom"/>,与离线(<see cref="DungeonBot.Next"/>)是同一份代码、同样的数。</item>
    /// <item>节奏不新写:与 <see cref="DungeonBot.Advance"/> 一样按固定的 1/60 秒心跳补步(一帧最多补 <see cref="DungeonBot.MaxBeatsPerFrame"/> 个、欠账不超过 <see cref="DungeonBot.MaxDebtMs"/>),
    /// 同一局不管帧长是多少打出的是同一场;每个心跳让 <see cref="HostedRoomRun"/> 走一步(本地怪死了由它按序号交给联网战斗,交几次、报什么都是它的事,这里不碰)。</item>
    /// <item>联网房间里主角没有复活窗口(默认 <see cref="RoomCombatSession.Revive"/> 为 null):打不过就躺到底。所以自动机带上限,绝不无限挂机,到了上限就停手(<see cref="Reason"/>、<see cref="LogLine"/> 写明):
    /// <see cref="HostedBotStop.Settled"/> 结算收齐;<see cref="HostedBotStop.HeroDown"/> 主角倒下且没有复活窗口;<see cref="HostedBotStop.TimedOut"/> 整段超时;
    /// <see cref="HostedBotStop.NoProgress"/> 还有怪但长时间没有进展;<see cref="HostedBotStop.NoSettlement"/> 怪都死了结算却不来;<see cref="HostedBotStop.Failed"/> 宿主给的复活窗口走完失败计时。
    /// 上限是有限的正数,构造时就拒绝 0、负数、无穷与 NaN。时间按 <see cref="Advance"/> 收到的毫秒累计(宿主把一帧的时间乘上 <c>-dnftimescale</c> 之后再封顶 100 毫秒),不是墙钟。</item>
    /// <item>停手之后不再出手也不再走位,但时间照走(<see cref="HostedRoomRun.Update(double, int, int, bool, bool)"/> 喂 0 方向):结算收齐之后要等回城延迟走完,流程才回城镇。</item>
    /// <item>与 <c>-dnfnodamage</c> 的关系按既有语义:主角没有生命值,永远不会倒下(<see cref="HostedBotStop.HeroDown"/> 不会出现),怪物打不到他,上限照常生效。</item>
    /// </list>
    /// 一个实例只管一局(造房间的宿主每局新造一个);不是线程安全的,与房间会话一样只在宿主主线程用。它只是测试工具,不代表原版的 AI,也不绕过任何官方服务。
    /// </summary>
    public sealed class HostedRoomBot
    {
        /// <summary>日志行首:与联网房间接线的日志(<c>DNF_NET stage=room …</c>)同一族;行里只有状态名与只数,没有任何服务端编号、地图或坐标。</summary>
        public const string LogPrefix = "DNF_NET stage=room bot";

        /// <summary>整段上限的缺省值(毫秒):10 分钟。一间房的战斗再肉也该在这个时间内有结果;超了就是打不动。</summary>
        public const double DefaultTotalLimitMs = 600000.0;

        /// <summary>无进展上限的缺省值(毫秒):1 分钟。走到怪跟前、够着开打的路程只有几秒,一分钟里一击也没命中、一只怪也没打死,就是打不到。</summary>
        public const double DefaultNoProgressLimitMs = 60000.0;

        private readonly DungeonBot _bot = new DungeonBot(false);
        private double _debtMs;
        private long _progressMark = -1;
        private double _progressAtMs;
        private bool _logTaken;

        // 最近一次检查时的现状(给日志用)
        private int _reported;
        private int _dropped;
        private int _alive;
        private int _hits;
        private int _kills;
        private bool _heroDown;

        /// <param name="totalLimitMs">整段上限(毫秒):自动机从第一次推进起累计这么久结算还没来就停手。</param>
        /// <param name="noProgressLimitMs">无进展上限(毫秒):连续这么久没有任何进展(命中、击杀、上报、打翻物件)就停手。</param>
        /// <exception cref="ArgumentOutOfRangeException">任一上限不是有限的正数(不许无限挂机)。</exception>
        public HostedRoomBot(double totalLimitMs = DefaultTotalLimitMs, double noProgressLimitMs = DefaultNoProgressLimitMs)
        {
            if (!(totalLimitMs > 0.0) || double.IsInfinity(totalLimitMs))
            {
                throw new ArgumentOutOfRangeException(nameof(totalLimitMs), totalLimitMs, "整段上限必须是有限的正数(自动机不许无限挂机)");
            }
            if (!(noProgressLimitMs > 0.0) || double.IsInfinity(noProgressLimitMs))
            {
                throw new ArgumentOutOfRangeException(nameof(noProgressLimitMs), noProgressLimitMs, "无进展上限必须是有限的正数(自动机不许无限挂机)");
            }
            TotalLimitMs = totalLimitMs;
            NoProgressLimitMs = noProgressLimitMs;
        }

        /// <summary>整段上限(毫秒)。</summary>
        public double TotalLimitMs { get; }

        /// <summary>无进展上限(毫秒)。</summary>
        public double NoProgressLimitMs { get; }

        /// <summary>清房之后顺手打翻房间里的可破坏物件(与 <see cref="DungeonBot.SmashObjects"/> 同一个开关,默认关)。</summary>
        public bool SmashObjects
        {
            get { return _bot.SmashObjects; }
            set { _bot.SmashObjects = value; }
        }

        /// <summary>停手的原因;<see cref="HostedBotStop.None"/> 是还在打。停手之后不再变。</summary>
        public HostedBotStop Reason { get; private set; }

        /// <summary>已经停手(不再出手也不再走位,时间照走)。</summary>
        public bool Stopped => Reason != HostedBotStop.None;

        /// <summary>自动机收到的时间累计(毫秒;停手之后不再累计)。整段上限与无进展上限都按它算。</summary>
        public double ElapsedMs { get; private set; }

        /// <summary>已经做过决策的心跳数(与 <see cref="DungeonBot.Steps"/> 同一个数)。</summary>
        public int Steps => _bot.Steps;

        /// <summary>最近一步的意图(诊断用,见 <see cref="DungeonBot.Intent"/>)。</summary>
        public string Intent => _bot.Intent;

        /// <summary>
        /// <c>-dnfnetbot</c> 开关要不要生效:它只在 <c>-dnfbot</c>(自动机)、<c>-dnfnet</c>(联网)、<c>-dnfnetdungeon</c>(联网进副本)三者齐全时才有意义。
        /// 返回 null 表示三者齐全、可以生效;否则返回缺的第一项的原因(<c>NeedsDnfbot</c>、<c>NeedsDnfnet</c>、<c>NeedsDnfnetdungeon</c>),宿主把它写进日志(与
        /// <c>DNF_NET stage=dungeon ignored reason=NeedsDnfnet</c> 同一个风格)、开关当没带。这样没有联网进副本时自动机的行为一字不变。
        /// </summary>
        public static string IgnoredReason(bool botOn, bool netMode, bool netDungeon)
        {
            if (!botOn)
            {
                return "NeedsDnfbot";
            }
            if (!netMode)
            {
                return "NeedsDnfnet";
            }
            return netDungeon ? null : "NeedsDnfnetdungeon";
        }

        /// <summary>
        /// 过去了一帧、<paramref name="deltaMs"/> 毫秒:没停手时按固定心跳补步(每个心跳先 <see cref="DungeonBot.NextInRoom"/> 决策、再让 <paramref name="run"/> 走 <see cref="CombatScript.StepMs"/>,
        /// 每步之后查一遍停手条件,停了就不再补)并返回这一帧补了几个心跳;已经停手就只让时间走(方向 0、不按键),返回 0。负的时间当 0。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="run"/> 为 null。</exception>
        public int Advance(HostedRoomRun run, double deltaMs)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }
            double delta = deltaMs > 0.0 ? deltaMs : 0.0;
            if (Reason == HostedBotStop.None)
            {
                ElapsedMs += delta;
                Check(run);
            }
            if (Reason != HostedBotStop.None)
            {
                // 停手:主角站着不动,只让时间走(结算收齐之后要等回城延迟);一帧最多走 MaxDebtMs,卡了几秒的帧不会一步跨过去(与按心跳补步同一个规矩,也防住荒谬的帧长)
                run.Update(Math.Min(delta, DungeonBot.MaxDebtMs), 0, 0, false, false);
                return 0;
            }
            _debtMs = Math.Min(_debtMs + delta, DungeonBot.MaxDebtMs);
            int beats = 0;
            while (beats < DungeonBot.MaxBeatsPerFrame && _debtMs >= CombatScript.StepMs && Reason == HostedBotStop.None)
            {
                bool attack = _bot.NextInRoom(run.Room, out int dirX, out int dirY);
                run.Update(CombatScript.StepMs, dirX, dirY, attack, false);
                _debtMs -= CombatScript.StepMs;
                beats++;
                Check(run);
            }
            return beats;
        }

        /// <summary>
        /// 一行日志:<c>DNF_NET stage=room bot state=running|stopped reason=… steps=… elapsedMs=… reported=… dropped=… alive=… hits=… kills=… hero=up|down</c>。
        /// 只有状态名与只数(取自最近一次检查),没有服务端编号、地图与坐标。
        /// </summary>
        public string LogLine()
        {
            return LogPrefix + " state=" + (Stopped ? "stopped" : "running") + " reason=" + Reason + " steps=" + Steps + " elapsedMs=" + (long)ElapsedMs
                + " reported=" + _reported + " dropped=" + _dropped + " alive=" + _alive + " hits=" + _hits + " kills=" + _kills + " hero=" + (_heroDown ? "down" : "up");
        }

        /// <summary>
        /// 宿主该写的那一行日志,只给一次:自动机停手之后第一次来取,或者 <paramref name="atQuit"/>(宿主收尾、自动机还在打,留一行它当时的状态)时来取,给 <see cref="LogLine"/>;
        /// 其余时候(还在打又不是收尾、已经给过)返回 null。"只写一次"放在这里(可测),宿主只管转发。
        /// </summary>
        public string TakeLogLine(bool atQuit)
        {
            if (_logTaken || (!Stopped && !atQuit))
            {
                return null;
            }
            _logTaken = true;
            return LogLine();
        }

        /// <summary>读一遍现状、刷新"最近一次有进展"的时刻,再按优先级判断要不要停手:失败、结算收齐、主角倒下无复活窗口、整段超时、长时间无进展。</summary>
        private void Check(HostedRoomRun run)
        {
            RoomCombatSession room = run.Room;
            _reported = run.Reported;
            _dropped = run.Dropped;
            _alive = room.AliveCount;
            _hits = room.Hits;
            _kills = room.Kills;
            _heroDown = room.HeroDead;
            // 进展 = 命中、击杀、上报(或被拒收)、打翻物件里任何一个计数动了;血量再厚的怪只要一直在挨打就不算"没进展",由整段上限兜底
            long mark = (long)room.Hits + room.Kills + room.ObjectsDestroyed + run.Reported + run.Dropped;
            if (mark != _progressMark)
            {
                _progressMark = mark;
                _progressAtMs = ElapsedMs;
            }
            if (run.Failed)
            {
                Reason = HostedBotStop.Failed;
            }
            else if (run.Finished || run.Choice != SettlementChoice.None)
            {
                Reason = HostedBotStop.Settled;
            }
            else if (room.HeroDead && room.Revive == null)
            {
                Reason = HostedBotStop.HeroDown;
            }
            else if (ElapsedMs >= TotalLimitMs)
            {
                Reason = HostedBotStop.TimedOut;
            }
            else if (ElapsedMs - _progressAtMs >= NoProgressLimitMs)
            {
                Reason = _alive > 0 ? HostedBotStop.NoProgress : HostedBotStop.NoSettlement;
            }
        }
    }
}
