using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>一张世界地图读出来的结果:副本选择界面的条目与画面数据。</summary>
    public sealed class WorldMapSet
    {
        public WorldMapDef Def { get; }
        public WorldMapScene Scene { get; }

        /// <summary>地图上能进的副本,按世界地图定义里 [dungeon] 的先后排。</summary>
        public IReadOnlyList<DungeonEntry> Entries { get; }

        public WorldMapSet(WorldMapDef def, WorldMapScene scene, IReadOnlyList<DungeonEntry> entries)
        {
            Def = def;
            Scene = scene;
            Entries = entries;
        }
    }

    /// <summary>
    /// 从世界地图定义(.wdm)、界面脚本(.ui)、副本列表(dungeon/dungeon.lst)与副本定义(.dgn)拼出副本选择界面的数据(证据 E-094):
    /// .wdm 的 [dungeon] 列出这张地图上的副本编号与解锁条件;只留下没有条件(-1)的;每个编号在 .ui 里要有对应的 [balloon] 控件(位置与气泡样式),
    /// 在 dungeon.lst 里要有路径,才进得了界面。条件不是 -1 的副本(推断是要先做任务)先不显示,条件的含义没读到。
    /// </summary>
    public static class WorldMapCatalog
    {
        public const string DungeonListPath = "dungeon/dungeon.lst";

        /// <summary>读 <paramref name="worldMapPath"/>(例如 worldmap/lorien.wdm)并造出界面数据;读不到这张地图时返回 null。</summary>
        public static WorldMapSet Load(DnfAssets assets, string worldMapPath)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            WorldMapDef def = WorldMapDef.Read(assets, worldMapPath);
            if (def == null)
            {
                return null;
            }
            UiScript ui = def.UiPath == null ? null : UiScript.Read(assets, def.UiPath);
            Dictionary<int, string> list = WorldMapDef.ParseList(assets.ReadScript(DungeonListPath), "dungeon/");
            return Build(def, ui, list, path => DungeonCardDef.Read(assets, path), assets);
        }

        /// <summary>
        /// 纯逻辑的部分(合成数据的测试直接调它):<paramref name="readDungeon"/> 按副本路径取副本定义(取不到可以返回 null,卡片就没有画);
        /// 副本编号或路径对不上的条目丢掉。
        /// </summary>
        public static WorldMapSet Build(WorldMapDef def, UiScript ui, IReadOnlyDictionary<int, string> dungeonList, Func<string, DungeonCardDef> readDungeon, ISpriteSource sprites)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            var markers = new Dictionary<int, UiControl>();
            if (ui != null)
            {
                foreach (UiControl control in ui.Controls)
                {
                    if (control.Type != "[balloon]")
                    {
                        continue;
                    }
                    List<int> p = control.Params();
                    if (p.Count > 0 && !markers.ContainsKey(p[p.Count - 1]))
                    {
                        markers.Add(p[p.Count - 1], control);
                    }
                }
            }
            var entries = new List<DungeonEntry>();
            foreach (WorldMapDungeon d in def.Dungeons)
            {
                if (!d.Unconditional || !markers.TryGetValue(d.DungeonId, out UiControl marker)
                    || dungeonList == null || !dungeonList.TryGetValue(d.DungeonId, out string path))
                {
                    continue;
                }
                List<int> p = marker.Params();
                int style = p.Count >= 3 ? p[p.Count - 2] : WorldMapArt.MeasuredBubbleStyle;
                DungeonCardDef card = readDungeon?.Invoke(path);
                entries.Add(new DungeonEntry(
                    path,
                    null,
                    new DungeonCard(d.DungeonId, marker.X, marker.Y, style, card?.SlotImage, card?.SlotFrame ?? 0, card?.MinimumLevel ?? 0)));
            }
            var scene = new WorldMapScene(sprites, def.MapImage, def.MapFrame, DungeonEntry.FromPath(def.Path));
            return new WorldMapSet(def, scene, entries);
        }
    }
}
