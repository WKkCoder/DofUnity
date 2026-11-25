namespace Dnf.Client.Tests
{
    public sealed class TownDemoQuitPolicyTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("12abc")]
        [InlineData("0")]
        [InlineData("-0")]
        [InlineData("-1")]
        [InlineData("-0.001")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        [InlineData("1e309")]
        public void TryParseQuitAfterSim_RejectsInvalidOrNonPositive(string raw)
        {
            bool ok = TownDemoQuitPolicy.TryParseQuitAfterSim(raw, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
        }

        [Theory]
        [InlineData("1", 1.0)]
        [InlineData("0.5", 0.5)]
        [InlineData(" 25.25 ", 25.25)]
        [InlineData("1e9", 1_000_000_000.0)]
        [InlineData("1e-6", 1e-6)]
        public void TryParseQuitAfterSim_AcceptsPositiveFiniteNumbers(string raw, double expected)
        {
            bool ok = TownDemoQuitPolicy.TryParseQuitAfterSim(raw, out double seconds);

            Assert.True(ok);
            Assert.Equal(expected, seconds);
        }

        [Fact]
        public void EvaluateQuit_DisabledLimits_NeverQuitsEvenWhenSimOrWallHuge()
        {
            TownDemoQuitDecision unset = TownDemoQuitPolicy.EvaluateQuit(
                botDone: true,
                flowDoneNoted: true,
                simElapsedMs: 1_000_000.0,
                quitAfterSimSeconds: -1.0,
                wallElapsedSeconds: 500.0,
                quitAfterWallSeconds: -1.0);
            Assert.False(unset.ShouldQuit);
            Assert.Null(unset.Trigger);

            TownDemoQuitDecision zeroSimAndInf = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: double.PositiveInfinity,
                quitAfterSimSeconds: 0.0,
                wallElapsedSeconds: double.PositiveInfinity,
                quitAfterWallSeconds: double.PositiveInfinity);
            Assert.False(zeroSimAndInf.ShouldQuit);
            Assert.Null(zeroSimAndInf.Trigger);

            TownDemoQuitDecision infSim = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: double.PositiveInfinity,
                quitAfterSimSeconds: double.PositiveInfinity,
                wallElapsedSeconds: double.PositiveInfinity,
                quitAfterWallSeconds: -0.001);
            Assert.False(infSim.ShouldQuit);
            Assert.Null(infSim.Trigger);

            TownDemoQuitDecision nanLimits = TownDemoQuitPolicy.EvaluateQuit(
                botDone: true,
                flowDoneNoted: true,
                simElapsedMs: 5000.0,
                quitAfterSimSeconds: double.NaN,
                wallElapsedSeconds: 5.0,
                quitAfterWallSeconds: double.NaN);
            Assert.False(nanLimits.ShouldQuit);
            Assert.Null(nanLimits.Trigger);
        }

        [Fact]
        public void EvaluateQuit_SimThreshold_UsesExactMillisecondBoundary()
        {
            TownDemoQuitDecision justBefore = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: 9999.999,
                quitAfterSimSeconds: 10.0,
                wallElapsedSeconds: 3.0,
                quitAfterWallSeconds: 30.0);
            Assert.False(justBefore.ShouldQuit);
            Assert.Null(justBefore.Trigger);

            TownDemoQuitDecision exact = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: 10000.0,
                quitAfterSimSeconds: 10.0,
                wallElapsedSeconds: 3.0,
                quitAfterWallSeconds: 30.0);
            Assert.True(exact.ShouldQuit);
            Assert.Equal("sim", exact.Trigger);

            TownDemoQuitDecision largeBefore = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: 999_999_999_999.0,
                quitAfterSimSeconds: 1e9,
                wallElapsedSeconds: 1.0,
                quitAfterWallSeconds: -1.0);
            Assert.False(largeBefore.ShouldQuit);

            TownDemoQuitDecision largeExact = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: 1_000_000_000_000.0,
                quitAfterSimSeconds: 1e9,
                wallElapsedSeconds: 1.0,
                quitAfterWallSeconds: -1.0);
            Assert.True(largeExact.ShouldQuit);
            Assert.Equal("sim", largeExact.Trigger);
        }

        [Fact]
        public void EvaluateQuit_WallThreshold_UsesZeroAndExactBoundary()
        {
            TownDemoQuitDecision zeroWallImmediate = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: 0.0,
                quitAfterSimSeconds: -1.0,
                wallElapsedSeconds: 0.0,
                quitAfterWallSeconds: 0.0);
            Assert.True(zeroWallImmediate.ShouldQuit);
            Assert.Equal("wall", zeroWallImmediate.Trigger);

            TownDemoQuitDecision zeroWallNegativeElapsed = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: 0.0,
                quitAfterSimSeconds: -1.0,
                wallElapsedSeconds: -0.01,
                quitAfterWallSeconds: 0.0);
            Assert.False(zeroWallNegativeElapsed.ShouldQuit);

            TownDemoQuitDecision justBeforeWall = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: 4000.0,
                quitAfterSimSeconds: 20.0,
                wallElapsedSeconds: 29.999,
                quitAfterWallSeconds: 30.0);
            Assert.False(justBeforeWall.ShouldQuit);

            TownDemoQuitDecision exactWall = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: 4000.0,
                quitAfterSimSeconds: 20.0,
                wallElapsedSeconds: 30.0,
                quitAfterWallSeconds: 30.0);
            Assert.True(exactWall.ShouldQuit);
            Assert.Equal("wall", exactWall.Trigger);
        }

        [Fact]
        public void EvaluateQuit_FirstToFire_BotVsSimVsWall()
        {
            // botDone 刚成立但还未等到 1 秒收尾窗口：不提前退出
            TownDemoQuitDecision botWaitingSettle = TownDemoQuitPolicy.EvaluateQuit(
                botDone: true,
                flowDoneNoted: false,
                simElapsedMs: 5000.0,
                quitAfterSimSeconds: 20.0,
                wallElapsedSeconds: 4.0,
                quitAfterWallSeconds: 5.0);
            Assert.False(botWaitingSettle.ShouldQuit);
            Assert.Null(botWaitingSettle.Trigger);

            // botDone 且 1 秒收尾窗口已到（wallHit=true）：报 bot
            TownDemoQuitDecision botSettled = TownDemoQuitPolicy.EvaluateQuit(
                botDone: true,
                flowDoneNoted: true,
                simElapsedMs: 5000.0,
                quitAfterSimSeconds: 20.0,
                wallElapsedSeconds: 5.0,
                quitAfterWallSeconds: 5.0);
            Assert.True(botSettled.ShouldQuit);
            Assert.Equal("bot", botSettled.Trigger);

            // 即使 sim 在收尾窗口到期这一帧也恰好达标，已完成并走完收尾的 bot 仍优先记为 bot
            TownDemoQuitDecision botAndSimAtSettle = TownDemoQuitPolicy.EvaluateQuit(
                botDone: true,
                flowDoneNoted: true,
                simElapsedMs: 20000.0,
                quitAfterSimSeconds: 20.0,
                wallElapsedSeconds: 5.0,
                quitAfterWallSeconds: 5.0);
            Assert.True(botAndSimAtSettle.ShouldQuit);
            Assert.Equal("bot", botAndSimAtSettle.Trigger);

            // 只有 flowDoneNoted=true 而 botDone=false 时不能算 bot，应按 wall
            TownDemoQuitDecision notedWithoutBotDone = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: true,
                simElapsedMs: 5000.0,
                quitAfterSimSeconds: 20.0,
                wallElapsedSeconds: 5.0,
                quitAfterWallSeconds: 5.0);
            Assert.True(notedWithoutBotDone.ShouldQuit);
            Assert.Equal("wall", notedWithoutBotDone.Trigger);

            // 只有 botDone=true 而 flowDoneNoted=false 且 wallHit=true：算 wall
            TownDemoQuitDecision wallBeforeBotNoted = TownDemoQuitPolicy.EvaluateQuit(
                botDone: true,
                flowDoneNoted: false,
                simElapsedMs: 5000.0,
                quitAfterSimSeconds: 20.0,
                wallElapsedSeconds: 5.0,
                quitAfterWallSeconds: 5.0);
            Assert.True(wallBeforeBotNoted.ShouldQuit);
            Assert.Equal("wall", wallBeforeBotNoted.Trigger);

            // sim 与 wall 在同一帧达标（非 bot）：sim 优先于兜底 wall
            TownDemoQuitDecision simAndWallTie = TownDemoQuitPolicy.EvaluateQuit(
                botDone: false,
                flowDoneNoted: false,
                simElapsedMs: 20000.0,
                quitAfterSimSeconds: 20.0,
                wallElapsedSeconds: 60.0,
                quitAfterWallSeconds: 60.0);
            Assert.True(simAndWallTie.ShouldQuit);
            Assert.Equal("sim", simAndWallTie.Trigger);

            // bot 收尾期间 sim 先于 1s 墙钟窗口到点：sim 先到先退
            TownDemoQuitDecision simDuringBotSettle = TownDemoQuitPolicy.EvaluateQuit(
                botDone: true,
                flowDoneNoted: true,
                simElapsedMs: 20000.0,
                quitAfterSimSeconds: 20.0,
                wallElapsedSeconds: 4.5,
                quitAfterWallSeconds: 5.0);
            Assert.True(simDuringBotSettle.ShouldQuit);
            Assert.Equal("sim", simDuringBotSettle.Trigger);
        }

        [Fact]
        public void AppendQuitLogFields_PreservesExistingFieldsVerbatimAndAppendsTrigger()
        {
            const string baseLine = "DNF_TOWN_DEMO ran frames=1544 seconds=80.85 clockMs=80850 sprites=23 hero=(876.5,246.5) facing=left motion=Rest cameraX=96 leftEdge=False flow=Town serial=16 runs=3 settlements=3 botDone=True choicesMade=3 failures=0 giveups=0";

            Assert.Equal(
                baseLine + " simMs=12345 quitBy=bot",
                TownDemoQuitPolicy.AppendQuitLogFields(baseLine, 12345.67, "bot"));

            Assert.Equal(
                baseLine + " simMs=95000 quitBy=sim",
                TownDemoQuitPolicy.AppendQuitLogFields(baseLine, 95000.0, "sim"));

            Assert.Equal(
                baseLine + " simMs=0 quitBy=wall",
                TownDemoQuitPolicy.AppendQuitLogFields(baseLine, 0.0, "wall"));

            Assert.Equal(
                " simMs=0 quitBy=unknown",
                TownDemoQuitPolicy.AppendQuitLogFields(null, -5.0, ""));

            Assert.Equal(
                "x simMs=0 quitBy=unknown",
                TownDemoQuitPolicy.AppendQuitLogFields("x", double.NaN, null));

            Assert.Equal(
                "x simMs=0 quitBy=unknown",
                TownDemoQuitPolicy.AppendQuitLogFields("x", double.PositiveInfinity, null));
        }
    }
}
