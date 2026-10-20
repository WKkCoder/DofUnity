using System;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 把城镇图册里的一块区域变成可玩的会话(证据 E-095):地图按"房间式内容"合成(背景、瓦片、图层、NPC、主角),
    /// 镜头范围 = 地图瓦片数 × 224 − 800(赛丽亚房间按这个式子算出的上限与原版连拍量到的一致),
    /// 主角出生点与朝向取自 <see cref="TownAtlas.Arrive"/>,镜头从最小值追上主角(<see cref="TownCamera.AtArrival"/>),出口触发器按地图的 [town movable area] 建。
    /// </summary>
    public static class TownWorld
    {
        public static TownSession CreateSession(DnfAssets assets, TownAtlas atlas, TownPlace place, AvatarOutfit outfit = null)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (atlas == null)
            {
                throw new ArgumentNullException(nameof(atlas));
            }
            TownAreaInfo info = atlas.GetArea(place.Town, place.Area)
                ?? throw new DnfFormatException("城镇图册里没有这块区域: " + place.Town + "/" + place.Area);
            TownArrival arrival = atlas.Arrive(place);
            outfit = outfit ?? DemoHero.CreateOutfit(assets);
            TownHero hero = TownHero.Create(assets, DemoHero.SwordmanChrPath, outfit, info.Walkable, arrival.X, arrival.Y, arrival.FacingLeft,
                TownHero.TownRestTag);
            RoomMap map = RoomMap.Read(assets, info.MapPath);
            RoomContent room = RoomContent.Load(assets, map);
            TownCamera camera = TownCamera.AtArrival(0, Math.Max(0, map.MaxCameraX), hero.X);
            var exits = new TownExitTracker(info.Exits, info.Bounds);
            return new TownSession(assets, info.MapPath, hero, camera, TownSession.DefaultGroundY, room, exits);
        }
    }
}
