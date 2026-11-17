using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dnf.Client;
using Dnf.Data;
using Dnf.Play;
using Dnf.Render;
using NUnit.Framework;
using UnityEngine;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 联网外壳的登录页与状态画面(r18-onlineshell):用系统汉字字体把登录页的几种样子(空白、输入中、请求在途、各类提示、修改密码)、
    /// 连接频道的状态画面与选角界面上的提示叠层画成 800×600 的 PNG 放在 out/onlineshell/(文件名以 zh- 开头)供本机查看版式与中文。
    /// 账号、密码、提示与角色全是合成的;有 DNF_DIR 时背景是自备数据里选角界面的底图(只在本机看,不入库),没有就是纯色背景。
    /// 断言只看结构(中文是不是真的走了字体、各个精灵在不在、文字有没有画到屏幕外),不对图下结论;没有带汉字的系统字体时相应的测试跳过。
    /// </summary>
    [Category("Text")]
    public class OnlineShellShotTests
    {
        private const int Width = 800;
        private const int Height = 600;

        private static UnityGlyphFonts _fonts;

        [OneTimeSetUp]
        public void OpenFonts()
        {
            _fonts = UnityGlyphFonts.TryCreate(null, out _);
        }

        [OneTimeTearDown]
        public void CloseFonts()
        {
            _fonts?.Dispose();
            _fonts = null;
        }

        private static void NeedFonts()
        {
            if (_fonts == null)
            {
                Assert.Ignore("这台机器上没有带汉字的系统字体");
            }
        }

        /// <summary>数据目录(有就打开,没有返回 null:背景退回纯色);调用方负责 Dispose。</summary>
        private static DnfAssets OpenArt()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                return null;
            }
            return DnfAssets.Open(dir);
        }

        private static void Save(List<SceneSprite> sprites, string name)
        {
            var canvas = new RgbaCanvas(Width, Height);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "out", "onlineshell"));
            Directory.CreateDirectory(dir);
            var flipped = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
            {
                Buffer.BlockCopy(canvas.Pixels, y * Width * 4, flipped, (Height - 1 - y) * Width * 4, Width * 4);   // Unity 纹理的第 0 行在下面
            }
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(flipped);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static SceneSprite One(List<SceneSprite> sprites, string tag)
        {
            return sprites.Single(s => s.AnimationPath == tag);
        }

        /// <summary>登录页上的文字类精灵(不含光晕、阴影、暗角这类衬底)都完整落在屏幕里。</summary>
        private static void AssertTextInsideTheScreen(List<SceneSprite> sprites)
        {
            string[] backing = { "shadow", "focus", "glow", "vignette", "backdrop", "background", "corner", "bar", "accent", "box", "cell", "button-login", "button-register", "button-change", "button-confirm", "button-back" };
            foreach (SceneSprite s in sprites)
            {
                string tag = s.AnimationPath;
                if (tag.StartsWith("login-") && !backing.Any(b => tag.Contains(b) && !tag.Contains("text")))
                {
                    Assert.GreaterOrEqual(s.ScreenX, 0, tag);
                    Assert.GreaterOrEqual(s.ScreenY, 0, tag);
                    Assert.LessOrEqual(s.ScreenX + s.Frame.Width, Width, tag);
                    Assert.LessOrEqual(s.ScreenY + s.Frame.Height, Height, tag);
                }
            }
        }

        private static OnlineCharacterList Roster(int count)
        {
            var entries = new List<OnlineCharacterEntry>();
            for (int i = 0; i < count; i++)
            {
                var equips = new[] { new OnlineCharacterEquip((byte)i, (uint)(1000 + i), (uint)(2000 + i), (byte)(3 + i), (uint)(4000 + i), (uint)(5000 + i)) };
                entries.Add(new OnlineCharacterEntry(
                    (ushort)i, new byte[] { (byte)(0x41 + i), 0x42, 0x43 }, (byte)(i % 2), (byte)i, (byte)(0x10 + i), (byte)(10 + i), 0, (byte)(i % 3),
                    equips, (byte)(7 + i), 0, (uint)(100 + i), 1, (byte)(i % 2), (uint)(200 + i), 1, 0, 2, (uint)(300 + i)));
            }
            return new OnlineCharacterList(6, 1, 2, 77u, entries, 4);
        }

        [Test]
        public void TheLoginPageIsDesignedAndSpeaksChinese()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                var screen = new LoginScreen(assets, _fonts);

                List<SceneSprite> sprites = screen.Build(0);

                // 标题字、各个标签都是字体画的中文:宽度远超点阵占位英文
                Assert.GreaterOrEqual(One(sprites, "login-banner").Frame.Width, 200, "游戏名标题字");
                Assert.GreaterOrEqual(One(sprites, "login-title").Frame.Width, 80, "账号登录");
                Assert.GreaterOrEqual(One(sprites, "login-subtitle").Frame.Width, 120, "登录后选择服务器与角色");
                Assert.GreaterOrEqual(One(sprites, "login-label-username").Frame.Width, 28, "账号");
                Assert.GreaterOrEqual(One(sprites, "login-placeholder-username").Frame.Width, 70, "请输入账号");
                Assert.GreaterOrEqual(One(sprites, "login-rule").Frame.Width, 200, "账号规则");
                Assert.GreaterOrEqual(One(sprites, "login-button-text-login").Frame.Width, 60, "进入游戏");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "login-hint-text-0"));
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "login-caret"), "账号框有焦点,光标在闪");
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-login.png");
            }
        }

        [Test]
        public void EveryStateOfTheLoginPageIsDrawnInChinese()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                // 输入中:账号有字,密码框里有十个圆点(密码本身从不画)
                var typed = new LoginScreen(assets, _fonts);
                typed.SetUsername("player_01");
                typed.Type("0123456789");
                List<SceneSprite> typedSprites = typed.Build(0);
                Assert.AreEqual(10, typedSprites.Count(s => s.AnimationPath == "login-password-mask"));
                Assert.IsTrue(typedSprites.Any(s => s.AnimationPath == "login-username-text"));
                AssertTextInsideTheScreen(typedSprites);
                Save(typedSprites, "zh-login-typed.png");

                // 请求在途:转圈、请稍候、按钮变灰
                var busy = new LoginScreen(assets, _fonts);
                busy.SetUsername("player_01");
                busy.Type("0123456789");
                busy.SetBusy(true);
                List<SceneSprite> busySprites = busy.Build(240);
                Assert.IsTrue(busySprites.Any(s => s.AnimationPath == "login-busy-spinner"));
                Assert.IsTrue(busySprites.Any(s => s.AnimationPath == "login-button-login-disabled"));
                AssertTextInsideTheScreen(busySprites);
                Save(busySprites, "zh-login-busy.png");

                // 各类提示:出错、限流倒计时、成功
                foreach (var item in new[]
                {
                    ("zh-login-error.png", new OnlineNotice(OnlineNoticeKind.WrongCredentials)),
                    ("zh-login-ratelimit.png", new OnlineNotice(OnlineNoticeKind.RateLimited, seconds: 12)),
                    ("zh-login-success.png", new OnlineNotice(OnlineNoticeKind.RegisterOk)),
                    ("zh-login-notconfigured.png", new OnlineNotice(OnlineNoticeKind.NotConfigured, detail: "缺少连接地址")),
                })
                {
                    var screen = new LoginScreen(assets, _fonts);
                    screen.SetUsername("player_01");
                    screen.Type("0123456789");
                    screen.SetNotice(item.Item2);
                    List<SceneSprite> sprites = screen.Build(0);
                    Assert.IsTrue(sprites.Any(s => s.AnimationPath == "login-notice"), item.Item1);
                    Assert.IsFalse(sprites.Any(s => s.AnimationPath == "login-rule"), "提示出现时账号规则让位");
                    AssertTextInsideTheScreen(sprites);
                    Save(sprites, item.Item1);
                }

                // 修改密码
                var change = new LoginScreen(assets, _fonts);
                change.SetUsername("player_01");
                change.SetMode(LoginMode.ChangePassword);
                change.Type("01234567");
                change.Tab();
                change.Type("89abcdef");
                List<SceneSprite> changeSprites = change.Build(0);
                Assert.IsTrue(changeSprites.Any(s => s.AnimationPath == "login-box-new-password"));
                AssertTextInsideTheScreen(changeSprites);
                Save(changeSprites, "zh-login-change-password.png");
            }
        }

        [Test]
        public void TheStatusScreensAndTheSelectNoticeAreDrawnInChinese()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                var connecting = new OnlineShellStatusScreen(assets, _fonts)
                {
                    Title = OnlineShellLabel.ChannelConnecting,
                    Hint = OnlineShellLabel.ChannelHintCancel,
                };
                List<SceneSprite> connectingSprites = connecting.Build(240);
                Assert.IsTrue(connectingSprites.Any(s => s.AnimationPath == "online-status-spinner"));
                Assert.GreaterOrEqual(One(connectingSprites, "online-status-title").Frame.Width, 100, "正在连接频道…");
                Save(connectingSprites, "zh-status-connecting.png");

                var failed = new OnlineShellStatusScreen(assets, _fonts)
                {
                    Title = OnlineShellLabel.ChannelConnecting,
                    Hint = OnlineShellLabel.ChannelHint,
                    Notice = new OnlineNotice(OnlineNoticeKind.ConnectFailed),
                };
                List<SceneSprite> failedSprites = failed.Build(0);
                Assert.IsTrue(failedSprites.Any(s => s.AnimationPath == "online-status-icon"));
                Assert.IsTrue(failedSprites.Any(s => s.AnimationPath == "online-status-notice"));
                Save(failedSprites, "zh-status-error.png");

                if (assets == null)
                {
                    Assert.Ignore("没有 DNF_DIR:选角界面的提示叠层需要选角底图,跳过");
                }
                IGlyphFonts saved = OverlayArt.SharedFonts;
                try
                {
                    OverlayArt.SharedFonts = _fonts;
                    var select = new CharacterSelectScreen(OnlineShellRoster.Placeholder(Roster(3)), new CharacterSelectScene(assets));
                    var overlay = new OnlineShellNoticeOverlay(select, _fonts) { Notice = new OnlineNotice(OnlineNoticeKind.SelectFailed) };
                    List<SceneSprite> sprites = overlay.Build(0);
                    SceneSprite bar = One(sprites, "online-select-notice-bar");
                    Assert.LessOrEqual(bar.ScreenY + bar.Frame.Height, CharacterSelectArt.StartY, "提示条不压\"开始\"按钮");
                    Assert.GreaterOrEqual(One(sprites, "online-select-notice").Frame.Width, 150, "这个角色暂时进不去,请换一个");
                    Save(sprites, "zh-select-notice.png");
                }
                finally
                {
                    OverlayArt.SharedFonts = saved;
                }
            }
        }
    }
}
