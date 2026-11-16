using Dnf.Data;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// login-shot:联网登录界面(<see cref="LoginScreen"/>,<c>-dnfonline</c> 的第一屏,r18-onlineshell)在界面打开 --time 毫秒时的 800×600 画面,写成 PNG。
/// 没有汉字字体,标签是点阵字库的英文大写占位(Unity 里有字体时是中文)。账号、密码与提示全是合成的:
/// --user 预填账号(默认 player_01;不会打印),--typed N 让密码框里有 N 个字符(只画 N 个圆点,密码本身从不画),
/// --notice 显示某一类提示(<see cref="OnlineNoticeKind"/> 的名字,不分大小写,连字符可写可不写;--countdown 秒 是"限流"提示的倒计时),
/// --busy 画请求在途的样子,--change-password 画修改密码的形态。
/// 背景默认是选角界面的底图(用户自备数据,这张图只在本机看,不入库);--no-art 不读数据目录、背景是纯色,整个画面都是合成的。
/// Unity 一侧没有对应的离屏渲染(外壳要和服务端通信,不走 -dnfshot),这条命令是看版面的 CPU 参照。
/// </summary>
internal static class LoginShot
{
    public static int Run(Options opts)
    {
        if (opts.PngFile == null)
        {
            throw new UserError("login-shot 需要 --png <输出.png>");
        }
        if (opts.LoginTyped > LoginScreen.MaxPasswordLength)
        {
            throw new UserError("--typed 超出了密码的字数上限 " + LoginScreen.MaxPasswordLength);
        }
        OnlineNotice? notice = ParseNotice(opts);
        DnfAssets? assets = opts.LoginNoArt ? null : DnfAssets.Open(Locator.Resolve(opts.DnfDir, opts.OutDir).DnfDir);
        try
        {
            var screen = new LoginScreen(assets);
            screen.SetUsername(opts.LoginUser);                  // 焦点落到密码框
            if (opts.LoginChangePassword)
            {
                screen.SetMode(LoginMode.ChangePassword);        // 焦点在"旧密码"
            }
            screen.Type(new string('x', opts.LoginTyped));       // 只为数个数:密码内容不会被画出来
            if (opts.LoginChangePassword)
            {
                screen.Tab();                                    // 新密码框里也放同样多的字符
                screen.Type(new string('x', opts.LoginTyped));
            }
            screen.SetBusy(opts.LoginBusy);
            screen.SetNotice(notice);
            List<SceneSprite> sprites = screen.Build(opts.TimeMs);
            var canvas = new RgbaCanvas(800, 600);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            PngWriter.Write(opts.PngFile, 800, 600, canvas.Pixels);
            Console.WriteLine("登录界面:时间 " + opts.TimeMs + " ms,"
                + (opts.LoginChangePassword ? "修改密码形态" : "登录形态")
                + ",账号 " + opts.LoginUser.Length + " 个字符,密码框 " + opts.LoginTyped + " 个字符"
                + (opts.LoginBusy ? ",请求在途" : string.Empty)
                + (notice != null ? ",提示 " + notice.Kind : string.Empty)
                + (assets == null ? ",纯色背景" : ",选角底图背景")
                + ":绘制列表 " + sprites.Count + " 个精灵");
            Console.WriteLine("已写 " + opts.PngFile);
            return 0;
        }
        finally
        {
            assets?.Dispose();
        }
    }

    /// <summary>--notice 的名字(忽略大小写、连字符与下划线)→ 提示;没带返回 null,认不得就报错并列出可用的名字。</summary>
    private static OnlineNotice? ParseNotice(Options opts)
    {
        if (opts.LoginNotice == null)
        {
            return null;
        }
        string wanted = opts.LoginNotice.Replace("-", string.Empty).Replace("_", string.Empty);
        foreach (OnlineNoticeKind kind in Enum.GetValues<OnlineNoticeKind>())
        {
            if (kind != OnlineNoticeKind.None && string.Equals(kind.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return new OnlineNotice(kind, seconds: opts.LoginCountdown);
            }
        }
        string known = string.Join("、", Enum.GetValues<OnlineNoticeKind>().Where(k => k != OnlineNoticeKind.None).Select(k => k.ToString()));
        throw new UserError("--notice 不认识 " + opts.LoginNotice + ",可用:" + known);
    }
}
