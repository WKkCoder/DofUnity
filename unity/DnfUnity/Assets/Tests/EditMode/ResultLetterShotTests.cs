using System;
using System.Collections.Generic;
using System.IO;
using Dnf.Play;
using Dnf.Render;
using NUnit.Framework;
using UnityEngine;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 结算页的评分字母(M32,证据 E-124):用用户自己的数据,把 Rank_Letter.img 的九个帧各按结算页的摆法
    /// (<see cref="ResultArt.PlaceRankLetter"/>)排成一张 800×600 的拼图放在 out/m32/ 供本机查看,图里是原版美术,只在本机看,不入库、不贴到别处。
    /// 断言只看版式的结构:九帧都取得到,摆在屏幕里,整块字母在文字行的起始位置(<see cref="ResultArt.TextTop"/>)之上。没有 DNF_DIR 时跳过。
    /// </summary>
    public class ResultLetterShotTests
    {
        private static string DataDir()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Assert.Ignore("未设置 DNF_DIR(或目录里没有 Script.pvf),跳过评分字母的出图");
            }
            return dir;
        }

        private static void Save(List<SceneSprite> sprites, string name)
        {
            var canvas = new RgbaCanvas(800, 600);
            canvas.Clear(40, 40, 40, 255);
            TownScene.Render(canvas, sprites);
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "out", "m32"));
            Directory.CreateDirectory(dir);
            const int w = 800, h = 600;
            var flipped = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(canvas.Pixels, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);   // Unity 纹理的第 0 行在下面
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(flipped);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        [Test]
        public void TheNineRankLettersAreInTheImagePackAndFitAboveTheTextLines()
        {
            string dir = DataDir();
            using (DnfAssets assets = DnfAssets.Open(dir))
            {
                var sheet = new List<SceneSprite>();
                for (int frame = 0; frame < ResultArt.FrameCount; frame++)
                {
                    SceneSprite letter = ResultArt.PlaceRankLetter(assets, frame, DungeonSession.RewardPanelLeft, frame);
                    Assert.NotNull(letter, "评分字母的第 " + frame + " 帧取不到");
                    Assert.GreaterOrEqual(letter.ScreenX, 0, "第 " + frame + " 帧在屏幕左边之外");
                    Assert.GreaterOrEqual(letter.ScreenY, 0, "第 " + frame + " 帧在屏幕上边之外");
                    Assert.LessOrEqual(letter.ScreenX + letter.Frame.Width, 800, "第 " + frame + " 帧超出右边");
                    Assert.LessOrEqual(letter.ScreenY + letter.Frame.Height, ResultArt.TextTop, "第 " + frame + " 帧压到了文字行");
                    // 拼图:3 列 × 3 行,每格 266 × 200;帧的位置按结算页的摆法平移到格子里
                    int col = frame % 3, row = frame / 3;
                    sheet.Add(OverlayArt.Place(letter.Frame, letter.ScreenX + col * 266, letter.ScreenY - ResultArt.LetterTop + row * 200, frame, ResultArt.Tag));
                }
                Save(sheet, "rank-letters.png");
            }
        }
    }
}
