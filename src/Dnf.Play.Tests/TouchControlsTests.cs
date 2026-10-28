using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 触屏操作层(M13,本项目自己的设计,不是对原版的还原):摆法、摇杆的八向与死区与滞回、按钮的边沿与多点触摸、
/// 把"推到最外圈"翻译成原版奔跑认的那一次双击,以及时间表与键盘时间表给出同一组输入。不依赖游戏数据(主角用合成的动画与角色脚本)。
/// </summary>
public class TouchControlsTests
{
    private const double Frame = 1000.0 / 60.0;
    private static readonly IReadOnlyList<WorldRect> Unrestricted = new List<WorldRect>();

    public static IEnumerable<object[]> Screens()
    {
        // 宽 × 高,以及安全区边距占短边的比例(左右、下)
        yield return new object[] { 800, 600, 0.0, 0.0 };
        yield return new object[] { 1024, 768, 0.0, 0.0 };
        yield return new object[] { 1280, 720, 0.0, 0.0 };
        yield return new object[] { 1920, 1080, 0.0, 0.0 };
        yield return new object[] { 2400, 1080, 0.06, 0.03 };
        yield return new object[] { 2532, 1170, 0.06, 0.03 };
        yield return new object[] { 2796, 1290, 0.07, 0.04 };
        yield return new object[] { 2560, 1080, 0.0, 0.0 };
    }

    private static TouchControls New(int w = 1280, int h = 720)
    {
        return new TouchControls(w, h);
    }

    /// <summary>从静止位置按下摇杆,再朝 <paramref name="angleDeg"/>(0 = 右,90 = 下)拖到离原点 <paramref name="reach"/> 倍半径处。</summary>
    private static void Stick(TouchControls c, double angleDeg, double reach, int id = 1)
    {
        if (!c.StickActive)
        {
            Assert.True(c.Down(id, c.Layout.StickIdleX, c.Layout.StickIdleY));
        }
        double a = angleDeg * Math.PI / 180.0;
        double r = c.Layout.StickRadius * reach;
        c.Move(id, c.StickOriginX + Math.Cos(a) * r, c.StickOriginY + Math.Sin(a) * r);
    }

    private static (int X, int Y) Dir(TouchControls c)
    {
        TouchFrame f = c.Poll();
        return (f.DirX, f.DirY);
    }

    /// <summary>连取几帧(让奔跑的"双击"序列与退出奔跑的那一帧过去),返回最后一帧的方向。</summary>
    private static (int X, int Y) Settle(TouchControls c)
    {
        (int X, int Y) last = (0, 0);
        for (int i = 0; i < 4; i++)
        {
            last = Dir(c);
        }
        return last;
    }

    private static TownHero NewHero(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, Unrestricted, 100, 50, false, TownHero.DungeonRestTag);
    }

    /// <summary>取一帧输入并推进主角一帧(60 Hz);返回这一帧给主角的水平方向。</summary>
    private static int Step(TouchControls c, TownHero hero)
    {
        TouchFrame f = c.Poll();
        hero.Update(Frame, f.DirX, f.DirY, f.Attack, f.Jump);
        return f.DirX;
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheLayoutKeepsEveryControlOnScreenApartFromEachOtherAndOutOfTheStickZone(int w, int h, double sideInset, double bottomInset)
    {
        double side = sideInset * Math.Min(w, h), bottom = bottomInset * Math.Min(w, h);
        TouchLayout l = TouchLayout.For(w, h, side, 0, side, bottom);
        double u = l.Unit;
        Assert.Equal(Math.Min(w, h) / 100.0, u, 9);

        var buttons = new List<(TouchButton B, TouchDisc D)>();
        foreach (TouchButton b in Enum.GetValues(typeof(TouchButton)))
        {
            TouchDisc d = l.Button(b);
            buttons.Add((b, d));
            // 整个圆都在安全区里
            Assert.True(d.X - d.Radius >= side - 1e-6 && d.X + d.Radius <= w - side + 1e-6, b + " 水平方向出了安全区");
            Assert.True(d.Y - d.Radius >= 0 && d.Y + d.Radius <= h - bottom + 1e-6, b + " 竖直方向出了安全区");
            // 判定圈(放大后)不碰摇杆区:手指按在摇杆区里时不会被当成按钮
            double nx = Math.Max(l.StickZone.X, Math.Min(d.X, l.StickZone.Right));
            double ny = Math.Max(l.StickZone.Y, Math.Min(d.Y, l.StickZone.Bottom));
            double dist = Math.Sqrt((d.X - nx) * (d.X - nx) + (d.Y - ny) * (d.Y - ny));
            Assert.True(dist >= d.Radius * TouchControls.ButtonSlop, b + " 的判定圈压到了摇杆区");
        }
        for (int i = 0; i < buttons.Count; i++)
        {
            for (int j = i + 1; j < buttons.Count; j++)
            {
                Assert.True(buttons[i].D.GapTo(buttons[j].D) >= 0.4 * u, buttons[i].B + " 与 " + buttons[j].B + " 离得太近");
            }
        }

        // 摇杆区在安全区里,静止的摇杆(整个底圈)在摇杆区里与屏幕上
        Assert.True(l.StickZone.X >= side - 1e-6 && l.StickZone.Right <= w - side + 1e-6 && l.StickZone.Bottom <= h - bottom + 1e-6);
        Assert.True(l.StickZone.Contains(l.StickIdleX, l.StickIdleY));
        Assert.True(l.StickIdleX - l.StickRadius >= side && l.StickIdleY + l.StickRadius <= h - bottom && l.StickIdleY - l.StickRadius >= 0);
    }

    [Fact]
    public void TheLayoutRejectsAnEmptyScreen()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TouchLayout.For(0, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => TouchLayout.For(800, -1));
    }

    [Fact]
    public void TheStickMapsEightDirectionsAndHasADeadZone()
    {
        var expected = new (int Angle, int X, int Y)[]
        {
            (0, 1, 0), (45, 1, 1), (90, 0, 1), (135, -1, 1), (180, -1, 0), (225, -1, -1), (270, 0, -1), (315, 1, -1),
        };
        foreach ((int angle, int ex, int ey) in expected)
        {
            TouchControls c = New();
            Stick(c, angle, 0.65);
            Assert.Equal((ex, ey), Dir(c));
            c.Reset();
            Stick(c, angle, 0.2);                                  // 死区(0.30 倍半径)里:没有方向
            Assert.Equal((0, 0), Dir(c));
        }
    }

    [Fact]
    public void ThePlayerCanTellUpFromDownTheSameWayTheKeyboardPathDoes()
    {
        TouchControls c = New();
        Stick(c, 270, 0.65);                                       // 屏幕上方 = 键盘的向上键 = dirY -1
        Assert.Equal((0, -1), Dir(c));
        Stick(c, 90, 0.65);
        Assert.Equal((0, 1), Dir(c));
    }

    [Fact]
    public void DirectionHasHysteresisSoAFingerOnASectorBoundaryDoesNotFlicker()
    {
        TouchControls c = New();
        Stick(c, 0, 0.65);
        Assert.Equal((1, 0), Dir(c));
        double forwardEnter = -1, backExit = -1;
        (int X, int Y) last = (1, 0);
        int changes = 0;
        for (int a = 0; a <= 90; a++)
        {
            Stick(c, a, 0.65);
            var d = Dir(c);
            if (d != last)
            {
                changes++;
                if (d == (1, 1) && forwardEnter < 0) { forwardEnter = a; }
                last = d;
            }
        }
        Assert.Equal((0, 1), last);
        Assert.Equal(2, changes);                                  // (1,0) → (1,1) → (0,1),中间没有来回跳
        changes = 0;
        for (int a = 90; a >= 0; a--)
        {
            Stick(c, a, 0.65);
            var d = Dir(c);
            if (d != last)
            {
                changes++;
                if (last == (1, 1) && d == (1, 0)) { backExit = a; }
                last = d;
            }
        }
        Assert.Equal((1, 0), last);
        Assert.Equal(2, changes);
        // 往回转时在更靠近水平的角度才退回纯水平:竖直分量的进入阈值与退出阈值不同
        Assert.True(forwardEnter > 0 && backExit >= 0 && backExit < forwardEnter, "往回转时退出斜向的角度 " + backExit + " 应小于往前转时进入斜向的 " + forwardEnter);
    }

    [Fact]
    public void TheStickOriginFollowsAFingerThatDragsFarSoReversingIsQuick()
    {
        TouchControls c = New();
        double r = c.Layout.StickRadius;
        Assert.True(c.Down(1, c.Layout.StickIdleX, c.Layout.StickIdleY));
        double ox = c.StickOriginX;
        Assert.Equal(c.Layout.StickIdleX, ox, 9);
        c.Move(1, ox + 4 * r, c.StickOriginY);                    // 拖出去 4 倍半径
        Assert.Equal(ox + 4 * r - TouchControls.FollowLimit * r, c.StickOriginX, 6);   // 原点追到离手指 1.5 倍半径
        Assert.Equal((1, 0), Settle(c));
        c.Move(1, c.StickThumbX - 1.0 * r, c.StickOriginY);       // 往回只拖 1 倍半径:还在原点右边 0.5 倍
        Assert.Equal((1, 0), Settle(c));
        c.Move(1, c.StickOriginX - 0.5 * r, c.StickOriginY);      // 过了原点:反向
        Assert.Equal((-1, 0), Settle(c));
    }

    [Fact]
    public void ButtonsFireOncePerPressAndSeveralFingersWorkTogether()
    {
        TouchControls c = New();
        Stick(c, 0, 0.65, id: 1);
        TouchDisc atk = c.Layout.Button(TouchButton.Attack), jmp = c.Layout.Button(TouchButton.Jump), sa = c.Layout.Button(TouchButton.SkillA);
        Assert.True(c.Down(2, atk.X, atk.Y));
        Assert.True(c.Down(3, jmp.X, jmp.Y));
        Assert.True(c.Down(4, sa.X, sa.Y));
        Assert.True(c.IsPressed(TouchButton.Attack) && c.IsPressed(TouchButton.Jump) && c.IsPressed(TouchButton.SkillA) && !c.IsPressed(TouchButton.SkillS));

        TouchFrame f = c.Poll();
        Assert.Equal((1, 0, true, true, SkillKeys.A), (f.DirX, f.DirY, f.Attack, f.Jump, f.Skills));
        Assert.Equal(SkillKeys.A, f.SkillsHeld);
        f = c.Poll();                                              // 按住不连发;摇杆还在
        Assert.Equal((1, 0, false, false, SkillKeys.None), (f.DirX, f.DirY, f.Attack, f.Jump, f.Skills));
        Assert.Equal(SkillKeys.A, f.SkillsHeld);                   // 技能按钮没有边沿了,但"按着"一直是真的(蓄力类技能每帧查它,证据 E-138)

        c.Up(2);
        Assert.False(c.IsPressed(TouchButton.Attack));
        Assert.True(c.IsPressed(TouchButton.Jump));
        Assert.False(c.Poll().Attack);                             // 抬起不产生边沿
        c.Down(2, atk.X, atk.Y);                                   // 再按一次:再给一次
        Assert.True(c.Poll().Attack);

        TouchDisc ss = c.Layout.Button(TouchButton.SkillS), sd = c.Layout.Button(TouchButton.SkillD);
        c.Down(5, ss.X, ss.Y);
        c.Down(6, sd.X, sd.Y);
        Assert.Equal(SkillKeys.S | SkillKeys.D, c.Poll().Skills);
        c.Up(4);                                                   // 抬起 A:按着的集合只剩 S 与 D
        TouchFrame later = c.Poll();
        Assert.Equal((SkillKeys.None, SkillKeys.S | SkillKeys.D), (later.Skills, later.SkillsHeld));
    }

    [Fact]
    public void ATouchOnNoControlIsNotClaimedAndLaterEventsForItAreIgnored()
    {
        TouchControls c = New();
        Assert.False(c.Down(9, 640, 300));                         // 画面中央:不是任何控件
        c.Move(9, 700, 300);
        c.Up(9);
        Assert.Equal((0, 0), Dir(c));
        Assert.False(c.StickActive);

        Assert.True(c.Down(1, c.Layout.StickIdleX, c.Layout.StickIdleY));
        Assert.False(c.Down(2, c.Layout.StickIdleX + 20, c.Layout.StickIdleY));   // 摇杆已被占着:第二根手指落在摇杆区里也不认领
        c.Move(2, c.Layout.StickIdleX + 200, c.Layout.StickIdleY);
        Assert.Equal((0, 0), Dir(c));
    }

    [Fact]
    public void ButtonsAcceptAFingerSomewhatOutsideTheDrawnCircleButNotFarOutside()
    {
        TouchControls c = New();
        TouchDisc d = c.Layout.Button(TouchButton.Attack);
        Assert.True(c.Down(1, d.X + d.Radius * 1.2, d.Y));
        Assert.True(c.IsPressed(TouchButton.Attack));
        c.Up(1);
        Assert.False(c.Down(2, d.X + d.Radius * 1.4, d.Y - d.Radius * 0.3));
    }

    [Fact]
    public void PauseIsReportedSeparatelyAndDoesNotReachThePlayerInputs()
    {
        TouchControls c = New();
        TouchDisc p = c.Layout.Button(TouchButton.Pause);
        Assert.True(c.Down(1, p.X, p.Y));
        TouchFrame f = c.Poll();
        Assert.Equal((0, 0, false, false, SkillKeys.None), (f.DirX, f.DirY, f.Attack, f.Jump, f.Skills));
        Assert.True(c.TakePause());
        Assert.False(c.TakePause());                               // 只给一次
    }

    [Fact]
    public void ChangingTheScreenSizeRelaysOutAndReleasesEveryFinger()
    {
        TouchControls c = New(1280, 720);
        Stick(c, 0, 0.65);
        TouchDisc d = c.Layout.Button(TouchButton.Attack);
        c.Down(2, d.X, d.Y);
        c.Resize(1280, 720);                                       // 没变:什么都不动
        Assert.True(c.StickActive);
        c.Resize(1920, 1080);
        Assert.False(c.StickActive);
        Assert.False(c.IsPressed(TouchButton.Attack));
        Assert.False(c.Poll().Attack);
        Assert.Equal(1080, c.Layout.Height);
    }

    [Fact]
    public void PushingTheStickToTheOuterRingPlaysADoubleTapAndTheHeroRuns()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        // 从站着起:按、松、按,第三帧起主角在奔跑
        TownHero hero = NewHero(assets);
        TouchControls c = New();
        Stick(c, 0, TouchScript.RunReach);
        Assert.True(c.InRunZone);
        var seen = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            seen.Add(Step(c, hero));
        }
        Assert.Equal(new[] { 1, 0, 1, 1, 1, 1 }, seen);
        Assert.Equal(HeroMotion.Dash, hero.Motion);
        Assert.False(hero.FacingLeft);

        // 已经在走(走了半秒,早过了双击的时间窗):松开、按、松开、按,最后一步是常规输出
        hero = NewHero(assets);
        c = New();
        Stick(c, 0, TouchScript.WalkReach);
        for (int i = 0; i < 30; i++)
        {
            Step(c, hero);
        }
        Assert.Equal(HeroMotion.Move, hero.Motion);
        Stick(c, 0, TouchScript.RunReach);
        seen.Clear();
        for (int i = 0; i < 6; i++)
        {
            seen.Add(Step(c, hero));
        }
        Assert.Equal(new[] { 0, 1, 0, 1, 1, 1 }, seen);
        Assert.Equal(HeroMotion.Dash, hero.Motion);

        // 跑一阵再退回内圈:全松开一帧,之后是走路,不再是奔跑
        for (int i = 0; i < 30; i++)
        {
            Step(c, hero);
        }
        Assert.Equal(HeroMotion.Dash, hero.Motion);
        Stick(c, 0, TouchScript.WalkReach);
        Assert.False(c.InRunZone);
        TouchFrame release = c.Poll();
        hero.Update(Frame, release.DirX, release.DirY, false, false);
        Assert.Equal((0, 0), (release.DirX, release.DirY));
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(1, Step(c, hero));
        }
        Assert.Equal(HeroMotion.Move, hero.Motion);
    }

    [Fact]
    public void ReversingAtTheRimRunsTheOtherWayAndLiftingTheFingerMidSequenceLeavesNothingBehind()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        TouchControls c = New();
        Stick(c, 0, TouchScript.RunReach);
        for (int i = 0; i < 20; i++)
        {
            Step(c, hero);
        }
        Assert.Equal(HeroMotion.Dash, hero.Motion);

        Stick(c, 180, TouchScript.RunReach);                       // 直接从右边的外圈换到左边的外圈
        var seen = new List<int>();
        for (int i = 0; i < 5; i++)
        {
            seen.Add(Step(c, hero));
        }
        Assert.Equal(new[] { -1, 0, -1, -1, -1 }, seen);
        Assert.Equal(HeroMotion.Dash, hero.Motion);
        Assert.True(hero.FacingLeft);

        // 序列只放了一半手指就抬了:作废,之后一直是全松开
        c = New();
        Stick(c, 0, TouchScript.RunReach);
        Assert.Equal(1, c.Poll().DirX);
        c.Up(1);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((0, 0), Dir(c));
        }
    }

    [Fact]
    public void TheRunningTouchGivesTheSameSteadyStateSpeedAsTheKeyboardDoubleTap()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero touch = NewHero(assets);
        TouchControls c = New();
        Stick(c, 0, TouchScript.RunReach);
        for (int i = 0; i < 10; i++)
        {
            Step(c, touch);
        }
        double x0 = touch.X;
        for (int i = 0; i < 60; i++)
        {
            Step(c, touch);
        }
        double touchDistance = touch.X - x0;

        TownHero keys = NewHero(assets);
        keys.Update(50, 1, 0);
        keys.Update(50, 0, 0);
        keys.Update(50, 1, 0);
        for (int i = 0; i < 10; i++)
        {
            keys.Update(Frame, 1, 0);
        }
        Assert.Equal(HeroMotion.Dash, keys.Motion);
        double k0 = keys.X;
        for (int i = 0; i < 60; i++)
        {
            keys.Update(Frame, 1, 0);
        }
        Assert.Equal(keys.X - k0, touchDistance, 6);
    }

    [Fact]
    public void WalkingWithTheStickMovesTheHeroLikeHoldingTheArrowKey()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero touch = NewHero(assets);
        TownHero keys = NewHero(assets);
        TouchControls c = New();
        Stick(c, 45, TouchScript.WalkReach);                       // 右下斜走
        for (int i = 0; i < 90; i++)
        {
            Step(c, touch);
            keys.Update(Frame, 1, 1);
        }
        Assert.Equal((keys.X, keys.Y, keys.Motion), (touch.X, touch.Y, touch.Motion));
        Assert.Equal(HeroMotion.Move, touch.Motion);
    }

    [Fact]
    public void ATouchScriptGivesTheSameInputsFrameForFrameAsTheMatchingKeyScript()
    {
        CombatScript keys = CombatScript.Parse("0.5:right,1:right+attack,1.5:none,2:jump,2.5:left+a,3:down+s,3.5:none+d,4:up+attack+jump,4.5:none");
        TouchScript touch = TouchScript.Parse("0.5:stick=right,1:tap=attack,1.5:stick=none,2:tap=jump,2.5:stick=left,2.5:tap=a,3:stick=down,3:tap=s,3.5:stick=none,3.5:tap=d,4:stick=up,4:tap=attack,4:tap=jump,4.5:stick=none");
        TouchControls c = New();
        int edges = 0;
        for (int i = 0; i < 360; i++)
        {
            double t = i * Frame;
            bool keyAttack = keys.Poll(t, out int kx, out int ky, out bool keyJump, out SkillKeys keySkills);
            touch.Advance(c, t);
            TouchFrame f = c.Poll();
            Assert.Equal((kx, ky, keyAttack, keyJump, keySkills), (f.DirX, f.DirY, f.Attack, f.Jump, f.Skills));
            if (keyAttack || keyJump || keySkills != SkillKeys.None)
            {
                edges++;
            }
        }
        Assert.Equal(6, edges);                                    // 1:attack、2:jump、2.5:a、3:s、3.5:d、4:attack+jump 的边沿落在 6 个不同的帧上
    }

    [Fact]
    public void ATouchScriptWorksAtAnyResolution()
    {
        foreach ((int w, int h) in new[] { (800, 600), (1280, 720), (2532, 1170) })
        {
            TouchScript script = TouchScript.Parse("0.1:stick=left+up,0.5:stick=right+run,1:stick=none,1.1:tap=pause");
            TouchControls c = New(w, h);
            script.Advance(c, 150);
            Assert.Equal((-1, -1), Dir(c));
            script.Advance(c, 600);
            Assert.True(c.InRunZone);
            script.Advance(c, 1050);
            Assert.False(c.StickActive);
            script.Advance(c, 1150);
            Assert.True(c.TakePause());
        }
    }

    [Fact]
    public void ATouchScriptRejectsGarbageAndAnEmptyOneIsEmpty()
    {
        Assert.True(TouchScript.Parse("").IsEmpty);
        Assert.True(TouchScript.Parse(null).IsEmpty);
        Assert.False(TouchScript.Parse("1:tap=attack").IsEmpty);
        foreach (string bad in new[] { "stick=left", "x:stick=left", "-1:stick=left", "1:stick", "1:stick=", "1:stick=sideways", "1:stick=run", "1:tap=banana", "1:wave=left", "1:tap" })
        {
            Assert.Throws<FormatException>(() => TouchScript.Parse(bad));
        }
    }

    [Fact]
    public void ATouchScriptAppliesItemsInTimeOrderEvenWhenWrittenOutOfOrder()
    {
        TouchScript script = TouchScript.Parse("2:stick=none,0.5:stick=right");
        TouchControls c = New();
        script.Advance(c, 1000);
        Assert.Equal((1, 0), Dir(c));
        script.Advance(c, 2500);
        Assert.Equal((0, 0), Dir(c));
    }
}
