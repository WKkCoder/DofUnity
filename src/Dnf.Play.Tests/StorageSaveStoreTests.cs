using Dnf.Game;
using static Dnf.Play.Tests.StorageKit;

namespace Dnf.Play.Tests;

/// <summary>
/// 仓库存档的存放与存档员(本项目自己的设计,没有原版证据):文件存放(先写临时文件再换进去、读不懂的文件留一份 .bad)、内存存放、
/// 存档员按时间间隔检查、内容没变不写、写失败留下原因并在下次再试。时间由测试给,不用等。
/// </summary>
public sealed class StorageSaveStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dnf-storage-test-" + Guid.NewGuid().ToString("N"));

    public StorageSaveStoreTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static StorageSave Sample(long gold = 10)
    {
        var save = new StorageSave { AccountCapacity = 8, AccountGold = gold };
        save.AccountSlots.Add(new SavedVaultSlot(2, 900, 1));
        var vault = new SavedPersonalVault("hero1", 24);
        vault.Slots.Add(new SavedVaultSlot(0, 911, 30));
        save.Personal.Add(vault);
        return save;
    }

    // ------------------------------------------------------------------ 文件

    [Fact]
    public void TheFileStoreCreatesDirectoriesWritesAndReadsBack()
    {
        string path = Path.Combine(_dir, "sub", "deeper", "storage.txt");
        var store = new FileStorageSaveStore(path);
        Assert.Equal(path, store.Path);
        Assert.True(store.Save(Sample(), out string error), error);
        Assert.Null(error);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(Sample().ToText(), File.ReadAllText(path));

        StorageSave back = store.Load(out string note);
        Assert.Null(note);
        Assert.Equal(Sample().ToText(), back.ToText());
    }

    [Fact]
    public void TheFileIsWrittenAsUtf8WithoutAByteOrderMark()
    {
        string path = Path.Combine(_dir, "storage.txt");
        Assert.True(new FileStorageSaveStore(path).Save(Sample(), out _));
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'D', bytes[0]);
        Assert.Equal((byte)'\n', bytes[bytes.Length - 1]);
    }

    [Fact]
    public void SavingAgainReplacesTheFileAndLeavesNoTemporaryOne()
    {
        string path = Path.Combine(_dir, "storage.txt");
        var store = new FileStorageSaveStore(path);
        Assert.True(store.Save(Sample(10), out _));
        Assert.True(store.Save(Sample(20), out string error), error);
        Assert.Equal(Sample(20).ToText(), File.ReadAllText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
        Assert.Equal(20, store.Load(out _).AccountGold);
    }

    [Fact]
    public void NoFileMeansNoSaveAndNoNote()
    {
        var store = new FileStorageSaveStore(Path.Combine(_dir, "missing.txt"));
        Assert.Null(store.Load(out string note));
        Assert.Null(note);
    }

    [Fact]
    public void AnUnreadableFileIsCopiedToBadOnceAndTreatedAsNoSave()
    {
        string path = Path.Combine(_dir, "storage.txt");
        File.WriteAllText(path, "garbage one");
        var store = new FileStorageSaveStore(path);

        Assert.Null(store.Load(out string note));
        Assert.Contains("storage.txt.bad", note);
        Assert.Contains("读不懂", note);
        Assert.Equal("garbage one", File.ReadAllText(path + ".bad"));

        File.WriteAllText(path, "garbage two");
        Assert.Null(store.Load(out string again));
        Assert.NotNull(again);
        Assert.DoesNotContain("复制失败", again);                            // 已经有 .bad 时根本不去复制,所以也不会有复制失败的说明
        Assert.Equal("garbage one", File.ReadAllText(path + ".bad"));       // 已经有 .bad 就不覆盖
        Assert.Equal("garbage two", File.ReadAllText(path));                 // 原文件不动
    }

    [Fact]
    public void ANewerVersionFileIsAlsoKeptAsBad()
    {
        string path = Path.Combine(_dir, "storage.txt");
        File.WriteAllText(path, "DNFUNITY-STORAGE 99\nacap 8\n");
        Assert.Null(new FileStorageSaveStore(path).Load(out string note));
        Assert.Contains("新", note);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void SavingNothingOrToAnImpossiblePathFailsWithoutThrowing()
    {
        var store = new FileStorageSaveStore(Path.Combine(_dir, "storage.txt"));
        Assert.False(store.Save(null, out string none));
        Assert.Equal("没有要存的内容", none);

        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "i am a file");
        var broken = new FileStorageSaveStore(Path.Combine(blocker, "inside", "storage.txt"));
        Assert.False(broken.Save(Sample(), out string error));
        Assert.StartsWith("写仓库存档失败", error);
        Assert.Equal("i am a file", File.ReadAllText(blocker));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TheFileStoreNeedsAPath(string path)
    {
        Assert.Throws<ArgumentException>(() => new FileStorageSaveStore(path));
    }

    // ------------------------------------------------------------------ 内存

    [Fact]
    public void TheMemoryStoreKeepsTheTextAndCountsWrites()
    {
        var store = new MemoryStorageSaveStore();
        Assert.Null(store.Load(out string note));
        Assert.Null(note);
        Assert.Equal(0, store.Writes);

        Assert.True(store.Save(Sample(), out string error));
        Assert.Null(error);
        Assert.Equal(1, store.Writes);
        Assert.Equal(Sample().ToText(), store.Text);
        Assert.Equal(Sample().ToText(), store.Load(out note).ToText());
        Assert.Null(note);
    }

    [Fact]
    public void TheMemoryStoreCanBeMadeToFail()
    {
        var store = new MemoryStorageSaveStore { FailWrites = true };
        Assert.False(store.Save(Sample(), out string error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(0, store.Writes);
        Assert.Null(store.Text);
        store.FailWrites = false;
        Assert.True(store.Save(Sample(), out _));
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void TheMemoryStoreReportsTextItCannotRead()
    {
        var store = new MemoryStorageSaveStore { Text = "not a save" };
        Assert.Null(store.Load(out string note));
        Assert.Contains("读不懂", note);
    }

    // ------------------------------------------------------------------ 存档员

    private static void Change(StorageSession session, int count = 1)
    {
        Assert.True(session.Hero.AddItem(Potion(911), count));
        Assert.Equal(StorageResult.Ok, session.Store(StorageKind.Personal, InventoryTab.Consumable, 0, count, -1, out _));
    }

    [Fact]
    public void TheSaverWritesOnFlushAndSkipsAnUnchangedState()
    {
        StorageSession session = Session();
        var store = new MemoryStorageSaveStore();
        var saver = new StorageSaver(store, session);

        Assert.True(saver.Flush(0));
        Assert.Equal(1, store.Writes);
        Assert.Equal(1, saver.Saves);
        Assert.False(saver.Flush(1));                                 // 没变不写
        Assert.Equal(1, store.Writes);

        Change(session);
        Assert.True(saver.Flush(2));
        Assert.Equal(2, store.Writes);
        Assert.Equal(2, saver.Saves);
        Assert.Equal(session.Snapshot().ToText(), store.Text);
        Assert.Null(saver.LastError);
    }

    [Fact]
    public void TheSaverDoesNotRewriteWhatWasJustLoaded()
    {
        StorageSession session = Session();
        Change(session);
        var store = new MemoryStorageSaveStore();
        var saver = new StorageSaver(store, session, session.Snapshot());
        Assert.False(saver.Flush(0));
        Assert.Equal(0, store.Writes);
        Change(session);
        Assert.True(saver.Flush(1));
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void AFailedWriteKeepsTheReasonAndIsRetriedNextTime()
    {
        StorageSession session = Session();
        var store = new MemoryStorageSaveStore { FailWrites = true };
        var saver = new StorageSaver(store, session);
        Change(session);

        Assert.False(saver.Flush(0));
        Assert.False(string.IsNullOrEmpty(saver.LastError));
        Assert.Equal(0, saver.Saves);
        Assert.Equal(0, store.Writes);

        store.FailWrites = false;
        Assert.True(saver.Flush(1));                                  // 内容没变也要再试:上次没写成
        Assert.Null(saver.LastError);
        Assert.Equal(1, saver.Saves);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void TickChecksOnlyOncePerInterval()
    {
        StorageSession session = Session();
        var store = new MemoryStorageSaveStore();
        var saver = new StorageSaver(store, session);
        Assert.Equal(10.0, saver.IntervalSeconds);

        Assert.True(saver.Tick(100));                                 // 第一次总是检查
        Assert.False(saver.Tick(105));
        Change(session);
        Assert.False(saver.Tick(109.9));
        Assert.True(saver.Tick(110));                                 // 恰好一个间隔
        Assert.Equal(2, store.Writes);

        Assert.False(saver.Tick(120));                                // 到点检查了,没变不写,计时也重新开始
        Change(session);
        Assert.False(saver.Tick(125));
        Assert.True(saver.Tick(130));
        Assert.Equal(3, store.Writes);
    }

    [Fact]
    public void TheIntervalCanBeChanged()
    {
        StorageSession session = Session();
        var store = new MemoryStorageSaveStore();
        var saver = new StorageSaver(store, session) { IntervalSeconds = 2 };
        Assert.True(saver.Tick(0));
        Change(session);
        Assert.False(saver.Tick(1.5));
        Assert.True(saver.Tick(2));
    }

    [Fact]
    public void TheSaverRefusesMissingParts()
    {
        StorageSession session = Session();
        Assert.Throws<ArgumentNullException>(() => new StorageSaver(null, session));
        Assert.Throws<ArgumentNullException>(() => new StorageSaver(new MemoryStorageSaveStore(), null));
    }
}
