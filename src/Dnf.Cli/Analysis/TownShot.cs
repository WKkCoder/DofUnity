using System.Globalization;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// town-shot:城镇图册里的一块区域(<see cref="TownWorld"/> 造的会话,证据 E-095)在某一时刻的 800×600 画面,写成 PNG;
/// 主角在登录落脚点(<see cref="TownAtlas.Arrive"/>),镜头从最小值起步。Unity 播放器的 -dnftownarea 离屏渲染以它为 CPU 参照(见 tools/unity-build-town.sh)。
/// --hero 可以改主角与镜头的位置,写法同 Unity 的 -dnfhero(x,y,left|right,rest|move,动作毫秒[,镜头x];不带镜头 x 时主角居中再夹住)。
/// </summary>
internal static class TownShot
{
    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.TownArea == null)
        {
            throw new UserError("town-shot 需要 --area <城镇>/<区域>(例如 1/0)");
        }
        if (opts.PngFile == null)
        {
            throw new UserError("town-shot 需要 --png <输出.png>");
        }
        TownPlace place = ParsePlace(opts.TownArea);
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        TownAtlas atlas = TownAtlas.Open(assets);
        TownSession session = TownWorld.CreateSession(assets, atlas, place, DemoHero.CreateOutfit(assets));
        if (opts.Hero != null)
        {
            Apply(session, opts.Hero);
        }
        List<SceneSprite> sprites = session.Build(opts.TimeMs);
        if (opts.NpcRects)
        {
            foreach (SceneSprite s in sprites)
            {
                if (s.NpcId > 0)
                {
                    string anchor = "";
                    foreach (TownNpc n in session.Npcs)
                    {
                        if (n.Id == s.NpcId)
                        {
                            anchor = " 锚点屏幕=(" + (n.X - session.Camera.IntX) + "," + (n.Y - n.Z + session.GroundY) + ")";
                        }
                    }
                    Console.WriteLine("NPC " + s.NpcId + " 精灵矩形 x=" + s.ScreenX + " y=" + s.ScreenY + " 宽=" + s.Frame.Width + " 高=" + s.Frame.Height + anchor);
                }
            }
        }
        string hudNote = "";
        if (opts.TownHudOn)
        {
            // 城镇底栏(M30):和 Unity 流程里同一条,等级取 --level
            TownHud hud = TownHud.TryCreate(assets);
            HeroProgress progress = HeroProgress.CreateForLocalHero(assets, opts.HeroLevel);
            int order = 0;
            int before = sprites.Count;
            bool drawn = hud != null && hud.Build(sprites, ref order, progress);
            hudNote = ",底栏 " + (drawn ? (sprites.Count - before) + " 个精灵(等级 " + progress.Level + ")" : "没画");
        }
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, sprites);
        PngWriter.Write(opts.PngFile, 800, 600, canvas.Pixels);
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine("区域 " + place.Town + "/" + place.Area + " 地图 " + session.MapPath + ",时间 " + opts.TimeMs + " ms:绘制列表 " + sprites.Count + " 个精灵,NPC "
            + session.NpcCount + " 个,图层 " + session.LayerCount + " 个;主角 (" + session.Hero.X.ToString("F1", inv) + "," + session.Hero.Y.ToString("F1", inv) + ") 朝"
            + (session.Hero.FacingLeft ? "左" : "右") + ",镜头 x " + session.Camera.IntX + hudNote);
        Console.WriteLine("已写 " + opts.PngFile);
        return 0;
    }

    private static TownPlace ParsePlace(string text)
    {
        string[] p = text.Split('/');
        if (p.Length != 2 || !int.TryParse(p[0], out int town) || !int.TryParse(p[1], out int area) || town < 0 || area < 0)
        {
            throw new UserError("--area 需要 城镇/区域(例如 1/0): " + text);
        }
        return new TownPlace(town, area);
    }

    private static void Apply(IHeroSession session, string spec)
    {
        string[] p = spec.Split(',');
        var inv = CultureInfo.InvariantCulture;
        if ((p.Length != 5 && p.Length != 6)
            || !double.TryParse(p[0], NumberStyles.Float, inv, out double x)
            || !double.TryParse(p[1], NumberStyles.Float, inv, out double y)
            || (p[2] != "left" && p[2] != "right") || (p[3] != "rest" && p[3] != "move")
            || !long.TryParse(p[4], out long ms) || ms < 0)
        {
            throw new UserError("--hero 需要 x,y,left|right,rest|move,毫秒[,镜头x]");
        }
        session.Hero.Place(x, y, p[2] == "left", p[3] == "move" ? HeroMotion.Move : HeroMotion.Rest, ms);
        if (p.Length == 6)
        {
            if (!double.TryParse(p[5], NumberStyles.Float, inv, out double cam))
            {
                throw new UserError("--hero 的镜头 x 不是数字");
            }
            session.Camera.Place(cam);
        }
        else
        {
            session.Camera.Place(x - TownCamera.DefaultCenterX);
        }
    }
}
