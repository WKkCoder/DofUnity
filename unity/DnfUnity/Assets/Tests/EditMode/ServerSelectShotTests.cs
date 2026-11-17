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
    /// 选服务器、选频道两张界面(r18-serverui):用系统汉字字体把两张界面的几种样子(基线内容、多个服务器、多页频道、读目录中/空/失败、连接中、刷新中、提示)
    /// 画成 800×600 的 PNG 放在 out/serverui/(文件名以 zh- 开头)供本机查看版式与中文。
    /// 目录、提示全是合成的;有 DNF_DIR 时背景与各件素材是自备数据(只在本机看,不入库),没有就是程序画的底板。
    /// 断言只看结构(中文是不是真的走了字体、各个文字精灵在不在、有没有画到屏幕外),不对图下结论;没有带汉字的系统字体时相应的测试跳过。
    /// </summary>
    [Category("Text")]
    public class ServerSelectShotTests
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

        /// <summary>数据目录(有就打开,没有返回 null:界面退回程序画的底板);调用方负责 Dispose。</summary>
        private static DnfAssets OpenArt()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                return null;
            }
            return DnfAssets.Open(dir);
        }

        private static ServerPickerScene SceneOf(DnfAssets assets)
        {
            return assets == null ? null : ServerPickerScene.From(assets);
        }

        private static void Save(List<SceneSprite> sprites, string name)
        {
            var canvas = new RgbaCanvas(Width, Height);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "out", "serverui"));
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

        private static List<SceneSprite> All(List<SceneSprite> sprites, string tag)
        {
            return sprites.Where(s => s.AnimationPath == tag).ToList();
        }

        /// <summary>文字类精灵(名字、提示、按钮字、页码这些)都完整落在屏幕里。</summary>
        private static void AssertTextInsideTheScreen(List<SceneSprite> sprites)
        {
            string[] textTags =
            {
                "server-subtitle", "server-entry-name", "server-entry-note", "server-message", "server-notice",
                "picker-banner-text", "channel-item-title", "channel-item-level", "channel-item-congestion", "channel-page-label",
                "channel-refresh-text", "channel-message", "channel-notice",
            };
            foreach (SceneSprite s in sprites)
            {
                if (textTags.Contains(s.AnimationPath))
                {
                    Assert.GreaterOrEqual(s.ScreenX, 0, s.AnimationPath);
                    Assert.GreaterOrEqual(s.ScreenY, 0, s.AnimationPath);
                    Assert.LessOrEqual(s.ScreenX + s.Frame.Width, Width, s.AnimationPath);
                    Assert.LessOrEqual(s.ScreenY + s.Frame.Height, Height, s.AnimationPath);
                }
            }
        }

        private static DirectoryChannel Channel(int number, string title, ChannelCongestion congestion, int min = 0, int max = 0, bool enterable = true)
        {
            return new DirectoryChannel(number, title, congestion, enterable ? new ChannelTarget(31000 + number) : null, min, max);
        }

        private static ServerDirectory Servers()
        {
            return new ServerDirectory(new[]
            {
                new DirectoryServer("希洛克", new[] { Channel(1, "格兰之森", ChannelCongestion.Smooth, 41, 55) }, false),
                new DirectoryServer("卡恩", new[] { Channel(2, "天空之城", ChannelCongestion.Good) }, false),
                new DirectoryServer("狄瑞吉", null, false),                 // 没有频道:灰牌
                new DirectoryServer("某个新服务器", new[] { Channel(3, "测试", ChannelCongestion.Normal) }, false),   // 素材里没有的名字
            });
        }

        private static DirectoryServer ManyChannels(int count)
        {
            var channels = new List<DirectoryChannel>();
            ChannelCongestion[] cycle = { ChannelCongestion.Smooth, ChannelCongestion.Good, ChannelCongestion.Normal, ChannelCongestion.Crowded, ChannelCongestion.Full };
            string[] names = { "格兰之森", "天空之城", "暗黑城", "雷鸣废墟", "龙人之塔", "冰霜幽谷", "深渊" };
            for (int i = 0; i < count; i++)
            {
                channels.Add(Channel(i + 1, names[i % names.Length], cycle[i % cycle.Length], 10 + i, 20 + i, cycle[i % cycle.Length] != ChannelCongestion.Full));
            }
            return new DirectoryServer("希洛克", channels, false);
        }

        // ------------------------------------------------------------------ 选服务器

        [Test]
        public void TheServerPageSpeaksChinese()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                var screen = new ServerSelectScreen(SceneOf(assets), _fonts);
                screen.SetDirectory(Servers());

                List<SceneSprite> sprites = screen.Build(2250);

                // 副标题是字体画的整行中文(点阵占位英文只有 "SELECT A SERVER." 这么长,中文那行更宽)
                Assert.GreaterOrEqual(One(sprites, "server-subtitle").Frame.Width, 100, "请选择进行游戏的伺服器。");
                // 素材里没有的服务器名只能由字体画在程序板上;有图包时认得的名字烤在图里(灰牌的"无频道"小字同理),没有图包时全部由字体画
                List<SceneSprite> names = All(sprites, "server-entry-name");
                Assert.GreaterOrEqual(names.Count, assets == null ? 4 : 1, "服务器名");
                Assert.IsTrue(names.Any(n => n.Frame.Width >= 60), "某个新服务器 这样的中文名走了字体");
                if (assets == null)
                {
                    Assert.IsTrue(sprites.Any(s => s.AnimationPath == "server-entry-note"), "灰牌上有\"无频道\"小字");
                }
                Assert.IsTrue(sprites.Any(s => s.AnimationPath.StartsWith("server-start")), "游戏开始按钮在");
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-server.png");
            }
        }

        [Test]
        public void EveryStateOfTheServerPageIsDrawnInChinese()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                ServerPickerScene scene = SceneOf(assets);

                var loading = new ServerSelectScreen(scene, _fonts);
                loading.SetLoading();
                List<SceneSprite> sprites = loading.Build(1500);
                Assert.GreaterOrEqual(One(sprites, "server-message").Frame.Width, 100, "正在获取服务器列表…");
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-server-loading.png");

                var empty = new ServerSelectScreen(scene, _fonts);
                empty.SetDirectory(ServerDirectory.Empty);
                sprites = empty.Build(1500);
                Assert.GreaterOrEqual(One(sprites, "server-message").Frame.Width, 60, "没有可用的服务器");
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-server-empty.png");

                var failed = new ServerSelectScreen(scene, _fonts);
                failed.SetFailed();
                failed.SetNotice(new OnlineNotice(OnlineNoticeKind.NetworkError));
                sprites = failed.Build(1500);
                Assert.GreaterOrEqual(One(sprites, "server-message").Frame.Width, 60, "无法获取服务器列表");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath.StartsWith("server-retry")), "重试");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath.StartsWith("server-direct")), "默认频道");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "server-notice"), "失败原因的提示条");
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-server-failed.png");
            }
        }

        [Test]
        public void ManyServersScrollAndKeepTheirTextInside()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                var list = new List<DirectoryServer>();
                for (int i = 0; i < 14; i++)
                {
                    list.Add(new DirectoryServer("服务器" + (i + 1), new[] { Channel(i + 1, "测试", ChannelCongestion.Smooth) }, false));
                }
                var screen = new ServerSelectScreen(SceneOf(assets), _fonts);
                screen.SetDirectory(new ServerDirectory(list));
                screen.Move(0, 1);
                screen.Move(0, 1);

                List<SceneSprite> sprites = screen.Build(1500);

                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "server-scroll-down" || s.AnimationPath == "server-scroll-down-dim"));
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-server-many.png");
            }
        }

        // ------------------------------------------------------------------ 选频道

        [Test]
        public void TheChannelPageSpeaksChinese()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                var screen = new ChannelSelectScreen(SceneOf(assets), _fonts);
                screen.SetServer(new DirectoryServer("希洛克", new[]
                {
                    Channel(11, "格兰之森", ChannelCongestion.Smooth, 41, 55),
                    Channel(12, "天空之城", ChannelCongestion.Good),
                    Channel(13, "暗黑城", ChannelCongestion.Crowded, 20, 30),
                    Channel(14, "雷鸣废墟", ChannelCongestion.Full, 50, 60, false),
                }, false));

                List<SceneSprite> sprites = screen.Build(2800);

                List<SceneSprite> titles = All(sprites, "channel-item-title");
                Assert.AreEqual(4, titles.Count, "每个频道一行名字");
                Assert.GreaterOrEqual(titles[0].Frame.Width, 40, "ch11格兰之森");
                Assert.AreEqual(4, All(sprites, "channel-item-congestion").Count, "每个频道一个拥挤词");
                Assert.AreEqual(3, All(sprites, "channel-item-level").Count, "有推荐等级的才有第二行");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "channel-refresh" || s.AnimationPath == "channel-refresh-text"), "刷新(有图包时是图里的字,没有时由字体画)");
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-channel.png");
            }
        }

        [Test]
        public void ManyChannelsPageAndShowThePageNumber()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                var screen = new ChannelSelectScreen(SceneOf(assets), _fonts);
                screen.SetServer(ManyChannels(40));
                screen.NextPage();

                List<SceneSprite> sprites = screen.Build(1500);

                Assert.AreEqual(15, All(sprites, "channel-item-title").Count, "一页 15 个");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "channel-page-label"), "页码");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "channel-page-prev"));
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "channel-page-next"));
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-channel-pages.png");
            }
        }

        [Test]
        public void EveryStateOfTheChannelPageIsDrawnInChinese()
        {
            NeedFonts();
            using (DnfAssets assets = OpenArt())
            {
                ServerPickerScene scene = SceneOf(assets);

                var loading = new ChannelSelectScreen(scene, _fonts);
                loading.SetLoading();
                List<SceneSprite> sprites = loading.Build(1500);
                Assert.GreaterOrEqual(One(sprites, "channel-message").Frame.Width, 60, "正在获取频道列表…");
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-channel-loading.png");

                var empty = new ChannelSelectScreen(scene, _fonts);
                empty.SetServer(new DirectoryServer("希洛克", null, false));
                sprites = empty.Build(1500);
                Assert.GreaterOrEqual(One(sprites, "channel-message").Frame.Width, 60, "这个服务器没有可用的频道");
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-channel-empty.png");

                var failed = new ChannelSelectScreen(scene, _fonts);
                failed.SetFailed();
                failed.SetNotice(new OnlineNotice(OnlineNoticeKind.NetworkError));
                sprites = failed.Build(1500);
                Assert.GreaterOrEqual(One(sprites, "channel-message").Frame.Width, 60, "无法获取频道列表");
                Assert.IsTrue(sprites.Any(s => s.AnimationPath == "channel-notice"));
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-channel-failed.png");

                var connecting = new ChannelSelectScreen(scene, _fonts);
                connecting.SetServer(ManyChannels(5));
                connecting.SetBusy(ChannelBusy.Connecting);
                sprites = connecting.Build(1500);
                Assert.GreaterOrEqual(One(sprites, "channel-notice").Frame.Width, 60, "正在连接频道…(列表还在,等待提示在提示条里)");
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-channel-connecting.png");

                var full = new ChannelSelectScreen(scene, _fonts);
                full.SetServer(ManyChannels(5));
                // 第 5 个频道(第二行第二列)是满载的:点它给一句说明
                full.Click(ChannelSelectScreen.ItemX + ChannelSelectScreen.ItemPitchX + 10, ChannelSelectScreen.ItemY + ChannelSelectScreen.ItemPitchY + 10);
                Assert.IsNotNull(full.Hint, "点满载的频道要有说明");
                sprites = full.Build(1500);
                AssertTextInsideTheScreen(sprites);
                Save(sprites, "zh-channel-full-hint.png");
            }
        }
    }
}
