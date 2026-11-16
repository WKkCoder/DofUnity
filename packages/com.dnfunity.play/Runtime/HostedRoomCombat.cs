using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网多房间副本里宿主造房间会话的转手(r18-hostmulti):宿主(Unity 的 <c>Dnf.Client</c> 程序集)自己拼出一间房——出口按迷宫给、主角位置自己定,
    /// 不是按地图路径造默认房间(那条路是 <see cref="DemoCombat.CreateSession"/>)——之后要给它摆上怪物与战斗,也就是 <see cref="RoomCombatSession.Create"/>。
    /// 可那个方法的签名里有 <c>Dnf.Game</c> 程序集的类型(副本难度加成),而 <c>Dnf.Client.asmdef</c> 只引用 Data、Render、Play、Net 四个程序集,
    /// asmdef 的引用不传递:宿主里直接调它会因为"类型在未引用的程序集里"编译不过(CS0012,只有 Unity 编译得出来,
    /// 普通的 .NET 工程引用是传递的,<c>tools/typecheck-client.sh</c> 与 <c>dotnet test</c> 都发现不了)。所以从 Play 程序集里转一手。
    /// 只转手:参数原样交给 <see cref="RoomCombatSession.Create"/>,不碰别的,与离线副本、已有的联网房间同一条造法。
    /// </summary>
    public static class HostedRoomCombat
    {
        /// <summary>给一间已经拼好的房间摆上怪物与战斗。</summary>
        /// <param name="assets">数据资源。</param>
        /// <param name="room">拼好的房间会话。</param>
        /// <param name="seed">随机种子。</param>
        /// <param name="heroVitals">主角有没有生命值(<c>-dnfnodamage</c> 时没有)。</param>
        /// <param name="progress">主角的等级进度;null 是没有。</param>
        /// <param name="spawnPlan">出生点计划(联网宿主按服务端怪表的只数填的,见 <see cref="RoomSpawnPlan"/>);null 是房间地图自己的 [monster]。</param>
        public static RoomCombatSession CreateSession(DnfAssets assets, RoomSession room, int seed, bool heroVitals, HeroProgress progress,
            Func<RoomMap, IReadOnlyList<RoomMonsterSpawn>> spawnPlan)
        {
            return RoomCombatSession.Create(assets, room, seed, heroVitals, progress, spawnPlan: spawnPlan);
        }
    }
}
