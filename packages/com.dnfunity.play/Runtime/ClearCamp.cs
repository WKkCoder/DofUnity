using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game.Act;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 通关之后首领房里的营地(证据 E-091):主角脚下生起一堆营火,房间地图 [NPC] 里写着的"通关时才出现"的 NPC(修理商德利拉,
    /// 她的 .npc 带 [visible on dungeon clear] 1)站在营火旁边。
    /// 数据链:营火是 <c>passiveobject/actionobject/common/campfire.obj</c> 的 [basic action](<see cref="CampfireAction"/>):
    /// 一个 [MOTION],[BASE ANI] 是木柴出现,[SUB ANI] 依次是火出现、木柴、火焰循环、加法混合的光晕(<c>Interface/EPLP.img</c>),各自的偏移都是 (0, 0);
    /// NPC 是地图 [NPC] 列表里编号 → <c>npc.lst</c> → .npc 的 [field animation]。
    /// 营地的**位置是粗测**(证据 E-092):基线里只有结算画面的几帧(营火与德利拉被头顶标签与掉落物遮挡)。观察:营火原点与主角的脚下重合(主角就站在营火里),
    /// 德利拉的脚线与营火同高、在营火左边约 100 像素(用她的待机帧镜像后与基线逐通道回归对位,相关系数 0.92;营火原点由木柴图的位置反推,
    /// 木柴被火光与名牌遮挡、相关系数只有 0.3,所以横向误差在 ±5 像素量级);地图里营地 NPC 的坐标写的是 (0, 0, 0),原版是运行时安排的位置,规则没有读到。
    /// 图层:营火各层(木柴、火、光晕)都画在整屏变暗层之上(<see cref="FireRank"/>)——基线上火芯亮到 255、没被压暗;德利拉在两层变暗层之间(见 <see cref="DungeonSession"/>)。
    /// 木柴的出现与火焰的淡入来自动画自己的帧(第一帧 alpha 为 0、向下一帧插值),所以营火是"生"出来的;德利拉没有出现动画,直接站在那里。
    /// 没有做:掉落物与它们的名牌、德利拉的对话与修理、营火的声音。
    /// </summary>
    public sealed class ClearCamp
    {
        /// <summary>营火的动作脚本(.obj 的 [basic action],相对 passiveobject/actionobject/common/)。</summary>
        public const string CampfireAction = "passiveobject/actionobject/common/action/campfire.act";

        /// <summary>地图里写 (0, 0, 0) 的营地 NPC 站在营火旁:相对营火的偏移(像素;基线结算帧上粗测,见类说明)。</summary>
        public const int NpcOffsetX = -100;

        public const int NpcOffsetY = 0;

        /// <summary>第二个及之后的营地 NPC 再往左错开多少(全库只有德利拉与几个加百列,不会同时出现多个;近似)。</summary>
        public const int NpcSpacing = 70;

        /// <summary>营火各层的图层大类:整屏变暗层的大类(<see cref="OverlayArt.DimRank"/>),画在变暗层之后,所以不被压暗,也盖在主角前面(基线上主角被火与木柴挡住)。</summary>
        public const int FireRank = OverlayArt.DimRank;

        /// <summary>NPC 的排序偏移:与主角同高时画在主角后面。</summary>
        public const int NpcSortBias = 0;

        private readonly List<AnimatedEffect> _fire = new List<AnimatedEffect>();
        private readonly List<AnimatedEffect> _npcs = new List<AnimatedEffect>();

        /// <summary>营火的落点(世界坐标,脚下)。</summary>
        public double X { get; }

        public double Y { get; }

        /// <summary>营火用到的动画层数(木柴出现、火出现、木柴、火焰、光晕共 5 层);读不到动作脚本时为 0。</summary>
        public int FireLayers => _fire.Count;

        /// <summary>站在营地里的 NPC 个数。</summary>
        public int NpcCount => _npcs.Count;

        /// <summary>营地已经存在多久(毫秒)。</summary>
        public double AgeMs { get; private set; }

        private ClearCamp(double x, double y)
        {
            X = x;
            Y = y;
        }

        /// <summary>
        /// 在 (<paramref name="x"/>, <paramref name="y"/>) 生营火,并把 <paramref name="mapPath"/> 的 [NPC] 里"通关时才出现"的 NPC 摆在它旁边。
        /// 素材缺失的部分安静地略过(合成数据的测试里没有营火素材,结果就是一个空营地)。
        /// </summary>
        public static ClearCamp Create(DnfAssets assets, string mapPath, double x, double y)
        {
            var camp = new ClearCamp(x, y);
            if (assets == null)
            {
                return camp;
            }
            camp.AddCampfire(assets);
            camp.AddNpcs(assets, mapPath);
            return camp;
        }

        private void AddCampfire(DnfAssets assets)
        {
            ScriptLine[] doc = assets.ReadScript(CampfireAction);
            if (doc == null)
            {
                return;
            }
            ActNode motion = null;
            foreach (ActNode root in ActParser.Parse(doc).Roots)
            {
                if (root.Is("[MOTION]"))
                {
                    motion = root;
                    break;
                }
            }
            if (motion == null)
            {
                return;
            }
            string basePath = motion.Find("[BASE ANI]")?.TextAt(0);
            if (basePath != null)
            {
                AddFire(assets, PvfPath.ResolveRelativeTo(CampfireAction, basePath), 0, 0);
            }
            ActNode sub = motion.Find("[SUB ANI]");
            if (sub != null)
            {
                // 值是 "路径 x y" 一组一组的
                for (int i = 0; i + 2 < sub.Values.Length; i += 3)
                {
                    string path = sub.TextAt(i);
                    if (path != null)
                    {
                        AddFire(assets, PvfPath.ResolveRelativeTo(CampfireAction, path), sub.IntAt(i + 1), sub.IntAt(i + 2));
                    }
                }
            }
        }

        private void AddFire(DnfAssets assets, string aniPath, int dx, int dy)
        {
            AnimatedEffect e = AnimatedEffect.Create(assets, aniPath, X + dx, Y + dy, 0, false, 0, persistent: true, layerRank: FireRank);
            if (e != null)
            {
                _fire.Add(e);
            }
        }

        private void AddNpcs(DnfAssets assets, string mapPath)
        {
            if (string.IsNullOrEmpty(mapPath))
            {
                return;
            }
            List<TownNpc> npcs;
            Dictionary<int, string> catalog;
            try
            {
                npcs = TownScene.ReadMapNpcs(assets, mapPath);
                catalog = TownScene.ReadNpcCatalog(assets);
            }
            catch (DnfFormatException)
            {
                return;
            }
            int placed = 0;
            foreach (TownNpc npc in npcs)
            {
                if (!catalog.TryGetValue(npc.Id, out string file))
                {
                    continue;
                }
                string script = PvfPath.ResolveRelativeTo("npc/", file);
                if (!IsVisibleOnDungeonClear(assets, script))
                {
                    continue;
                }
                string aniPath = TownScene.FieldAnimationPath(assets, script);
                if (aniPath == null)
                {
                    continue;
                }
                bool auto = npc.X == 0 && npc.Y == 0 && npc.Z == 0;
                double nx = auto ? X + NpcOffsetX - placed * NpcSpacing : npc.X;
                double ny = auto ? Y + NpcOffsetY : npc.Y;
                AnimatedEffect e = AnimatedEffect.Create(assets, aniPath, nx, ny, auto ? 0 : npc.Z, npc.FacingLeft, NpcSortBias, persistent: true);
                if (e != null)
                {
                    _npcs.Add(e);
                    placed++;
                }
            }
        }

        /// <summary>.npc 有没有 [visible on dungeon clear] 1(全库只有德利拉与三个加百列带它)。</summary>
        public static bool IsVisibleOnDungeonClear(DnfAssets assets, string npcScriptPath)
        {
            ScriptLine[] doc = assets.ReadScript(npcScriptPath);
            if (doc == null)
            {
                return false;
            }
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[visible on dungeon clear]"))
                {
                    return line.Values.Length > 0 && line.Values[0].IsNumber && line.Values[0].Int != 0;
                }
            }
            return false;
        }

        /// <summary>营地推进 <paramref name="deltaMs"/> 毫秒(营火的出现、火焰与光晕的循环、德利拉的待机都按这个时间播)。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs <= 0.0)
            {
                return;
            }
            AgeMs += deltaMs;
            foreach (AnimatedEffect e in _fire)
            {
                e.Advance(deltaMs);
            }
            foreach (AnimatedEffect e in _npcs)
            {
                e.Advance(deltaMs);
            }
        }

        /// <summary>
        /// 把此刻营地的精灵(NPC 先、营火各层后)加进 <paramref name="into"/>;没有图的层略过。NPC 在 normal 大类(与主角一起按世界 y 排),
        /// 营火各层在 <see cref="FireRank"/>(变暗层的大类,同大类按列表顺序画):调用方要先把变暗层加进列表、再调这个方法,营火才画在变暗层之上。
        /// </summary>
        public void AddSprites(List<SceneSprite> into, DnfAssets assets, TownProjection projection)
        {
            foreach (AnimatedEffect e in _npcs)
            {
                SceneSprite s = e.Sprite(assets, projection);
                if (s != null)
                {
                    into.Add(s);
                }
            }
            foreach (AnimatedEffect e in _fire)
            {
                SceneSprite s = e.Sprite(assets, projection);
                if (s != null)
                {
                    into.Add(s);
                }
            }
        }
    }
}
