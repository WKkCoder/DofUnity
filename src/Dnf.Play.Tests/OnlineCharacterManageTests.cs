using System.Text;

namespace Dnf.Play.Tests;

public sealed class OnlineCharacterManageTests
{
    // ------------------------------------------------------------------ 名字规则边界（UTF-8 字节数与字符限制）

    [Fact]
    public void Create_name_validates_utf8_byte_length_boundaries()
    {
        Assert.Equal(OnlineCharacterNameError.Null, OnlineCharacterNameRules.ValidateCreateName((byte[])null));
        Assert.Equal(OnlineCharacterNameError.Null, OnlineCharacterNameRules.ValidateCreateName((string)null));

        // 0、3 字节过短；4 字节合法；18 字节合法；19 字节过长
        Assert.Equal(OnlineCharacterNameError.TooShort, OnlineCharacterNameRules.ValidateCreateName(Array.Empty<byte>()));
        Assert.Equal(OnlineCharacterNameError.TooShort, OnlineCharacterNameRules.ValidateCreateName("abc"));
        Assert.Equal(OnlineCharacterNameError.Valid, OnlineCharacterNameRules.ValidateCreateName("abcd"));
        Assert.Equal(OnlineCharacterNameError.Valid, OnlineCharacterNameRules.ValidateCreateName(new string('a', 18)));
        Assert.Equal(OnlineCharacterNameError.TooLong, OnlineCharacterNameRules.ValidateCreateName(new string('a', 19)));

        // 中文 UTF-8 每个汉字 3 字节：1 字(3B)过短、2 字(6B)合法、6 字(18B)合法、7 字(21B)过长
        Assert.Equal(OnlineCharacterNameError.TooShort, OnlineCharacterNameRules.ValidateCreateName("甲"));
        Assert.Equal(OnlineCharacterNameError.Valid, OnlineCharacterNameRules.ValidateCreateName("甲乙"));
        Assert.Equal(OnlineCharacterNameError.Valid, OnlineCharacterNameRules.ValidateCreateName("甲乙丙丁戊己"));
        Assert.Equal(OnlineCharacterNameError.TooLong, OnlineCharacterNameRules.ValidateCreateName("甲乙丙丁戊己庚"));
    }

    [Fact]
    public void Create_name_rejects_nul_non_bmp_invalid_utf8_and_all_8_forbidden_ascii()
    {
        Assert.Equal(OnlineCharacterNameError.ContainsNullByte,
            OnlineCharacterNameRules.ValidateCreateName(new byte[] { 0x61, 0x62, 0x00, 0x63 }));
        Assert.Equal(OnlineCharacterNameError.InvalidUtf8,
            OnlineCharacterNameRules.ValidateCreateName(new byte[] { 0xE4, 0xB8, 0x20, 0x61 }));
        Assert.Equal(OnlineCharacterNameError.InvalidUtf8,
            OnlineCharacterNameRules.ValidateCreateName("ab\uD800c"));

        // 非 BMP（U+1F600，4 字节 UTF-8）
        Assert.Equal(OnlineCharacterNameError.NonBmpCharacter,
            OnlineCharacterNameRules.ValidateCreateName("\U0001F600a"));

        // 8 个禁用 ASCII 字符
        foreach (char bad in new[] { '\'', ' ', '\t', '\\', '%', '<', '>', '"' })
        {
            Assert.Equal(OnlineCharacterNameError.ForbiddenAsciiCharacter,
                OnlineCharacterNameRules.ValidateCreateName("ab" + bad + "c"));
        }
    }

    [Fact]
    public void Delete_confirmation_name_validates_1_to_29_bytes_and_allows_bmp_or_ascii()
    {
        Assert.Equal(OnlineCharacterNameError.Null, OnlineCharacterNameRules.ValidateDeleteConfirmationName((byte[])null));
        Assert.Equal(OnlineCharacterNameError.Null, OnlineCharacterNameRules.ValidateDeleteConfirmationName((string)null));

        Assert.Equal(OnlineCharacterNameError.TooShort, OnlineCharacterNameRules.ValidateDeleteConfirmationName(string.Empty));
        Assert.Equal(OnlineCharacterNameError.Valid, OnlineCharacterNameRules.ValidateDeleteConfirmationName("a"));
        Assert.Equal(OnlineCharacterNameError.Valid, OnlineCharacterNameRules.ValidateDeleteConfirmationName(new string('a', 29)));
        Assert.Equal(OnlineCharacterNameError.TooLong, OnlineCharacterNameRules.ValidateDeleteConfirmationName(new string('a', 30)));
        Assert.Equal(OnlineCharacterNameError.ContainsNullByte,
            OnlineCharacterNameRules.ValidateDeleteConfirmationName(new byte[] { 0x61, 0x00 }));
        Assert.Equal(OnlineCharacterNameError.InvalidUtf8,
            OnlineCharacterNameRules.ValidateDeleteConfirmationName(new byte[] { 0xFF, 0x61 }));
        Assert.Equal(OnlineCharacterNameError.InvalidUtf8,
            OnlineCharacterNameRules.ValidateDeleteConfirmationName("a\uDC00"));

        // 删除确认允许回传既有的空格或非 BMP 字符
        Assert.Equal(OnlineCharacterNameError.Valid, OnlineCharacterNameRules.ValidateDeleteConfirmationName("a b<c>"));
        Assert.Equal(OnlineCharacterNameError.Valid, OnlineCharacterNameRules.ValidateDeleteConfirmationName("\U0001F600"));
    }

    // ------------------------------------------------------------------ 结果对象

    [Fact]
    public void Outcome_factories_enforce_invariants()
    {
        OnlineCharacterManageOutcome ok = OnlineCharacterManageOutcome.Ok(ShellTestData.Roster(2), true);
        Assert.True(ok.Success);
        Assert.True(ok.CommandSucceeded);
        Assert.Equal(OnlineCharacterManageStatus.Succeeded, ok.Status);
        Assert.True(ok.EventCharacterFlagCleared);
        Assert.Equal(2, ok.Characters.Entries.Count);

        OnlineCharacterManageOutcome missing = OnlineCharacterManageOutcome.Ok(null);
        Assert.False(missing.Success);
        Assert.True(missing.CommandSucceeded);
        Assert.Equal(OnlineCharacterManageStatus.RosterMissing, missing.Status);

        OnlineCharacterManageOutcome failNormalized = OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.Succeeded);
        Assert.False(failNormalized.Success);
        Assert.Equal(OnlineCharacterManageStatus.ProtocolError, failNormalized.Status);

        OnlineCharacterManageOutcome unknown = OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.UnknownError, 0xAB);
        Assert.Equal((byte)0xAB, unknown.RawErrorCode);
    }

    // ------------------------------------------------------------------ FakeOnlineBackend 内存实现全分支

    [Fact]
    public async Task FakeBackend_defaults_to_disabled_and_enforces_in_memory_rules_when_enabled()
    {
        var backend = new FakeOnlineBackend();
        Assert.False(backend.AllowCharacterWrite);

        OnlineCharacterManageOutcome disabledCreate = await backend.CreateCharacterAsync(0, "Hero900", CancellationToken.None);
        OnlineCharacterManageOutcome disabledDelete = await backend.DeleteCharacterAsync(0, "Hero900", CancellationToken.None);
        Assert.Equal(OnlineCharacterManageStatus.Disabled, disabledCreate.Status);
        Assert.Equal(OnlineCharacterManageStatus.Disabled, disabledDelete.Status);

        backend.AllowCharacterWrite = true;
        backend.SeedManagedRoster(("Hero900", 0, 5));

        // 非法名字 / 重名 / 成功
        Assert.Equal(OnlineCharacterManageStatus.InvalidName,
            (await backend.CreateCharacterAsync(0, "ab", CancellationToken.None)).Status);
        Assert.Equal(OnlineCharacterManageStatus.DuplicateActiveName,
            (await backend.CreateCharacterAsync(0, "Hero900", CancellationToken.None)).Status);

        OnlineCharacterManageOutcome created = await backend.CreateCharacterAsync(3, "Hero901", CancellationToken.None);
        Assert.True(created.Success);
        Assert.Equal(2, created.Characters.Entries.Count);
        Assert.Equal((byte)3, created.Characters.Entries[1].Job);

        // 槽位满 36
        for (int i = 2; i < 36; i++)
        {
            Assert.True((await backend.CreateCharacterAsync((byte)(i % 11), "Role" + i.ToString("D3"), CancellationToken.None)).Success);
        }
        Assert.Equal(OnlineCharacterManageStatus.SlotLimitReached,
            (await backend.CreateCharacterAsync(0, "Overflow1", CancellationToken.None)).Status);

        // 删除：确认名不符、空槽、成功
        Assert.Equal(OnlineCharacterManageStatus.DatabaseOrUnknownError,
            (await backend.DeleteCharacterAsync(0, "WrongName", CancellationToken.None)).Status);
        Assert.Equal(OnlineCharacterManageStatus.EmptySlot,
            (await backend.DeleteCharacterAsync(36, "Hero900", CancellationToken.None)).Status);
        OnlineCharacterManageOutcome deleted = await backend.DeleteCharacterAsync(0, "Hero900", CancellationToken.None);
        Assert.True(deleted.Success);
        Assert.Equal(35, deleted.Characters.Entries.Count);

        // 取消与断线
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            backend.CreateCharacterAsync(0, "Hero999", stop.Token));

        backend.SimulateManageDisconnect = true;
        int disconnects = 0;
        backend.Disconnected += () => disconnects++;
        OnlineCharacterManageOutcome disc = await backend.CreateCharacterAsync(0, "Hero999", CancellationToken.None);
        Assert.Equal(OnlineCharacterManageStatus.Disconnected, disc.Status);
        Assert.Equal(1, disconnects);
    }
}
