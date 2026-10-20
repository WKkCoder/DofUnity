using Dnf.Data;

namespace Dnf.Data.Tests;

/// <summary>audio.xml 解析与声音标签展开(证据 E-089)。全部用合成的 XML,不含原版数据。</summary>
public class AudioCatalogTests
{
    private const string Sample = @"<?xml version=""1.0""?>
<!-- 注释 -->
<AudioTagDatabase>
  <EFFECT ID=""HIT_A"" FILE=""sounds\test\hit_a.ogg"" VOLUME_ADJUST=""0"" PRIORITY=""0""/>
  <EFFECT ID=""HIT_B"" FILE=""sounds\test\hit_b.ogg""/>
  <EFFECT ID=""HIT_C"" FILE=""sounds\test\hit_c.ogg""/>
  <EFFECT ID=""WIND_LOOP"" FILE=""sounds\test\wind.ogg"" LOOP_DELAY=""20"" LOOP_DELAY_RANGE=""6""/>
  <EFFECT ID=""DRIP"" FILE=""sounds\test\drip.ogg"" LOOP_DELAY=""0""/>
  <AMBIENT ID=""AMB_CAVE"" FILE=""sounds\amb\cave.ogg"" LOOP_DELAY=""0""/>
  <MUSIC ID=""M_TOWN"" FILE=""music\town.ogg"" LOOP_DELAY=""0""/>
  <RANDOM ID=""R_HIT"">
    <ITEM TAG=""HIT_A"" PROB=""33""/>
    <ITEM TAG=""HIT_B"" PROB=""33""/>
    <ITEM TAG=""HIT_C"" PROB=""34""/>
  </RANDOM>
  <RANDOM ID=""R_OUTER"" DELAY=""0.5"" DELAY_RANGE=""1"">
    <ITEM TAG=""R_HIT"" PROB=""1""/>
  </RANDOM>
  <RANDOM ID=""R_LOOPING"" LOOP_DELAY=""10"">
    <ITEM TAG=""HIT_A"" PROB=""1""/>
  </RANDOM>
  <GROUP ID=""G_TWO"">
    <ITEM TAG=""HIT_A"" DELAY=""0.1"" DELAY_RANGE=""0""/>
    <ITEM TAG=""HIT_B"" DELAY=""3"" DELAY_RANGE=""2""/>
  </GROUP>
  <RANDOM ID=""R_CYCLE_A""><ITEM TAG=""R_CYCLE_B"" PROB=""1""/></RANDOM>
  <RANDOM ID=""R_CYCLE_B""><ITEM TAG=""R_CYCLE_A"" PROB=""1""/></RANDOM>
  <RANDOM ID=""R_MISSING""><ITEM TAG=""NOT_THERE"" PROB=""1""/></RANDOM>
  <RANDOM ID=""R_ZERO""><ITEM TAG=""HIT_B"" PROB=""0""/><ITEM TAG=""HIT_C"" PROB=""0""/></RANDOM>
  <EFFECT ID=""hit_a"" FILE=""sounds\test\other.ogg""/>
  <SOMETHING ID=""X""/>
  <EFFECT FILE=""sounds\test\noid.ogg""/>
</AudioTagDatabase>";

    private static AudioCatalog Load() => AudioCatalog.ParseText(Sample);

    private static Func<double> Fixed(double v) => () => v;

    [Fact]
    public void Parse_ReadsEntriesByKind()
    {
        AudioCatalog c = Load();
        Assert.True(c.TryGet("HIT_A", out AudioEntry hit));
        Assert.Equal(AudioKind.Effect, hit.Kind);
        Assert.Equal("sounds/test/hit_a.ogg", hit.File);   // 反斜杠换成斜杠
        Assert.False(hit.HasLoopDelay);
        Assert.True(c.TryGet("AMB_CAVE", out AudioEntry amb));
        Assert.Equal(AudioKind.Ambient, amb.Kind);
        Assert.True(c.TryGet("M_TOWN", out AudioEntry music));
        Assert.Equal(AudioKind.Music, music.Kind);
        Assert.Equal("music/town.ogg", music.File);
        Assert.True(music.HasLoopDelay);
        Assert.Equal(0, music.LoopDelaySeconds);
        Assert.True(c.TryGet("R_HIT", out AudioEntry random));
        Assert.Equal(AudioKind.Random, random.Kind);
        Assert.Equal(3, random.Items.Count);
        Assert.Equal(34, random.Items[2].Weight);
        Assert.True(c.TryGet("G_TWO", out AudioEntry group));
        Assert.Equal(AudioKind.Group, group.Kind);
        Assert.Equal(3, group.Items[1].DelaySeconds);
        Assert.Equal(2, group.Items[1].DelayRangeSeconds);
    }

    [Fact]
    public void Parse_LookupIgnoresCaseAndFirstEntryWins()
    {
        AudioCatalog c = Load();
        Assert.True(c.TryGet("hit_a", out AudioEntry e));
        Assert.Equal("sounds/test/hit_a.ogg", e.File);     // 后面同名(大小写不同)的那条被丢掉
        Assert.Equal(1, c.DuplicateCount);
        Assert.Equal(1, c.UnknownElementCount);            // SOMETHING
        Assert.False(c.Contains("X"));
        Assert.False(c.Contains(""));
        Assert.False(c.Contains(null));
    }

    [Fact]
    public void Resolve_PlainEffect_ReturnsItself()
    {
        List<ResolvedSound> r = Load().Resolve("HIT_B", Fixed(0.5));
        Assert.Single(r);
        Assert.Equal("HIT_B", r[0].Id);
        Assert.Equal(AudioKind.Effect, r[0].Kind);
        Assert.Equal("sounds/test/hit_b.ogg", r[0].File);
        Assert.Equal(0, r[0].DelaySeconds);
        Assert.False(r[0].Loops);
    }

    [Fact]
    public void Resolve_RandomPicksByWeight()
    {
        AudioCatalog c = Load();
        // 权重 33/33/34:随机数 0 落在第一项,0.5 落在第二项(33~66),0.99 落在第三项
        Assert.Equal("HIT_A", c.Resolve("R_HIT", Fixed(0.0))[0].Id);
        Assert.Equal("HIT_A", c.Resolve("R_HIT", Fixed(0.32))[0].Id);
        Assert.Equal("HIT_B", c.Resolve("R_HIT", Fixed(0.5))[0].Id);
        Assert.Equal("HIT_C", c.Resolve("R_HIT", Fixed(0.99))[0].Id);
    }

    [Fact]
    public void CollectFiles_ListsEveryReachableFileOnceAndSurvivesCyclesAndMissingTags()
    {
        AudioCatalog c = Load();
        Assert.Equal(new[] { "sounds/test/hit_a.ogg" }, c.CollectFiles("HIT_A").ToArray());
        Assert.Equal(new[] { "sounds/test/hit_a.ogg", "sounds/test/hit_b.ogg", "sounds/test/hit_c.ogg" }, c.CollectFiles("R_HIT").ToArray());
        Assert.Equal(c.CollectFiles("R_HIT").ToArray(), c.CollectFiles("R_OUTER").ToArray());          // 嵌套的 RANDOM 一路展开
        Assert.Equal(new[] { "sounds/test/hit_a.ogg", "sounds/test/hit_b.ogg" }, c.CollectFiles("G_TWO").ToArray());
        Assert.Equal(new[] { "sounds/test/hit_b.ogg", "sounds/test/hit_c.ogg" }, c.CollectFiles("R_ZERO").ToArray());   // 权重 0 的项也算"可能到达"
        Assert.Empty(c.CollectFiles("R_CYCLE_A"));
        Assert.Empty(c.CollectFiles("R_MISSING"));
        Assert.Empty(c.CollectFiles("NOT_THERE"));
        Assert.Empty(c.CollectFiles(null));
    }

    [Fact]
    public void Resolve_RandomWithZeroWeights_PicksFirst()
    {
        Assert.Equal("HIT_B", Load().Resolve("R_ZERO", Fixed(0.9))[0].Id);
    }

    [Fact]
    public void Resolve_RandomInsideRandom_AddsOuterDelay()
    {
        // 外层 DELAY 0.5、范围 1:随机数 0.5 时延迟 0.5 + 1×0.5 = 1(随机数每用一次要新取;这里固定值)
        List<ResolvedSound> r = Load().Resolve("R_OUTER", Fixed(0.5));
        Assert.Single(r);
        Assert.Equal("HIT_B", r[0].Id);
        Assert.Equal(1.0, r[0].DelaySeconds, 6);
    }

    [Fact]
    public void Resolve_Group_PlaysEveryItemWithDelays()
    {
        List<ResolvedSound> r = Load().Resolve("G_TWO", Fixed(0.5));
        Assert.Equal(2, r.Count);
        Assert.Equal("HIT_A", r[0].Id);
        Assert.Equal(0.1, r[0].DelaySeconds, 6);
        Assert.Equal("HIT_B", r[1].Id);
        Assert.Equal(3 + 2 * 0.5, r[1].DelaySeconds, 6);
    }

    [Fact]
    public void Resolve_LoopAttributes()
    {
        AudioCatalog c = Load();
        ResolvedSound wind = c.Resolve("WIND_LOOP", Fixed(0.5))[0];
        Assert.True(wind.Loops);
        Assert.Equal(20 + 6 * 0.5, wind.LoopGapSeconds, 6);
        ResolvedSound drip = c.Resolve("DRIP", Fixed(0.9))[0];
        Assert.True(drip.Loops);
        Assert.Equal(0, drip.LoopGapSeconds);
        Assert.True(c.Resolve("M_TOWN")[0].Loops);
        // 外层 RANDOM 写了 LOOP_DELAY:解出来的叶子按外层循环
        ResolvedSound looping = c.Resolve("R_LOOPING", Fixed(0.0))[0];
        Assert.True(looping.Loops);
        Assert.Equal(10, looping.LoopGapSeconds);
    }

    [Fact]
    public void Resolve_UnknownMissingAndCyclicTags_ReturnEmpty()
    {
        AudioCatalog c = Load();
        Assert.Empty(c.Resolve("NOPE"));
        Assert.Empty(c.Resolve(null));
        Assert.Empty(c.Resolve("R_MISSING"));
        Assert.Empty(c.Resolve("R_CYCLE_A"));   // 环:达到最大层数后停下
    }

    [Fact]
    public void Resolve_WithoutRandom_IsReproducible()
    {
        AudioCatalog c = Load();
        string first = c.Resolve("R_HIT")[0].Id;
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(first, c.Resolve("R_HIT")[0].Id);
        }
    }

    [Fact]
    public void TryResolveFirst_ReportsMissing()
    {
        AudioCatalog c = Load();
        Assert.True(c.TryResolveFirst("HIT_C", null, out ResolvedSound s));
        Assert.Equal("HIT_C", s.Id);
        Assert.False(c.TryResolveFirst("NOPE", null, out _));
    }

    [Fact]
    public void Parse_Bytes_AndRejectsMissingRoot()
    {
        AudioCatalog c = AudioCatalog.Parse(System.Text.Encoding.UTF8.GetBytes(Sample));
        Assert.Equal(Load().Count, c.Count);
        Assert.Throws<DnfFormatException>(() => AudioCatalog.ParseText("<?xml version=\"1.0\"?><Other/>"));
        Assert.Empty(AudioCatalog.ParseText("<AudioTagDatabase/>").Entries);
    }

    [Fact]
    public void Parse_RejectsDtd()
    {
        string evil = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"b\">]><AudioTagDatabase/>";
        Assert.ThrowsAny<System.Xml.XmlException>(() => AudioCatalog.ParseText(evil));
    }
}
