using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 加载画面的素材(证据 E-095):一张整屏过场图、上下边条,城镇的还有居中的标题。
    /// 城镇加载画面在原版第一张截图里与"过场图 + 边条 + 标题"的合成逐像素一致(没有半透明、没有别的元素);
    /// 副本加载画面在这三样之外还有队伍加载条、提示文字与聊天框,那些要文字系统,这里没有画。
    /// </summary>
    public sealed class SplashScene
    {
        /// <summary>整屏过场图(800×600,左上角对齐屏幕);读不到时为 null,画黑底。</summary>
        public SpriteFrame Cutscene { get; }

        /// <summary>上下边条(整屏大小、中间透明);为 null 则不画。</summary>
        public SpriteFrame Strips { get; }

        /// <summary>标题(城镇名,图里连中英文名与分隔线);副本没有。</summary>
        public SpriteFrame Title { get; }

        /// <summary>标题图左上角在屏幕上的位置。</summary>
        public int TitleX { get; }

        public int TitleY { get; }

        /// <summary>画面停留多久(毫秒)。城镇与副本的时长都没有从原版里测准,是占位。</summary>
        public double DurationMs { get; }

        public string Name { get; }

        public SplashScene(string name, SpriteFrame cutscene, SpriteFrame strips, SpriteFrame title, int titleX, int titleY, double durationMs)
        {
            Name = name ?? string.Empty;
            Cutscene = cutscene;
            Strips = strips;
            Title = title;
            TitleX = titleX;
            TitleY = titleY;
            DurationMs = durationMs;
        }
    }

    /// <summary>
    /// 加载画面(<see cref="GameStage.Loading"/> 阶段的内容):按 <see cref="Advance"/> 累加的时间走,够长了就 <see cref="Finished"/>,
    /// 流程随后切到目标阶段。画法:过场图盖满屏幕,边条与标题叠在上面(<see cref="OverlayArt"/> 的叠加层,依次编号 0、1、2)。
    /// 原版有没有淡入淡出没有逐帧样本(进城只有一张截图),这里没有做。
    /// </summary>
    public sealed class SplashScreen : ISceneSource
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        private readonly OverlayArt _art = new OverlayArt();

        public SplashScene Scene { get; }

        /// <summary>画面已经停了多久(毫秒)。</summary>
        public double AgeMs { get; private set; }

        public SplashScreen(SplashScene scene)
        {
            Scene = scene ?? throw new ArgumentNullException(nameof(scene));
        }

        public bool Finished => AgeMs >= Scene.DurationMs;

        public void Advance(double deltaMs)
        {
            if (deltaMs > 0.0)
            {
                AgeMs += deltaMs;
            }
        }

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>(3);
            sprites.Add(OverlayArt.Place(Scene.Cutscene ?? _art.Solid(ScreenWidth, ScreenHeight, 0x000000u), 0, 0, 0, "splash-cutscene"));
            if (Scene.Strips != null)
            {
                sprites.Add(OverlayArt.Place(Scene.Strips, 0, 0, 1, "splash-strips"));
            }
            if (Scene.Title != null)
            {
                sprites.Add(OverlayArt.Place(Scene.Title, Scene.TitleX, Scene.TitleY, 2, "splash-title"));
            }
            return sprites;
        }
    }

    /// <summary>从游戏数据造加载画面素材。</summary>
    public static class SplashCatalog
    {
        /// <summary>城镇加载画面停多久(占位:原版只有一张截图,没有时长样本)。</summary>
        public const double TownDurationMs = 1500.0;

        /// <summary>副本加载画面停多久(占位:原版进图过渡的连拍里加载画面约 2.7 秒,但那一段里夹着真实的加载耗时)。</summary>
        public const double DungeonDurationMs = 2700.0;

        /// <summary>标题动画放在屏幕中央:原版第一张截图上标题图的左上角 = 屏幕中心 + 动画帧偏移 + 图自带偏移(逐像素吻合)。</summary>
        public const int TitleAnchorX = 400;

        public const int TitleAnchorY = 300;

        /// <summary>城镇的加载画面:过场图、边条与标题动画(第一帧)。<paramref name="town"/> 没写过场图时返回 null。</summary>
        public static SplashScene ForTown(ISpriteSource sprites, Func<string, AniFile> loadAni, TownDef town)
        {
            if (sprites == null)
            {
                throw new ArgumentNullException(nameof(sprites));
            }
            if (town == null || town.CutsceneImage == null)
            {
                return null;
            }
            SpriteFrame title = null;
            int titleX = 0, titleY = 0;
            if (town.EnteringTitle != null && loadAni != null)
            {
                AniFile ani = loadAni(town.EnteringTitle);
                if (ani != null && ani.Frames.Length > 0)
                {
                    AniFrame f = ani.Frames[0];
                    if (f.ImageIndex >= 0 && f.ImageIndex < ani.ImagePaths.Length
                        && sprites.TryGetFrame("sprite/" + ani.ImagePaths[f.ImageIndex].Replace('\\', '/').ToLowerInvariant(), f.FrameIndex, out title))
                    {
                        titleX = TitleAnchorX + f.X + title.OffsetX;
                        titleY = TitleAnchorY + f.Y + title.OffsetY;
                    }
                }
            }
            return Build(town.Path, sprites, town.CutsceneImage, town.CutsceneFrame, title, titleX, titleY, TownDurationMs);
        }

        public static SplashScene ForTown(DnfAssets assets, TownDef town)
        {
            return ForTown(assets, assets.LoadAni, town);
        }

        /// <summary>副本的加载画面:副本定义里 [cutscene image] 的过场图加边条。读不到副本或没写过场图时返回 null。</summary>
        public static SplashScene ForDungeon(ISpriteSource sprites, string dungeonPath, ScriptLine[] doc)
        {
            if (sprites == null)
            {
                throw new ArgumentNullException(nameof(sprites));
            }
            if (doc == null)
            {
                return null;
            }
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[cutscene image]"))
                {
                    List<int> n = line.Ints();
                    return Build(dungeonPath, sprites, WorldMapDef.ToImageName(line.FirstString()), n.Count > 0 ? n[0] : 0, null, 0, 0, DungeonDurationMs);
                }
            }
            return null;
        }

        public static SplashScene ForDungeon(DnfAssets assets, string dungeonPath)
        {
            return ForDungeon(assets, dungeonPath, assets.ReadScript(dungeonPath));
        }

        private static SplashScene Build(string name, ISpriteSource sprites, string cutsceneImage, int cutsceneFrame, SpriteFrame title, int titleX, int titleY,
            double durationMs)
        {
            sprites.TryGetFrame(cutsceneImage, cutsceneFrame, out SpriteFrame cutscene);
            sprites.TryGetFrame(WorldMapArt.UiImage, WorldMapArt.TopBottomBannerFrame, out SpriteFrame strips);
            return new SplashScene(name, cutscene, strips, title, titleX, titleY, durationMs);
        }
    }
}
