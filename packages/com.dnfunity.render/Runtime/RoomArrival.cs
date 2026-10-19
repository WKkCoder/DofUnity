using System;

namespace Dnf.Render
{
    /// <summary>一次进房间的落点计算结果(证据 E-074)。</summary>
    public readonly struct ArrivalPoint
    {
        /// <summary>矩形里取到的随机点(还没检查站不站得下)。</summary>
        public int RandomX { get; }

        public int RandomY { get; }

        /// <summary>最终落点:随机点站得下就是它,否则是最近的可站点;半径 200 以内没有可站点时(<see cref="Found"/> 为 false)是随机点挪到最近的可走点。</summary>
        public int X { get; }

        public int Y { get; }

        /// <summary>找到了站得下的点(随机点本身或半径 200 以内最近的一个)。</summary>
        public bool Found { get; }

        public ArrivalPoint(int randomX, int randomY, int x, int y, bool found)
        {
            RandomX = randomX;
            RandomY = randomY;
            X = x;
            Y = y;
            Found = found;
        }
    }

    /// <summary>
    /// 主角进房间时落在哪(证据 E-074):第一个房间落在 [dungeon start area] 里;从出口进来落在"进来的那个出口"的识别矩形里(<see cref="GateAreas"/>)。
    /// 两种都是 <see cref="GateArrival.RandomPoint"/> 取随机点(种子 = <see cref="GateArrival.SeedFor"/>(主角对象编号, 地图编号))、
    /// 站不下就 <see cref="GateArrival.NearestFree"/>;"站得下"= 主角占位盒(.chr 的 [width])在舞台格(<see cref="RoomSession.Grid"/>:瓦片通行表 + 盖上的出口门体)上
    /// 没压着非 0 的格子,且中心在房间的可走区域里(障碍物有没有也盖在舞台格上没有查明,先当作站不下)。
    /// 朝向:从左出口进来朝右、从右出口进来朝左,上下出口保持原来的朝向(客户端设朝向的函数参数 1 = 朝右是由"从左进来要朝里"推出的,没有直接读到参数含义)。
    /// </summary>
    public static class RoomArrival
    {
        /// <summary>从 <paramref name="entry"/> 侧的出口进房间的落点;房间地图没有这个出口的位置(没有识别矩形)时返回 null。</summary>
        public static ArrivalPoint? ComputeAtGate(RoomSession room, GateSide entry, int mapId, int heroObjectId)
        {
            if (room == null)
            {
                throw new ArgumentNullException(nameof(room));
            }
            WorldRect? area = room.Map.GateRects[(int)entry];
            return area.HasValue ? Compute(room, area.Value, mapId, heroObjectId) : (ArrivalPoint?)null;
        }

        /// <summary>第一个房间(没有来路)的落点:[dungeon start area] 里;地图没有出生区时返回 null。</summary>
        public static ArrivalPoint? ComputeAtStart(RoomSession room, int mapId, int heroObjectId)
        {
            if (room == null)
            {
                throw new ArgumentNullException(nameof(room));
            }
            WorldRect? area = room.Map.StartArea;
            return area.HasValue ? Compute(room, area.Value, mapId, heroObjectId) : (ArrivalPoint?)null;
        }

        /// <summary>把主角摆到 <paramref name="entry"/> 侧出口的落点;朝向按规则(左出口进朝右、右出口进朝左,上下保持 <paramref name="carryFacingLeft"/>)。没有识别矩形时不动主角并返回 false。</summary>
        public static bool PlaceAtGate(RoomSession room, GateSide entry, int mapId, int heroObjectId, bool carryFacingLeft)
        {
            ArrivalPoint? p = ComputeAtGate(room, entry, mapId, heroObjectId);
            if (!p.HasValue)
            {
                return false;
            }
            bool facingLeft = entry == GateSide.Left ? false : entry == GateSide.Right ? true : carryFacingLeft;
            room.Hero.Place(p.Value.X, p.Value.Y, facingLeft, HeroMotion.Rest, 0);
            return true;
        }

        /// <summary>把主角摆到第一个房间的落点(朝右)。地图没有出生区时不动主角并返回 false。</summary>
        public static bool PlaceAtStart(RoomSession room, int mapId, int heroObjectId)
        {
            ArrivalPoint? p = ComputeAtStart(room, mapId, heroObjectId);
            if (!p.HasValue)
            {
                return false;
            }
            room.Hero.Place(p.Value.X, p.Value.Y, false, HeroMotion.Rest, 0);
            return true;
        }

        private static ArrivalPoint Compute(RoomSession room, WorldRect area, int mapId, int heroObjectId)
        {
            TownHero hero = room.Hero;
            RoomGrid grid = room.Grid;
            int bodyWidth = hero.BodyWidth, bodyDepth = hero.BodyDepth;
            uint seed = GateArrival.SeedFor(heroObjectId, mapId);
            GateArrival.RandomPoint(area, bodyWidth, bodyDepth, ref seed, out int rx, out int ry);
            bool found = GateArrival.NearestFree((px, py) => grid.IsFree(px, py, bodyWidth, bodyDepth) && hero.Walk.Contains(px, py),
                rx, ry, GateArrival.NearestFreeRadius, GateArrival.NearestFreeStep, out int x, out int y);
            if (!found)
            {
                room.Map.NearestWalkable(rx, ry, out double wx, out double wy);
                x = (int)Math.Round(wx);
                y = (int)Math.Round(wy);
            }
            return new ArrivalPoint(rx, ry, x, y, found);
        }
    }
}
