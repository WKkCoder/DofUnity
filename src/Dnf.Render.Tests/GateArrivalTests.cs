using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 进房间的落点(证据 E-074):客户端的随机数、出口的识别矩形、在矩形里取点、找最近的可站点,以及它们在房间里的接线
/// (舞台格、出口的门体、主角占位盒、朝向、出生区)。
/// 随机数、取点与找点三张表是在模拟器里直接运行客户端对应的函数得到的标准答案(合成的种子与输入,不含原版数据);
/// 房间级的期望值由独立的 Python 实现按同一组公式算出;其余是手写的规则测试。
/// </summary>
public class GateArrivalTests
{
    private const int HeroId = 1002;

    private static readonly (uint Seed, (int Value, uint Next)[] Outs)[] RandomVectors =
    {
        (0u, new[] { (1012484, 2802067423u), (1716955679, 3256818826u), (1792309082, 2941955441u), (229610924, 1772930244u) }),
        (1u, new[] { (476707713, 662824084u), (1186278907, 2516284547u), (505671508, 3210001534u), (2137716191, 836760821u) }),
        (2u, new[] { (952403967, 2818548041u), (656651735, 1775750268u), (1367566158, 3478047627u), (1899386387, 4195558694u) }),
        (60u, new[] { (665313404, 3296485963u), (2125323715, 1774435046u), (1925478902, 1844851837u), (940183980, 1437339712u) }),
        (100u, new[] { (393052193, 3626098323u), (249735217, 2217834958u), (2015305992, 3976760965u), (2129727915, 2645268456u) }),
        (1234567u, new[] { (213927371, 3385588050u), (331976797, 3697125081u), (416275383, 867662668u), (1468106342, 711337115u) }),
        (2147483647u, new[] { (1670702726, 2793827114u), (99100226, 1849869457u), (931463008, 526425700u), (467940729, 561616019u) }),
        (2147483648u, new[] { (1012484, 654583775u), (1716955679, 1109335178u), (1792309082, 794471793u), (229610924, 3920413892u) }),
        (3735928559u, new[] { (1075635910, 1791404762u), (1410355045, 2003110913u), (390111939, 2730713236u), (828309080, 1582833795u) }),
        (4294967295u, new[] { (1670702726, 646343466u), (99100226, 3997353105u), (931463008, 2673909348u), (467940729, 2709099667u) }),
        (1103515245u, new[] { (706831626, 2500503280u), (543562068, 1565814671u), (1756651127, 2658601978u), (991978253, 4144838561u) }),
        (49212u, new[] { (1422335492, 302260811u), (1937151934, 2881387238u), (1459738943, 4181718141u), (645206480, 3117469760u) }),
    };

    private static readonly (int X, int Y, int Width, int Height, int BodyWidth, int BodyDepth, uint Seed, int ExpectedX, int ExpectedY, uint SeedAfter)[] PointVectors =
    {
        (1206, 182, 140, 1, 10, 11, 1234567u, 1259, 187, 3385588050u),
        (56, 189, 20, 1, 140, 20, 1234567u, 126, 199, 1234567u),
        (1139, 180, 1, 1, 41, 60, 986632417u, 1159, 210, 986632417u),
        (550, 147, 120, 1, 300, 10, 2665917410u, 700, 152, 2665917410u),
        (0, 296, 140, 11, 300, 10, 1234567u, 150, 302, 3385588050u),
        (1114, 177, 41, 11, 52, 0, 16404u, 1140, 177, 537142787u),
        (633, 251, 20, 1, 52, 200, 49212u, 659, 351, 49212u),
        (793, 286, 140, 11, 0, 10, 85687877u, 897, 291, 1947361447u),
        (336, 251, 120, 11, 52, 0, 1234567u, 409, 252, 3697125081u),
        (403, 167, 140, 106, 41, 10, 1u, 436, 216, 2516284547u),
        (662, 110, 120, 120, 300, 10, 2409341228u, 812, 216, 753232891u),
        (305, 280, 140, 106, 0, 11, 1301305739u, 310, 293, 3328733629u),
        (310, 130, 41, 11, 300, 10, 49212u, 460, 135, 302260811u),
        (1272, 187, 140, 106, 10, 5, 1u, 1300, 226, 2516284547u),
        (972, 125, 140, 8, 52, 60, 1234567u, 1027, 155, 3385588050u),
        (228, 210, 60, 120, 140, 200, 16404u, 298, 310, 16404u),
        (124, 243, 60, 106, 41, 20, 1422758801u, 160, 301, 1859513235u),
        (554, 142, 60, 8, 40, 5, 16404u, 583, 144, 1700019198u),
        (211, 146, 1, 106, 52, 5, 3735928559u, 237, 158, 1791404762u),
        (508, 134, 20, 106, 300, 5, 1u, 658, 139, 662824084u),
        (804, 225, 120, 1, 140, 60, 1u, 874, 255, 1u),
        (1015, 281, 1, 11, 140, 11, 49212u, 1085, 286, 302260811u),
        (633, 259, 140, 106, 40, 5, 1589597931u, 741, 311, 1404601373u),
        (1044, 177, 120, 11, 300, 60, 1234567u, 1194, 207, 1234567u),
        (348, 281, 1, 106, 40, 0, 3735928559u, 368, 287, 1791404762u),
        (952, 185, 120, 106, 52, 11, 49212u, 1037, 284, 2881387238u),
        (882, 186, 41, 1, 10, 60, 3735928559u, 893, 216, 1791404762u),
        (1074, 245, 60, 1, 10, 60, 872207562u, 1103, 275, 4222415537u),
        (42, 274, 60, 11, 0, 20, 1234567u, 47, 284, 3385588050u),
        (328, 240, 1, 11, 140, 60, 2026287968u, 398, 270, 2026287968u),
    };

    private static readonly (int X, int Y, int Radius, (int X, int Y)[] Free, bool Found, int OutX, int OutY, int Probes)[] SnapVectors =
    {
        (41, -5, 200, Array.Empty<(int X, int Y)>(), false, 0, 0, 17689),
        (180, 114, 60, new[] { (-60, 168), (174, 105), (174, 117), (177, 114), (183, 93), (210, 138), (366, 312), (369, 315) }, true, 177, 114, 3),
        (-1, 18, 200, new[] { (-139, -132), (2, 15), (5, 33) }, true, 2, 15, 7),
        (79, 244, 200, new[] { (-92, 46), (-20, 317), (-5, 445), (85, 232), (85, 259), (91, 229), (145, 484), (319, 145) }, true, 85, 232, 68),
        (15, 86, 60, new[] { (21, 89) }, true, 21, 89, 14),
        (22, 379, 60, new[] { (142, 529) }, false, 0, 0, 1681),
        (341, 58, 200, new[] { (143, -15), (338, 55), (448, -92) }, true, 338, 55, 9),
        (120, 187, 30, new[] { (102, 142) }, false, 0, 0, 441),
        (-18, 221, 30, new[] { (-21, 215), (75, 461) }, true, -21, 215, 21),
        (57, 209, 30, new[] { (51, 215), (57, 209) }, true, 57, 209, 1),
        (68, 293, 200, Array.Empty<(int X, int Y)>(), false, 0, 0, 17689),
        (350, 74, 60, new[] { (254, 272) }, false, 0, 0, 1681),
        (295, 331, 200, new[] { (310, 316) }, true, 310, 316, 119),
        (292, -17, 30, new[] { (247, -59) }, false, 0, 0, 441),
        (108, 317, 200, new[] { (93, 302), (123, 320) }, true, 123, 320, 86),
        (300, 168, 200, new[] { (150, 308), (201, 366), (255, 126), (288, 168), (297, 153), (309, 162), (327, 213), (498, 47) }, true, 309, 162, 39),
        (117, 146, 60, new[] { (-81, 261), (92, 386), (267, 239) }, false, 0, 0, 1681),
        (90, 63, 200, new[] { (45, 30), (93, 60) }, true, 93, 60, 7),
        (47, 109, 200, new[] { (119, 307) }, true, 119, 307, 17354),
        (137, 107, 60, new[] { (146, 95) }, true, 146, 95, 76),
        (153, 134, 60, Array.Empty<(int X, int Y)>(), false, 0, 0, 1681),
        (354, 48, 30, new[] { (336, 78), (348, 57), (351, 48), (354, 42), (366, 48), (381, 18), (446, 198) }, true, 351, 48, 3),
        (206, 290, 200, new[] { (197, 293), (203, 288), (253, 140) }, true, 197, 293, 32),
        (47, 213, 200, new[] { (44, 207), (47, 228), (48, 216) }, true, 44, 207, 21),
        (219, 208, 200, new[] { (204, 213) }, false, 0, 0, 17689),
        (109, 50, 200, new[] { (85, -190), (112, 50), (124, 44) }, true, 112, 50, 2),
        (114, 376, 200, new[] { (120, 376), (144, 382) }, true, 120, 376, 10),
        (400, 0, 60, new[] { (403, -3), (430, -99), (640, 3) }, true, 403, -3, 7),
        (186, 4, 200, new[] { (177, -8), (186, 4), (231, -38) }, true, 186, 4, 1),
        (119, 326, 60, new[] { (-79, 407), (110, 347), (119, 326) }, true, 119, 326, 1),
        (64, 275, 30, Array.Empty<(int X, int Y)>(), false, 0, 0, 441),
        (267, 98, 60, new[] { (27, 323), (69, 177), (171, -52), (237, 117), (261, 101), (264, 104), (288, 89), (294, -103) }, true, 261, 101, 16),
        (334, 344, 30, new[] { (352, 323), (355, 338) }, true, 355, 338, 183),
        (122, 199, 30, new[] { (221, 133) }, false, 0, 0, 441),
        (91, 180, 30, new[] { (46, 147) }, false, 0, 0, 441),
        (210, 330, 200, new[] { (207, 396), (208, 333), (210, 327) }, true, 210, 327, 5),
        (261, 250, 60, new[] { (240, 241), (246, 262), (249, 244), (252, 253), (264, 250), (282, 262), (303, 184), (462, 328) }, true, 264, 250, 2),
        (381, 223, 200, new[] { (372, 214), (372, 232), (390, 223) }, true, 390, 223, 26),
        (393, 223, 60, new[] { (363, 268), (390, 223), (432, -17) }, true, 390, 223, 3),
        (336, 342, 200, new[] { (333, 342) }, true, 333, 342, 3),
        (364, 305, 60, Array.Empty<(int X, int Y)>(), false, 0, 0, 1681),
        (95, 12, 200, new[] { (98, 9), (98, 15), (107, 27), (271, 252), (296, -51) }, true, 98, 15, 6),
        (209, 275, 200, new[] { (449, 140) }, false, 0, 0, 17689),
        (363, 73, 200, new[] { (264, -10) }, false, 0, 0, 17689),
        (230, 40, 60, new[] { (239, 28) }, true, 239, 28, 76),
        (306, 25, 30, new[] { (108, -146), (303, 16), (303, 28) }, true, 303, 28, 8),
        (294, 294, 200, new[] { (309, 228) }, true, 309, 228, 1892),
        (53, 273, 200, new[] { (-28, 72), (2, 207), (59, 273), (62, 264), (254, 81) }, true, 59, 273, 10),
        (166, 191, 200, new[] { (166, 191), (172, 203) }, true, 166, 191, 1),
        (87, 124, 30, new[] { (81, 139) }, true, 81, 139, 99),
        (213, 25, 30, Array.Empty<(int X, int Y)>(), false, 0, 0, 441),
        (255, 374, 200, new[] { (66, 575), (189, 344), (243, 362), (333, 275), (354, 467) }, true, 243, 362, 81),
        (287, 43, 200, new[] { (282, 73) }, false, 0, 0, 17689),
        (304, 106, 30, new[] { (64, 103), (202, -134), (259, 127), (307, 109), (403, 25) }, true, 307, 109, 6),
        (254, 233, 60, new[] { (251, 230), (254, 233) }, true, 254, 233, 1),
        (29, 393, 60, new[] { (29, 399) }, true, 29, 399, 12),
        (5, 295, 200, new[] { (-145, 337), (-10, 310), (2, 310) }, true, 2, 310, 91),
        (368, 141, 30, new[] { (518, 123) }, false, 0, 0, 441),
        (237, 378, 200, new[] { (192, 336), (234, 366), (277, 423) }, true, 234, 366, 61),
        (84, 47, 30, new[] { (-17, 197), (82, 38), (93, 35) }, true, 93, 35, 76),
    };

    private static (int X, int Y, int Width, int Height) Rect(WorldRect r)
    {
        return (r.X, r.Y, r.Width, r.Height);
    }

    // ---------- 随机数 ----------

    [Fact]
    public void TheClientRandomMatchesTheEmulatedClientFunctionStepByStep()
    {
        foreach (var vector in RandomVectors)
        {
            uint state = vector.Seed;
            for (int i = 0; i < vector.Outs.Length; i++)
            {
                int value = ClientRandom.Next(ref state);
                Assert.True((vector.Outs[i].Value, vector.Outs[i].Next) == (value, state), "种子 " + vector.Seed + " 的第 " + i + " 次");
            }
        }
    }

    [Fact]
    public void TheClientRandomIsAlwaysNonNegativeAndTheSeedLandsOnTheThirdStep()
    {
        uint seed = 12345;
        for (int i = 0; i < 2000; i++)
        {
            uint before = seed;
            int value = ClientRandom.Next(ref seed);
            Assert.True(value >= 0, "第 " + i + " 次");
            uint s1 = unchecked(before * ClientRandom.Multiplier + ClientRandom.Increment);
            uint s2 = unchecked(s1 * ClientRandom.Multiplier + ClientRandom.Increment);
            uint s3 = unchecked(s2 * ClientRandom.Multiplier + ClientRandom.Increment);
            Assert.Equal(s3, seed);
        }
    }

    [Fact]
    public void TheSeedIsTheProductOfTheHeroIdAndTheMapIdWrappedTo32Bits()
    {
        Assert.Equal(3006u, GateArrival.SeedFor(1002, 3));
        Assert.Equal(1705032704u, GateArrival.SeedFor(2000000000, 3));     // 6 000 000 000 − 2^32
        Assert.Equal(uint.MaxValue, GateArrival.SeedFor(-1, 1));
        Assert.Equal(0u, GateArrival.SeedFor(0, 17));
        Assert.True(GateArrival.DefaultHeroObjectId > 0);
    }

    // ---------- 矩形里取点 ----------

    [Fact]
    public void ThePointInTheRectangleMatchesTheEmulatedClientFunctionIncludingTheSeedItLeavesBehind()
    {
        foreach (var v in PointVectors)
        {
            uint seed = v.Seed;
            GateArrival.RandomPoint(new WorldRect(v.X, v.Y, v.Width, v.Height), v.BodyWidth, v.BodyDepth, ref seed, out int x, out int y);
            Assert.True((v.ExpectedX, v.ExpectedY, v.SeedAfter) == (x, y, seed),
                "矩形 (" + v.X + "," + v.Y + "," + v.Width + "," + v.Height + ") 盒 " + v.BodyWidth + "×" + v.BodyDepth + " 种子 " + v.Seed);
        }
    }

    [Fact]
    public void ThePointKeepsTheWholeBodyInsideTheRectangleAndSkipsTheRandomNumberWhenTheBodyDoesNotFit()
    {
        var area = new WorldRect(100, 50, 120, 106);
        uint seed = 7;
        for (int i = 0; i < 500; i++)
        {
            GateArrival.RandomPoint(area, 40, 10, ref seed, out int x, out int y);
            Assert.InRange(x - 20, area.X, area.X + area.Width - 40);
            Assert.InRange(y - 5, area.Y, area.Y + area.Height - 10);
        }

        // 盒子比矩形还大:不取随机数(种子不动),点落在 矩形左上角 + 盒的一半
        uint untouched = 99;
        GateArrival.RandomPoint(new WorldRect(10, 20, 30, 8), 40, 10, ref untouched, out int cx, out int cy);
        Assert.Equal((30, 25, 99u), (cx, cy, untouched));

        // 刚好等宽:只有一种取法,但随机数照样被消耗(一个方向一个)
        uint s = 1;
        GateArrival.RandomPoint(new WorldRect(0, 0, 40, 10), 40, 10, ref s, out int ex, out int ey);
        Assert.Equal((20, 5), (ex, ey));
        uint two = 1;
        ClientRandom.Next(ref two);
        ClientRandom.Next(ref two);
        Assert.Equal(two, s);
    }

    // ---------- 找最近的可站点 ----------

    [Fact]
    public void TheNearestFreeSearchMatchesTheEmulatedClientFunctionIncludingTheNumberOfProbes()
    {
        foreach (var v in SnapVectors)
        {
            var free = new HashSet<(int, int)>(v.Free.Select(p => (p.X, p.Y)));
            int probes = 0;
            bool found = GateArrival.NearestFree((px, py) =>
            {
                probes++;
                return free.Contains((px, py));
            }, v.X, v.Y, v.Radius, GateArrival.NearestFreeStep, out int outX, out int outY);
            string name = "(" + v.X + "," + v.Y + ") 半径 " + v.Radius;
            Assert.True(v.Found == found, name);
            if (found)
            {
                Assert.True((v.OutX, v.OutY) == (outX, outY), name);
            }
            Assert.True(v.Probes == probes, name + " 的探测次数 " + probes + " ≠ " + v.Probes);
        }
    }

    [Fact]
    public void TheNearestFreeSearchLooksAtTheOriginFirstAndOnlyThereWhenTheStepExceedsTheRadius()
    {
        int probes = 0;
        Assert.True(GateArrival.NearestFree((x, y) =>
        {
            probes++;
            return true;
        }, 10, 20, 200, 3, out int ox, out int oy));
        Assert.Equal((10, 20, 1), (ox, oy, probes));

        probes = 0;
        Assert.False(GateArrival.NearestFree((x, y) =>
        {
            probes++;
            return false;
        }, 10, 20, 2, 3, out ox, out oy));
        Assert.Equal((10, 20, 1), (ox, oy, probes));                     // 半径 2 < 步长 3:一圈也不看

        // 步长 3、半径 3:原点 + 第一圈(t = 0 与 3 的 8 个点去掉重复:(x+3,y)、(x−3,y)、(x,y+3)、(x,y−3),(x+3,y+3)、(x+3,y−3)、(x−3,y+3)、(x−3,y−3))
        probes = 0;
        Assert.False(GateArrival.NearestFree((x, y) =>
        {
            probes++;
            return false;
        }, 0, 0, 3, 3, out _, out _));
        Assert.Equal(1 + 8, probes);

        Assert.Throws<ArgumentNullException>(() => GateArrival.NearestFree(null!, 0, 0, 10, 3, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => GateArrival.NearestFree((x, y) => true, 0, 0, 10, 0, out _, out _));
        Assert.Equal((200, 3), (GateArrival.NearestFreeRadius, GateArrival.NearestFreeStep));
    }

    // ---------- 出口的识别矩形 ----------

    [Fact]
    public void TheRecognitionRectanglesAreCenteredOnTheGateWithTheClientsDefaultSizes()
    {
        Assert.Equal((16, 67, 120, 106), Rect(GateAreas.For(GateSide.Left, 16, 120)));            // 左:左边贴着出口 x
        Assert.Equal((984, 67, 120, 106), Rect(GateAreas.For(GateSide.Right, 1104, 120)));        // 右:右边贴着出口 x
        Assert.Equal((490, -8, 140, 40), Rect(GateAreas.For(GateSide.Up, 560, 12)));              // 上下:以出口为中心
        Assert.Equal((490, 328, 140, 40), Rect(GateAreas.For(GateSide.Down, 560, 348)));

        // 脚本给的范围 (宽, 高) 换掉缺省值;不大于 0 的分量仍取缺省值
        Assert.Equal((16, 80, 100, 80), Rect(GateAreas.For(GateSide.Left, 16, 120, 100, 80)));
        Assert.Equal((984, 95, 120, 50), Rect(GateAreas.For(GateSide.Right, 1104, 120, 0, 50)));
        Assert.Equal((1004, 67, 100, 106), Rect(GateAreas.For(GateSide.Right, 1104, 120, 100, 0)));
        Assert.Equal((515, -8, 90, 40), Rect(GateAreas.For(GateSide.Up, 560, 12, 90, -1)));
        Assert.Equal((490, 328, 140, 40), Rect(GateAreas.For(GateSide.Down, 560, 348, -5, -5)));
        Assert.Equal((516, -8, 91, 41), Rect(GateAreas.For(GateSide.Up, 561, 12, 91, 41)));       // 奇数尺寸:一半向零取整
    }

    [Fact]
    public void ComputeBuildsTheRectanglesInGateOrderAndLeavesMissingGatesNull()
    {
        var pos = new List<int[]> { new[] { 16, 120 }, null, new[] { 560 }, new[] { 560, 348 } };
        var range = new List<int[]> { new[] { 100, 80 }, null, null, null };
        WorldRect?[] r = GateAreas.Compute(pos, range);
        Assert.Equal(4, r.Length);
        Assert.Equal((16, 80, 100, 80), Rect(r[0]!.Value));
        Assert.Null(r[1]);
        Assert.Null(r[2]);                                               // 只有一个数:没写完整
        Assert.Equal((490, 328, 140, 40), Rect(r[3]!.Value));

        Assert.All(GateAreas.Compute(null, null), x => Assert.Null(x));
        WorldRect?[] few = GateAreas.Compute(new List<int[]> { new[] { 16, 120 } }, null);
        Assert.Equal((16, 67, 120, 106), Rect(few[0]!.Value));
        Assert.Null(few[1]);
        Assert.Null(few[3]);
    }

    [Fact]
    public void ARectangleIsHalfOpenSoItsFarEdgesAreOutside()
    {
        var r = new WorldRect(10, 20, 30, 40);
        Assert.True(GateAreas.Contains(r, 10, 20));
        Assert.True(GateAreas.Contains(r, 39, 59));
        Assert.False(GateAreas.Contains(r, 40, 59));
        Assert.False(GateAreas.Contains(r, 39, 60));
        Assert.False(GateAreas.Contains(r, 9, 20));
        Assert.False(GateAreas.Contains(r, 10, 19));
    }

    [Fact]
    public void AFootBoxTouchingTheRectangleCountsAsOverlappingItEvenWhenTheFootPointIsOutside()
    {
        var r = new WorldRect(100, 200, 50, 30);       // x ∈ [100, 150),y ∈ [200, 230)
        // 占位盒 40 × 10:左右各 20、上下各 5,边界像素算在内
        Assert.True(GateAreas.OverlapsBox(r, 125, 215, 40, 10));       // 整个在里面
        Assert.False(GateAreas.Contains(r, 125, 195));                  // 脚点在矩形上面 5 像素:点不在里面…
        Assert.True(GateAreas.OverlapsBox(r, 125, 195, 40, 10));        // …但盒子的下沿 200 压到了矩形的上沿
        Assert.False(GateAreas.OverlapsBox(r, 125, 194, 40, 10));       // 再高一像素:下沿 199,够不着
        Assert.True(GateAreas.OverlapsBox(r, 125, 234, 40, 10));        // 盒子的上沿 229 还在矩形里(下沿 230 不含)
        Assert.False(GateAreas.OverlapsBox(r, 125, 235, 40, 10));       // 上沿 230:不含
        Assert.True(GateAreas.OverlapsBox(r, 80, 215, 40, 10));         // 盒子右沿 100 碰到矩形左沿
        Assert.False(GateAreas.OverlapsBox(r, 79, 215, 40, 10));
        Assert.True(GateAreas.OverlapsBox(r, 169, 215, 40, 10));        // 盒子左沿 149 还在矩形里
        Assert.False(GateAreas.OverlapsBox(r, 170, 215, 40, 10));       // 左沿 150:不含
    }

    [Fact]
    public void ADoorBodyReachesTheGateOnlyWhenItsBlockingCellsAreWithinTwentyPixelsOfTheGatePosition()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var obj = new GateObject(800, "x.obj", "[normal]", new List<GatePart>(), null);
        var pass = new byte[14 * 3];                                    // 14 列 × 3 行,只有最后一列(列 13)挡路
        for (int r = 0; r < 3; r++)
        {
            pass[r * 14 + 13] = 2;
        }
        var wall = new TileFile("wall", new List<KeyValuePair<string, int>>(), 80, pass);

        // 右出口在 (1104, 120),所在的格是 (69, 10);锚点 (13, a):通行格左上角 (56, 10 − a),挡格在列 69、行 10 − a 起的三行
        // 占位盒 40 × 10 沿出口位置上下各挪 20 像素以内能压到的行是 7 到 12,所以挡格的起始行在 [5, 12] 里才算"够得着"
        var gate = new RoomGate(assets, obj, GateSide.Right, 1104, 120);
        Assert.False(gate.BodyReachesGate(40, 10));                     // 没有门体
        gate.SetBody(wall, 13, 1);                                      // 行 9 到 11:盖着出口所在的 y
        Assert.True(gate.BodyReachesGate(40, 10));
        gate.SetBody(wall, 13, -2);                                     // 行 12 起:出口下方 20 像素处(y 140 ± 5 → 行 11 到 12)压得到
        Assert.True(gate.BodyReachesGate(40, 10));
        gate.SetBody(wall, 13, -3);                                     // 行 13 起:够不着
        Assert.False(gate.BodyReachesGate(40, 10));
        gate.SetBody(wall, 13, 5);                                      // 行 5 到 7:出口上方 20 像素处(y 100 ± 5 → 行 7 到 8)压得到
        Assert.True(gate.BodyReachesGate(40, 10));
        gate.SetBody(wall, 13, 6);                                      // 行 4 到 6:够不着
        Assert.False(gate.BodyReachesGate(40, 10));
        // 门体表里一个挡格也没有(装饰性的门):碰不到任何东西
        gate.SetBody(new TileFile("empty", new List<KeyValuePair<string, int>>(), 80, new byte[14 * 3]), 13, 1);
        Assert.False(gate.BodyReachesGate(40, 10));
    }

    [Fact]
    public void TheMapScriptsRecognizeRangeIsReadPerGateAndTheRectanglesFollowIt()
    {
        using var fx = new RoomFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        RoomMap map = RoomMap.Read(assets, RoomFixture.MapPath);
        Assert.Equal(new[] { new[] { 100, 80 }, new[] { 0, 0 }, new[] { 0, 30 }, new[] { 90, 0 } }, map.GateRanges.ToArray());
        IReadOnlyList<WorldRect?> rects = map.GateRects;
        Assert.Equal((16, 80, 100, 80), Rect(rects[(int)GateSide.Left]!.Value));                  // 左:100 × 80
        Assert.Equal((320, 77, 120, 106), Rect(rects[(int)GateSide.Right]!.Value));               // 右:缺省
        Assert.Equal((150, 45, 140, 30), Rect(rects[(int)GateSide.Up]!.Value));                   // 上:宽缺省、高 30
        Assert.Equal((175, 170, 90, 40), Rect(rects[(int)GateSide.Down]!.Value));                 // 下:宽 90、高缺省
        Assert.Same(rects, map.GateRects);                                                         // 只算一次

        // 没有 [pathgate pos] 也没有 [pathgate recognize range] 的城镇地图
        RoomMap town = RoomMap.Read(assets, "map/test/room/town.map");
        Assert.All(town.GateRanges, x => Assert.Null(x));
        Assert.All(town.GateRects, x => Assert.Null(x));
        Assert.Equal(4, town.GateRanges.Count);
    }

    // ---------- 舞台格 ----------

    private static readonly IReadOnlyList<int[]> NoGates = new List<int[]> { null, null, null, null };

    private static readonly IReadOnlyList<int[]> GatePositions = new List<int[]> { new[] { 16, 42 }, new[] { 432, 42 }, new[] { 224, 26 }, new[] { 224, 58 } };

    /// <summary>14 列 × 6 行的通行表:只有 <paramref name="freeRows"/> 里的行能走(0),其余整行挡路(2)。</summary>
    private static byte[] Band(params int[] freeRows)
    {
        var table = new byte[14 * 6];
        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 14; c++)
            {
                table[r * 14 + c] = Array.IndexOf(freeRows, r) >= 0 ? (byte)0 : (byte)2;
            }
        }
        return table;
    }

    /// <summary>14 列 × 6 行的墙:左墙是第 0 到 3 列挡路,右墙是第 10 到 13 列挡路(每一行)。</summary>
    private static byte[] Wall(bool left)
    {
        var table = new byte[14 * 6];
        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 14; c++)
            {
                table[r * 14 + c] = (left ? c < 4 : c >= 10) ? (byte)2 : (byte)0;
            }
        }
        return table;
    }

    private static TileFile Tile(byte[] pass)
    {
        return new TileFile("t", new List<KeyValuePair<string, int>>(), 80, pass);
    }

    /// <summary>两块一样的瓦片拼成的房间(28 列 × 6 行 = 448 × 72 像素),四个出口在 <see cref="GatePositions"/>。</summary>
    private static RoomMap MapOf(byte[] pass, WorldRect? start = null, IReadOnlyList<int[]> gates = null)
    {
        return new RoomMap("map/test/arrival.map", "[normal]", new List<TileFile> { Tile(pass), Tile(pass) }, 50, 80, 120,
            new List<RoomBackground>(), new List<MapAnimation>(), gates ?? GatePositions, start);
    }

    [Fact]
    public void TheGridLaysTheTilePassTablesSideBySideAndTreatsEverythingOutsideAsBlocked()
    {
        var map = new RoomMap("map/test/grid.map", "[normal]", new List<TileFile> { Tile(Band(2, 3, 4)), Tile(new byte[14 * 4]) }, 50, 80, 120,
            new List<RoomBackground>(), new List<MapAnimation>(), NoGates, null);
        var grid = new RoomGrid(map);
        Assert.Equal((28, 6, 448, 72), (grid.Columns, grid.Rows, grid.WidthPixels, grid.HeightPixels));   // 行数取第一块瓦片的
        Assert.Equal((2, 0, 0, 2), (grid.TypeAt(0, 0), grid.TypeAt(0, 2), grid.TypeAt(13, 4), grid.TypeAt(0, 5)));
        Assert.Equal((0, 0, 2, 2), (grid.TypeAt(14, 0), grid.TypeAt(14, 3), grid.TypeAt(14, 4), grid.TypeAt(27, 5)));   // 第二块只有 4 行:其余按挡路
        Assert.Equal((2, 2, 2, 2), (grid.TypeAt(-1, 2), grid.TypeAt(28, 2), grid.TypeAt(5, -1), grid.TypeAt(5, 6)));

        Assert.Throws<ArgumentNullException>(() => new RoomGrid(null!));
        var empty = new RoomGrid(new RoomMap("map/test/empty.map", "[normal]", new List<TileFile>(), 50, 80, 120,
            new List<RoomBackground>(), new List<MapAnimation>(), NoGates, null));
        Assert.Equal((0, 0), (empty.Columns, empty.Rows));
        Assert.False(empty.IsFree(0, 0, 0, 0));
    }

    [Fact]
    public void ABodyStandsOnTheGridWhenNoCoveredCellIsBlockedAndTheBoxIncludesItsBorderPixels()
    {
        var grid = new RoomGrid(MapOf(Band(2, 3, 4)));                   // 能站的 y:盒的上沿 y − 5 ≥ 24、下沿 y + 5 ≤ 59 → 29 到 54
        Assert.True(grid.IsFree(100, 36, 40, 10));
        Assert.False(grid.IsFree(100, 28, 40, 10));                      // 上沿 23 → 第 1 行
        Assert.True(grid.IsFree(100, 29, 40, 10));                       // 上沿 24、下沿 34 → 都在第 2 行
        Assert.True(grid.IsFree(100, 54, 40, 10));                       // 下沿 59 → 第 4 行
        Assert.False(grid.IsFree(100, 55, 40, 10));                      // 下沿 60 → 第 5 行
        Assert.False(grid.IsFree(100, -1, 40, 10));                      // 中心先要在地图里
        Assert.False(grid.IsFree(100, 72, 40, 10));
        Assert.False(grid.IsFree(-1, 36, 40, 10));
        Assert.True(grid.IsFree(447, 36, 40, 10));                       // 盒子伸出右边:格子夹在地图里,中心还在图里就算
        Assert.False(grid.IsFree(448, 36, 40, 10));
        Assert.True(grid.IsFree(10, 36, 41, 10));                        // 盒子伸出左边,奇数宽左右各取 20
        Assert.True(grid.IsFree(20, 36, 41, 10));

        // 宽与深都是 0 的对象只看中心在不在地图里;只有一个是 0 照常算足迹
        Assert.True(grid.IsFree(100, 10, 0, 0));
        Assert.False(grid.IsFree(100, 72, 0, 0));
        Assert.False(grid.IsFree(100, 10, 0, 10));
        Assert.True(grid.IsFree(100, 36, 0, 10));
    }

    [Fact]
    public void AGateBodyStampedOnTheGridRaisesCellsToTheLargerValueAndStretchesToTheMapEdge()
    {
        // 右出口的墙:表左上角在第 12 列、第 0 行 → 第 22 到 25 列,第 13 列的值再向右延伸到地图右缘(第 27 列),不向左延伸
        var grid = new RoomGrid(MapOf(Band(2, 3, 4)));
        grid.Stamp(new GateBody(Tile(Wall(false)), 12, 0, GateSide.Right));
        Assert.Equal(new byte[] { 0, 2, 2, 2, 2, 2, 2 }, Enumerable.Range(21, 7).Select(c => grid.TypeAt(c, 3)).ToArray());
        Assert.True(grid.IsFree(331, 36, 40, 10));                       // 盒的右沿 351 → 第 21 列
        Assert.False(grid.IsFree(332, 36, 40, 10));                      // 352 → 第 22 列

        // 左出口的墙:表左上角在第 3 列 → 第 3 到 6 列,第 0 列的值向左延伸到地图左缘(第 0 到 2 列),不向右延伸
        var left = new RoomGrid(MapOf(Band(2, 3, 4)));
        left.Stamp(new GateBody(Tile(Wall(true)), 3, 0, GateSide.Left));
        Assert.Equal(new byte[] { 2, 2, 2, 2, 2, 2, 2, 0 }, Enumerable.Range(0, 8).Select(c => left.TypeAt(c, 3)).ToArray());
        Assert.False(left.IsFree(131, 36, 40, 10));                      // 盒的左沿 111 → 第 6 列
        Assert.True(left.IsFree(132, 36, 40, 10));                       // 112 → 第 7 列

        // 取较大值:1 抬高 0、盖不低 2
        var merge = new RoomGrid(MapOf(Band(2, 3, 4)));
        var ones = new byte[14 * 6];
        for (int c = 0; c < 14; c++)
        {
            ones[0 * 14 + c] = 1;
            ones[3 * 14 + c] = 1;
        }
        merge.Stamp(new GateBody(Tile(ones), 0, 0, GateSide.Right));
        Assert.Equal((1, 2, 1, 2), (merge.TypeAt(5, 3), merge.TypeAt(5, 0), merge.TypeAt(27, 3), merge.TypeAt(27, 0)));

        // 表伸出地图上下沿的部分被裁掉
        var clipped = new RoomGrid(MapOf(Band(2, 3, 4)));
        clipped.Stamp(new GateBody(Tile(Wall(false)), 12, 4, GateSide.Right));        // 第 4、5 行盖到,第 6 行起在地图外
        Assert.Equal((0, 2, 2), (clipped.TypeAt(22, 3), clipped.TypeAt(22, 4), clipped.TypeAt(22, 5)));
        clipped.Stamp(new GateBody(Tile(Wall(false)), 12, -2, GateSide.Right));       // 表的第 2 行起才进地图:第 0 到 3 行
        Assert.Equal(2, clipped.TypeAt(22, 3));

        Assert.Throws<ArgumentNullException>(() => grid.Stamp(null!));
    }

    // ---------- 进房间 ----------

    /// <summary>一个带主角的房间会话(主角用夹具里的 .chr,出生在 (300, 40))。</summary>
    private sealed class Rig : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomSession Room { get; }
        public TownHero Hero => Room.Hero;

        public Rig(RoomMap map, string chr = AvatarFixture.ChrPath, Func<DnfAssets, List<RoomGate>> gates = null)
        {
            Assets = DnfAssets.Open(_fx.Root);
            AvatarClass cls = AvatarClass.FromChr(Assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
            TownHero hero = TownHero.Create(Assets, chr, outfit, new RoomWalkArea(map), 300, 40, false);
            var content = new RoomContent(map, null, null, null, gates?.Invoke(Assets));
            Room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    private static List<RoomGate> BodiedGates(DnfAssets assets)
    {
        var left = new RoomGate(assets, new GateObject(800, "x.obj", "[normal]", new List<GatePart>(), null), GateSide.Left, 16, 42);
        left.SetBody(Tile(Wall(true)), 0, 3);
        var right = new RoomGate(assets, new GateObject(801, "x.obj", "[normal]", new List<GatePart>(), null), GateSide.Right, 432, 42);
        right.SetBody(Tile(Wall(false)), 13, 3);
        return new List<RoomGate> { left, right };
    }

    // 期望值:(出口一侧, 地图编号, 主角编号 → 随机点, 最终落点),用独立的 Python 实现按 E-074 的公式算出(能站的 y 是 29 到 54)
    [Theory]
    [InlineData(GateSide.Left, 2, 1002, 36, 51, 36, 51)]
    [InlineData(GateSide.Left, 1, 1002, 93, 26, 93, 29)]
    [InlineData(GateSide.Right, 3, 1002, 407, 4, 407, 31)]
    [InlineData(GateSide.Up, 1, 1002, 207, 39, 207, 39)]
    [InlineData(GateSide.Up, 7, 1002, 201, 12, 201, 30)]
    [InlineData(GateSide.Down, 7, 1002, 201, 44, 201, 44)]
    [InlineData(GateSide.Down, 3, 1002, 257, 63, 257, 54)]
    [InlineData(GateSide.Left, 1, 1000, 57, 73, 57, 52)]
    [InlineData(GateSide.Right, 1, 1000, 353, 73, 353, 52)]
    public void TheArrivalIsTheRandomPointInTheGateRectangleMovedToTheNearestStandablePoint(GateSide entry, int mapId, int heroId, int randomX, int randomY, int x, int y)
    {
        using var rig = new Rig(MapOf(Band(2, 3, 4)));
        ArrivalPoint p = RoomArrival.ComputeAtGate(rig.Room, entry, mapId, heroId)!.Value;
        Assert.Equal((randomX, randomY, x, y, true), (p.RandomX, p.RandomY, p.X, p.Y, p.Found));
    }

    // 占位盒取自 .chr 的 [width](夹具里的动作脚本是 36 × 12,能站的 y 是 30 到 53)
    [Theory]
    [InlineData(GateSide.Left, 2, 1002, 54, 34, 54, 34)]
    [InlineData(GateSide.Left, 1, 1002, 60, 77, 60, 53)]
    [InlineData(GateSide.Left, 6, 1002, 62, 27, 62, 30)]
    public void TheBodySizeFromTheCharacterScriptDecidesWhereTheHeroFitsInsideTheRectangle(GateSide entry, int mapId, int heroId, int randomX, int randomY, int x, int y)
    {
        using var rig = new Rig(MapOf(Band(2, 3, 4)), AvatarFixture.ActionChrPath);
        Assert.Equal((AvatarFixture.BodyWidthStat, AvatarFixture.BodyDepthStat), (rig.Hero.BodyWidth, rig.Hero.BodyDepth));
        ArrivalPoint p = RoomArrival.ComputeAtGate(rig.Room, entry, mapId, heroId)!.Value;
        Assert.Equal((randomX, randomY, x, y, true), (p.RandomX, p.RandomY, p.X, p.Y, p.Found));
    }

    // 出口门体盖在舞台格上:左墙盖住 x < 80(盒的左沿 ≥ 80 → x ≥ 100),右墙盖住 x ≥ 384(盒的右沿 x + 20 ≤ 383 → x ≤ 363)
    [Theory]
    [InlineData(GateSide.Left, 2, 1002, 36, 51, 102, 51)]
    [InlineData(GateSide.Right, 3, 1002, 407, 4, 362, 31)]
    [InlineData(GateSide.Left, 1, 1002, 93, 26, 102, 29)]
    [InlineData(GateSide.Right, 1, 1002, 389, 26, 362, 29)]
    [InlineData(GateSide.Up, 7, 1002, 201, 12, 201, 30)]
    public void ArrivalsAreMovedClearOfTheGateBodiesStampedOnTheRoomsGrid(GateSide entry, int mapId, int heroId, int randomX, int randomY, int x, int y)
    {
        using var rig = new Rig(MapOf(Band(2, 3, 4)), gates: BodiedGates);
        ArrivalPoint p = RoomArrival.ComputeAtGate(rig.Room, entry, mapId, heroId)!.Value;
        Assert.Equal((randomX, randomY, x, y, true), (p.RandomX, p.RandomY, p.X, p.Y, p.Found));
    }

    [Fact]
    public void TheRoomSessionsGridHasTheGateBodiesStampedOnItOnce()
    {
        using var rig = new Rig(MapOf(Band(2, 3, 4)), gates: BodiedGates);
        RoomGrid grid = rig.Room.Grid;
        Assert.Same(grid, rig.Room.Grid);
        Assert.Equal("2222200000000000000000002222", string.Concat(Enumerable.Range(0, 28).Select(c => grid.TypeAt(c, 3))));
        Assert.Equal("2222222222222222222222222222", string.Concat(Enumerable.Range(0, 28).Select(c => grid.TypeAt(c, 0))));

        using var plain = new Rig(MapOf(Band(2, 3, 4)));
        Assert.Equal(new string('0', 28), string.Concat(Enumerable.Range(0, 28).Select(c => plain.Room.Grid.TypeAt(c, 3))));
    }

    // 窄带里(只有第 3 行能走)36 × 12 的盒子哪也站不下:找不到可站点,退到离随机点最近的可走点(列不变,y 夹到 [36, 47])
    [Theory]
    [InlineData(1, 60, 77, 60, 47)]
    [InlineData(2, 54, 34, 54, 36)]
    [InlineData(3, 51, 43, 51, 43)]
    public void WhenNothingWithinTheRadiusFitsTheHeroGoesToTheNearestWalkablePoint(int mapId, int randomX, int randomY, int x, int y)
    {
        using var rig = new Rig(MapOf(Band(3)), AvatarFixture.ActionChrPath);
        ArrivalPoint p = RoomArrival.ComputeAtGate(rig.Room, GateSide.Left, mapId, HeroId)!.Value;
        Assert.Equal((randomX, randomY, x, y, false), (p.RandomX, p.RandomY, p.X, p.Y, p.Found));
    }

    [Theory]
    [InlineData(GateSide.Left, false, false)]
    [InlineData(GateSide.Left, true, false)]
    [InlineData(GateSide.Right, false, true)]
    [InlineData(GateSide.Right, true, true)]
    [InlineData(GateSide.Up, false, false)]
    [InlineData(GateSide.Up, true, true)]
    [InlineData(GateSide.Down, false, false)]
    [InlineData(GateSide.Down, true, true)]
    public void PlacingTheHeroAtAGateFacesInwardAtTheSideGatesAndKeepsTheFacingAtTheOthers(GateSide entry, bool carryFacingLeft, bool expectedFacingLeft)
    {
        using var rig = new Rig(MapOf(Band(2, 3, 4)));
        ArrivalPoint expected = RoomArrival.ComputeAtGate(rig.Room, entry, 5, HeroId)!.Value;
        Assert.True(RoomArrival.PlaceAtGate(rig.Room, entry, 5, HeroId, carryFacingLeft));
        TownHero hero = rig.Hero;
        Assert.Equal(((double)expected.X, (double)expected.Y, expectedFacingLeft, HeroMotion.Rest), (hero.X, hero.Y, hero.FacingLeft, hero.Motion));
    }

    // 出生区 (100, 30, 60, 20):第一个房间也是随机点 + 站得下检查,朝右
    [Theory]
    [InlineData(AvatarFixture.ChrPath, 3, 135, 41)]
    [InlineData(AvatarFixture.ActionChrPath, 3, 125, 43)]
    public void TheFirstRoomPutsTheHeroAtARandomPointOfTheStartAreaFacingRight(string chr, int mapId, int x, int y)
    {
        using var rig = new Rig(MapOf(Band(2, 3, 4), new WorldRect(100, 30, 60, 20)), chr);
        ArrivalPoint p = RoomArrival.ComputeAtStart(rig.Room, mapId, HeroId)!.Value;
        Assert.Equal((x, y, x, y, true), (p.RandomX, p.RandomY, p.X, p.Y, p.Found));
        rig.Hero.Place(300, 40, true, HeroMotion.Rest, 0);
        Assert.True(RoomArrival.PlaceAtStart(rig.Room, mapId, HeroId));
        TownHero hero = rig.Hero;
        Assert.Equal(((double)x, (double)y, false, HeroMotion.Rest), (hero.X, hero.Y, hero.FacingLeft, hero.Motion));
    }

    [Fact]
    public void AMapWithoutAGatePositionOrAStartAreaLeavesTheHeroWhereItIs()
    {
        using var rig = new Rig(MapOf(Band(2, 3, 4), null, NoGates));
        double x = rig.Hero.X, y = rig.Hero.Y;
        Assert.Null(RoomArrival.ComputeAtGate(rig.Room, GateSide.Left, 1, HeroId));
        Assert.Null(RoomArrival.ComputeAtStart(rig.Room, 1, HeroId));
        Assert.False(RoomArrival.PlaceAtGate(rig.Room, GateSide.Left, 1, HeroId, false));
        Assert.False(RoomArrival.PlaceAtStart(rig.Room, 1, HeroId));
        Assert.Equal((x, y), (rig.Hero.X, rig.Hero.Y));

        Assert.Throws<ArgumentNullException>(() => RoomArrival.ComputeAtGate(null!, GateSide.Left, 1, HeroId));
        Assert.Throws<ArgumentNullException>(() => RoomArrival.ComputeAtStart(null!, 1, HeroId));
    }

    [Fact]
    public void TheSameHeroAndMapAlwaysGiveTheSameArrivalAndDifferentOnesDoNot()
    {
        using var rig = new Rig(MapOf(Band(2, 3, 4)));
        ArrivalPoint a = RoomArrival.ComputeAtGate(rig.Room, GateSide.Up, 7, HeroId)!.Value;
        ArrivalPoint b = RoomArrival.ComputeAtGate(rig.Room, GateSide.Up, 7, HeroId)!.Value;
        Assert.Equal((a.RandomX, a.RandomY, a.X, a.Y), (b.RandomX, b.RandomY, b.X, b.Y));
        var seen = new HashSet<(int, int)>();
        for (int map = 1; map <= 30; map++)
        {
            ArrivalPoint p = RoomArrival.ComputeAtGate(rig.Room, GateSide.Up, map, HeroId)!.Value;
            seen.Add((p.RandomX, p.RandomY));
        }
        Assert.True(seen.Count > 20);                                    // 地图编号不同,落点基本不同
    }

    // ---------- 主角占位盒 ----------

    [Fact]
    public void TheHeroBodySizeIsTheCharacterScriptsWidthTagOrTheDefaults()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        var unrestricted = new List<WorldRect>();

        TownHero plain = TownHero.Create(assets, AvatarFixture.ChrPath, outfit, unrestricted, 100, 50, false);
        Assert.Equal((TownHero.DefaultBodyWidth, TownHero.DefaultBodyDepth), (plain.BodyWidth, plain.BodyDepth));
        Assert.Equal((40, 10), (TownHero.DefaultBodyWidth, TownHero.DefaultBodyDepth));

        TownHero sized = TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, unrestricted, 100, 50, false);
        Assert.Equal((AvatarFixture.BodyWidthStat, AvatarFixture.BodyDepthStat), (sized.BodyWidth, sized.BodyDepth));

        sized.LoadBodySize(assets, AvatarFixture.ChrPath);               // 这个脚本没有 [width]:回到缺省值
        Assert.Equal((TownHero.DefaultBodyWidth, TownHero.DefaultBodyDepth), (sized.BodyWidth, sized.BodyDepth));
        sized.LoadBodySize(assets, AvatarFixture.ActionChrPath);
        Assert.Equal((AvatarFixture.BodyWidthStat, AvatarFixture.BodyDepthStat), (sized.BodyWidth, sized.BodyDepth));
        Assert.Throws<DnfFormatException>(() => sized.LoadBodySize(assets, "character/test/missing.chr"));
    }
}
