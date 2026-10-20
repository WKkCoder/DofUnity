using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>怪物目录 monster/monster.lst:编号 → .mob 路径(证据 E-069)。</summary>
    public sealed class MonsterCatalog
    {
        public const string Path = "monster/monster.lst";
        public const string Root = "monster/";

        private readonly Dictionary<int, string> _paths;

        private MonsterCatalog(Dictionary<int, string> paths)
        {
            _paths = paths;
        }

        public int Count => _paths.Count;

        public static MonsterCatalog Read(DnfAssets assets)
        {
            ScriptLine[] doc = assets.ReadScript(Path);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有 " + Path);
            }
            var map = new Dictionary<int, string>();
            foreach (ScriptLine line in doc)
            {
                ScriptValue[] v = line.Values;
                for (int i = 0; i + 1 < v.Length; i++)
                {
                    if (v[i].IsNumber && v[i + 1].Kind == ScriptValueKind.String && v[i + 1].Text != null)
                    {
                        map[v[i].Int] = Root + v[i + 1].Text.Replace('\\', '/').ToLowerInvariant();
                        i++;
                    }
                }
            }
            return new MonsterCatalog(map);
        }

        /// <summary>编号对应的 .mob 的 PVF 路径(小写);没有则返回 null。</summary>
        public string PathOf(int code)
        {
            return _paths.TryGetValue(code, out string path) ? path : null;
        }
    }

    /// <summary>
    /// 一种怪物的外观:.mob 里的动作脚本(待机、行走、受击两种、倒地、翻倒、攻击)解析成动画文件。
    /// 路径相对 .mob 所在的目录(证据 E-069:monster/goblin/goblin.mob 的 "Animation_Goblin2/Stay.ani" → monster/goblin/animation_goblin2/stay.ani)。
    /// 缺少的动作为 null;攻击动作与攻击信息列表保持脚本里的顺序(攻击信息可以是空串)。
    /// </summary>
    public sealed class MonsterModel
    {
        public string MobPath { get; }
        public AniFile Waiting { get; }
        public AniFile Move { get; }
        public AniFile Damage1 { get; }
        public AniFile Damage2 { get; }
        public AniFile Down { get; }
        public AniFile Overturn { get; }
        public IReadOnlyList<AniFile> Attacks { get; }

        /// <summary>每个动画的 PVF 路径(用来在绘制列表里标记动画)。</summary>
        public IReadOnlyDictionary<AniFile, string> Paths { get; }

        /// <summary>[attack info] 列表(.atk 路径,绝对 PVF 路径;空串表示这一个攻击动作没有攻击信息)。</summary>
        public IReadOnlyList<string> AttackInfoPaths { get; }

        private MonsterModel(string mobPath, AniFile waiting, AniFile move, AniFile damage1, AniFile damage2, AniFile down, AniFile overturn,
            IReadOnlyList<AniFile> attacks, IReadOnlyList<string> attackInfos, Dictionary<AniFile, string> paths)
        {
            MobPath = mobPath;
            Waiting = waiting;
            Move = move;
            Damage1 = damage1;
            Damage2 = damage2;
            Down = down;
            Overturn = overturn;
            Attacks = attacks;
            AttackInfoPaths = attackInfos;
            Paths = paths;
        }

        public string PathOf(AniFile ani)
        {
            return ani != null && Paths.TryGetValue(ani, out string p) ? p : null;
        }

        /// <summary>读一个 .mob 的动作脚本;PVF 里没有时返回 null。</summary>
        public static MonsterModel Read(DnfAssets assets, string mobPath)
        {
            ScriptLine[] doc = assets.ReadScript(mobPath);
            if (doc == null)
            {
                return null;
            }
            var paths = new Dictionary<AniFile, string>();
            string waiting = null, move = null, damage1 = null, damage2 = null, down = null, overturn = null;
            var attackRel = new List<string>();
            var infoRel = new List<string>();
            foreach (ScriptLine line in doc)
            {
                if (waiting == null && line.TagIs("[waiting motion]"))
                {
                    waiting = line.FirstString();
                }
                else if (move == null && line.TagIs("[move motion]"))
                {
                    move = line.FirstString();
                }
                else if (damage1 == null && line.TagIs("[damage motion 1]"))
                {
                    damage1 = line.FirstString();
                }
                else if (damage2 == null && line.TagIs("[damage motion 2]"))
                {
                    damage2 = line.FirstString();
                }
                else if (down == null && line.TagIs("[down motion]"))
                {
                    down = line.FirstString();
                }
                else if (overturn == null && line.TagIs("[overturn motion]"))
                {
                    overturn = line.FirstString();
                }
                else if (attackRel.Count == 0 && line.TagIs("[attack motion]"))
                {
                    AddStrings(line, attackRel);
                }
                else if (infoRel.Count == 0 && line.TagIs("[attack info]"))
                {
                    AddStrings(line, infoRel);
                }
            }
            var attacks = new List<AniFile>();
            foreach (string rel in attackRel)
            {
                attacks.Add(Load(assets, mobPath, rel, paths));
            }
            var infos = new List<string>();
            foreach (string rel in infoRel)
            {
                infos.Add(string.IsNullOrEmpty(rel) ? string.Empty : PvfPath.ResolveRelativeTo(mobPath, rel));
            }
            return new MonsterModel(mobPath, Load(assets, mobPath, waiting, paths), Load(assets, mobPath, move, paths),
                Load(assets, mobPath, damage1, paths), Load(assets, mobPath, damage2, paths), Load(assets, mobPath, down, paths),
                Load(assets, mobPath, overturn, paths), attacks, infos, paths);
        }

        private static void AddStrings(ScriptLine line, List<string> into)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.String)
                {
                    into.Add(v.Text ?? string.Empty);
                }
            }
        }

        private static AniFile Load(DnfAssets assets, string mobPath, string rel, Dictionary<AniFile, string> paths)
        {
            if (string.IsNullOrEmpty(rel))
            {
                return null;
            }
            string path = PvfPath.ResolveRelativeTo(mobPath, rel);
            AniFile ani = assets.LoadAni(path);
            if (ani == null || ani.Frames.Length == 0)
            {
                return null;
            }
            paths[ani] = path;
            return ani;
        }
    }

    /// <summary>怪物精灵的摆放:与 NPC、主角用同一个投影模型(证据 E-024、E-067)。</summary>
    public static class MonsterSprites
    {
        public const int ObjectIdBase = 1000;

        /// <summary>
        /// 动画 <paramref name="ani"/> 第 <paramref name="aniFrame"/> 帧的精灵。地精的原图头朝右(证据 E-069:连拍里朝左的地精与翻转后的原图逐像素一致),
        /// 所以朝左时水平镜像;帧自己的 x 缩放为负时再翻一次(同 NPC,证据 E-029)。该帧没有图或图缺失时返回 null。
        /// </summary>
        /// <param name="silhouette">画成纯白剪影(怪物死亡的白闪,证据 E-084)。</param>
        /// <param name="tintAlpha">整张精灵的不透明度(0 到 255),死亡淡出用;普通混合下它就是整体的透明度(证据 E-071)。</param>
        public static SceneSprite SpriteAt(DnfAssets assets, AniFile ani, string animationPath, int aniFrame, TownProjection projection,
            int worldX, int worldY, int worldZ, bool facingRight, int objectId, bool silhouette = false, int tintAlpha = 255)
        {
            if (ani == null || (uint)aniFrame >= (uint)ani.Frames.Length)
            {
                return null;
            }
            AniFrame f = ani.Frames[aniFrame];
            if (f.ImageIndex < 0 || f.ImageIndex >= ani.ImagePaths.Length)
            {
                return null;
            }
            string img = "sprite/" + ani.ImagePaths[f.ImageIndex].Replace('\\', '/');
            if (!assets.TryGetFrame(img, f.FrameIndex, out SpriteFrame sprite))
            {
                return null;
            }
            bool mirrored = !facingRight ^ (f.TryGetScale(out float scaleX, out _) && scaleX < 0f);
            projection.TopLeft(worldX, worldY, worldZ, f.X, f.Y, sprite.OffsetX, sprite.OffsetY, sprite.Width, mirrored, out int sx, out int sy);
            return new SceneSprite(silhouette ? sprite.Silhouette : sprite, sx, sy, worldY, objectId, animationPath, aniFrame, mirrored,
                SpriteBlend.Normal, tintAlpha);
        }
    }
}
