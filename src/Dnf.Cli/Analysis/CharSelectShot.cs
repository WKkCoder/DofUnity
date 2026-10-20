using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// charselect-shot:选角界面(<see cref="CharacterSelectScreen"/>,证据 E-096)在界面打开 --time 毫秒时的 800×600 画面,写成 PNG。
/// 名册是 --chars 个默认角色(默认 1 个,与流程里的本地存档占位一样),--select 选中第几个。
/// Unity 播放器带 -dnfflow -dnfflowfrom char -dnfshot -dnftime 的离屏渲染以它为 CPU 参照(见 tools/unity-build-town.sh):
/// 两边都是"界面时间一次走到这一刻再画",所以逐字节一致。
/// </summary>
internal static class CharSelectShot
{
    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.PngFile == null)
        {
            throw new UserError("charselect-shot 需要 --png <输出.png>");
        }
        if (opts.SelectIndex > opts.CharCount)
        {
            throw new UserError("--select 超出了 --chars 个角色");
        }
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        var slots = new List<CharacterSlot>();
        for (int i = 0; i < opts.CharCount; i++)
        {
            slots.Add(new CharacterSlot(CharacterRoster.LocalName, CharacterRoster.LocalJob, 1));
        }
        CharacterSelectScene scene = opts.NoAvatars
            ? new CharacterSelectScene(assets, null, assets.LoadAni)
            : CharacterSelectScene.From(assets);
        var screen = new CharacterSelectScreen(new CharacterRoster(slots), scene, opts.SelectIndex - 1);
        screen.Advance(opts.TimeMs);
        List<SceneSprite> sprites = screen.Build(opts.TimeMs);
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, sprites);
        PngWriter.Write(opts.PngFile, 800, 600, canvas.Pixels);
        string? failure = screen.Scene?.Avatars is DnfCharacterAvatars avatars ? avatars.Failure : null;
        Console.WriteLine("选角界面:时间 " + opts.TimeMs + " ms,角色 " + opts.CharCount + " 个,选中第 " + opts.SelectIndex + " 个:绘制列表 " + sprites.Count + " 个精灵"
            + (failure != null ? ",头像没画出来:" + failure : string.Empty));
        Console.WriteLine("已写 " + opts.PngFile);
        return 0;
    }
}
