using System;
using System.Collections.Generic;
using System.Text;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

public sealed class CharacterManagePayloadsTests
{
    [Fact]
    public void WireNumberTable_AllPublicConstantsEqualLiteralProtocolValues()
    {
        Assert.Equal(5, CharacterManagePayloads.CreateCharacterPacketId);
        Assert.Equal(6, CharacterManagePayloads.DeleteCharacterPacketId);
        Assert.Equal(2, CharacterManagePayloads.UserInfoPacketId);
        Assert.Equal(138, CharacterManagePayloads.ChangeCharacterNamePacketId);
        Assert.Equal(415, CharacterManagePayloads.SlotExtendEffectPacketId);

        Assert.Equal((byte)10, CharacterManagePayloads.MaxCreateJobId);
        Assert.Equal((byte)127, CharacterManagePayloads.MaxEventFlag);
        Assert.Equal(4, CharacterManagePayloads.MinCreateNameByteLength);
        Assert.Equal(18, CharacterManagePayloads.MaxCreateNameByteLength);
        Assert.Equal(29, CharacterManagePayloads.CreateNameServerBufferSize);
        Assert.Equal(1, CharacterManagePayloads.MinDeleteNameByteLength);
        Assert.Equal(29, CharacterManagePayloads.MaxDeleteNameByteLength);
        Assert.Equal(30, CharacterManagePayloads.DeleteNameServerBufferSize);
        Assert.Equal(36, CharacterManagePayloads.MaxServerCharacterSlots);

        Assert.Equal(CharacterCreateFailure.None, default(CharacterCreateFailure));
        Assert.Equal(CharacterDeleteFailure.None, default(CharacterDeleteFailure));
        Assert.Equal(CharacterNameValidationError.Valid, default(CharacterNameValidationError));
    }

    [Fact]
    public void BuildCreateCharacter_AndTryParseRequest_RoundTripExactWireBytes()
    {
        // Min boundary: job=0, len=4 ("Test"), event=0 (including TryBuild default parameter eventFlag = 0)
        byte[] name4 = new byte[] { 0x54, 0x65, 0x73, 0x74 };
        byte[] payload4 = CharacterManagePayloads.BuildCreateCharacter(0, name4);
        Assert.True(CharacterManagePayloads.TryBuildCreateCharacter(0, name4, out byte[] tryDefaultPayload));
        Assert.Equal(payload4, tryDefaultPayload);
        Assert.Equal(
            new byte[]
            {
                0x00,
                0x04, 0x00, 0x00, 0x00,
                0x54, 0x65, 0x73, 0x74,
                0x00,
            },
            payload4);

        Assert.True(CharacterManagePayloads.TryParseCreateCharacterRequest(payload4, out CreateCharacterRequest req4));
        Assert.Equal((byte)0, req4.Job);
        Assert.Equal(new byte[] { 0x54, 0x65, 0x73, 0x74 }, req4.CopyNameBytes());
        Assert.Equal((byte)0, req4.EventFlag);
        Assert.Equal(0, req4.TrailingByteCount);

        // Max boundary: job=10, 18-byte UTF-8 (6 BMP CJK characters), eventFlag=127, plus trailing bytes
        byte[] name18 = Encoding.UTF8.GetBytes("一二三四五六");
        Assert.Equal(18, name18.Length);
        byte[] payload18 = CharacterManagePayloads.BuildCreateCharacter(10, name18, eventFlag: 127);
        Assert.Equal((byte)10, payload18[0]);
        Assert.Equal((byte)18, payload18[1]);
        Assert.Equal((byte)0, payload18[2]);
        Assert.Equal((byte)0, payload18[3]);
        Assert.Equal((byte)0, payload18[4]);
        Assert.Equal((byte)127, payload18[23]);
        Assert.Equal(24, payload18.Length);

        byte[] withTrailing = new byte[payload18.Length + 4];
        Buffer.BlockCopy(payload18, 0, withTrailing, 0, payload18.Length);
        withTrailing[24] = 0x91;
        withTrailing[25] = 0x92;
        withTrailing[26] = 0x93;
        withTrailing[27] = 0x94;

        Assert.True(CharacterManagePayloads.TryParseCreateCharacterRequest(withTrailing, out CreateCharacterRequest req18));
        Assert.Equal((byte)10, req18.Job);
        Assert.Equal(name18, req18.CopyNameBytes());
        Assert.Equal((byte)127, req18.EventFlag);
        Assert.Equal(4, req18.TrailingByteCount);

        // Jump character flag=1
        Assert.True(CharacterManagePayloads.TryBuildCreateCharacter(3, new byte[] { 0x41, 0x42, 0x43, 0x44 }, out byte[] jumpPayload, eventFlag: 1));
        Assert.Equal((byte)3, jumpPayload[0]);
        Assert.Equal((byte)1, jumpPayload[9]);
    }

    [Fact]
    public void BuildCreateCharacter_MakesDefensiveCopiesAndEnforcesReadOnlyNameBytes()
    {
        byte[] callerName = new byte[] { 0x4E, 0x61, 0x6D, 0x39 };
        byte[] payload = CharacterManagePayloads.BuildCreateCharacter(2, callerName, eventFlag: 1);
        callerName[0] = 0x00;
        callerName[3] = 0x7A;

        Assert.True(CharacterManagePayloads.TryParseCreateCharacterRequest(payload, out CreateCharacterRequest req));
        Assert.Equal(new byte[] { 0x4E, 0x61, 0x6D, 0x39 }, req.CopyNameBytes());

        byte[] copy1 = req.CopyNameBytes();
        copy1[0] = 0x21;
        Assert.Equal((byte)0x4E, req.NameBytes[0]);

        var asIList = (IList<byte>)req.NameBytes;
        Assert.Throws<NotSupportedException>(() => asIList[0] = 0x55);
        Assert.Throws<NotSupportedException>(() => asIList.Add(0x55));
    }

    [Fact]
    public void BuildCreateCharacter_RejectsOutOfRangeAndNullAndNulNames()
    {
        byte[] validName4 = new byte[] { 0x41, 0x42, 0x43, 0x44 };
        byte[] validName18 = Encoding.ASCII.GetBytes("SyntheticChar_9012");
        Assert.Equal(18, validName18.Length);

        Assert.Throws<ArgumentNullException>(() => CharacterManagePayloads.BuildCreateCharacter(0, null));
        Assert.False(CharacterManagePayloads.TryBuildCreateCharacter(0, null, out byte[] p));
        Assert.Null(p);

        // Job boundaries: 0..10 valid, 11 & 255 invalid
        foreach (byte badJob in new byte[] { 11, 12, 255 })
        {
            Assert.False(CharacterManagePayloads.TryBuildCreateCharacter(badJob, validName4, out byte[] badPayload));
            Assert.Null(badPayload);
            Assert.Throws<ArgumentException>(() => CharacterManagePayloads.BuildCreateCharacter(badJob, validName4));
        }

        // Event flag boundaries: 0..127 valid, 128 & 255 invalid
        foreach (byte badEvent in new byte[] { 128, 129, 255 })
        {
            Assert.False(CharacterManagePayloads.TryBuildCreateCharacter(1, validName4, out byte[] badPayload, badEvent));
            Assert.Null(badPayload);
            Assert.Throws<ArgumentException>(() => CharacterManagePayloads.BuildCreateCharacter(1, validName4, badEvent));
        }

        // Name lengths: 0, 1, 3 too short; 19, 29 too long
        foreach (int len in new[] { 0, 1, 3, 19, 29 })
        {
            byte[] badLenName = new byte[len];
            for (int i = 0; i < len; i++)
            {
                badLenName[i] = (byte)(0x61 + (i % 20));
            }
            Assert.False(CharacterManagePayloads.TryBuildCreateCharacter(1, badLenName, out byte[] badPayload));
            Assert.Null(badPayload);
            Assert.Throws<ArgumentException>(() => CharacterManagePayloads.BuildCreateCharacter(1, badLenName));
        }

        // Embedded NUL at index 0, middle, end
        foreach (byte[] nulName in new[]
        {
            new byte[] { 0x00, 0x42, 0x43, 0x44 },
            new byte[] { 0x41, 0x00, 0x43, 0x44 },
            new byte[] { 0x41, 0x42, 0x43, 0x00 },
        })
        {
            Assert.False(CharacterManagePayloads.TryBuildCreateCharacter(1, nulName, out byte[] badPayload));
            Assert.Null(badPayload);
            Assert.Throws<ArgumentException>(() => CharacterManagePayloads.BuildCreateCharacter(1, nulName));
        }
    }

    [Fact]
    public void TryParseCreateCharacterRequest_RejectsMalformedTruncatedOutOfRangeAndNul()
    {
        Assert.False(CharacterManagePayloads.TryParseCreateCharacterRequest(null, out _));
        Assert.False(CharacterManagePayloads.TryParseCreateCharacterRequest(Array.Empty<byte>(), out _));

        byte[] valid = new byte[] { 4, 4, 0, 0, 0, 0x52, 0x6F, 0x6C, 0x65, 2 };
        for (int len = 0; len < valid.Length; len++)
        {
            Assert.False(CharacterManagePayloads.TryParseCreateCharacterRequest(valid.AsSpan(0, len).ToArray(), out CreateCharacterRequest req));
            Assert.Null(req);
        }

        // Out-of-range job = 11
        Assert.False(CharacterManagePayloads.TryParseCreateCharacterRequest(
            new byte[] { 11, 4, 0, 0, 0, 0x52, 0x6F, 0x6C, 0x65, 0 }, out _));

        // Out-of-range eventFlag = 128
        Assert.False(CharacterManagePayloads.TryParseCreateCharacterRequest(
            new byte[] { 2, 4, 0, 0, 0, 0x52, 0x6F, 0x6C, 0x65, 128 }, out _));

        // Declared length < 4 (3) or > 18 (19) or negative (-1)
        Assert.False(CharacterManagePayloads.TryParseCreateCharacterRequest(
            new byte[] { 2, 3, 0, 0, 0, 0x52, 0x6F, 0x6C, 0 }, out _));
        Assert.False(CharacterManagePayloads.TryParseCreateCharacterRequest(
            new byte[] { 2, 0xFF, 0xFF, 0xFF, 0xFF, 0x52, 0x6F, 0x6C, 0x65, 0 }, out _));

        byte[] len19 = new byte[1 + 4 + 19 + 1];
        len19[0] = 2;
        len19[1] = 19;
        for (int i = 0; i < 19; i++) len19[5 + i] = 0x41;
        Assert.False(CharacterManagePayloads.TryParseCreateCharacterRequest(len19, out _));

        // Embedded NUL stops get_str early -> nameBytes.Length != nameLength
        Assert.False(CharacterManagePayloads.TryParseCreateCharacterRequest(
            new byte[] { 2, 4, 0, 0, 0, 0x41, 0x00, 0x43, 0x44, 0x00 }, out _));
    }

    [Fact]
    public void TryParseCreateResult_HandlesSuccessFailureTruncationAndUnknownCodes()
    {
        // Reject null, empty, truncated failure
        Assert.False(CharacterManagePayloads.TryParseCreateResult(null, out _));
        Assert.False(CharacterManagePayloads.TryParseCreateResult(Array.Empty<byte>(), out _));
        Assert.False(CharacterManagePayloads.TryParseCreateResult(new byte[] { 0 }, out CharacterCreateResult truncated));
        Assert.Null(truncated);

        // Success flags: 1, 2, 255 with trailing bytes
        foreach (byte flag in new byte[] { 1, 2, 255 })
        {
            byte[] payload = flag == 1 ? new byte[] { flag } : new byte[] { flag, 0x91, 0x92, 0x93, 0x94 };
            Assert.True(CharacterManagePayloads.TryParseCreateResult(payload, out CharacterCreateResult ok));
            Assert.NotNull(ok);
            Assert.True(ok.IsSuccess);
            Assert.Equal(flag, ok.Flag);
            Assert.Equal((byte)0, ok.FailureCode);
            Assert.Equal(CharacterCreateFailure.None, ok.Failure);
            Assert.Equal(flag == 1 ? 0 : 4, ok.TrailingByteCount);
        }

        // All documented Create error codes + trailing bytes
        var expected = new (byte Code, CharacterCreateFailure Kind)[]
        {
            (0x02, CharacterCreateFailure.DatabaseOrUnknownError),
            (0x04, CharacterCreateFailure.SlotLimitReached),
            (0x05, CharacterCreateFailure.ClientRestricted),
            (0x07, CharacterCreateFailure.CreationRestrictedEmptyAccount),
            (0x08, CharacterCreateFailure.CreationRestrictedByEvent),
            (0x12, CharacterCreateFailure.DuplicateActiveName),
            (0x13, CharacterCreateFailure.StateLocked),
            (0x14, CharacterCreateFailure.EventCharacterConditionFailed),
            (0x16, CharacterCreateFailure.ClientSilentIgnored),
            (0x17, CharacterCreateFailure.CreationBlocked),
            (0x18, CharacterCreateFailure.DuplicateDeletedName),
            (0x5f, CharacterCreateFailure.DailyCreationLimitReached),
            (0x60, CharacterCreateFailure.ChannelNotAllowed),
            (0x9f, CharacterCreateFailure.InvalidName),
            (0xd1, CharacterCreateFailure.ServiceRestricted),
        };

        foreach ((byte code, CharacterCreateFailure kind) in expected)
        {
            Assert.True(CharacterManagePayloads.TryParseCreateResult(new byte[] { 0, code }, out CharacterCreateResult fail0));
            Assert.False(fail0.IsSuccess);
            Assert.Equal((byte)0, fail0.Flag);
            Assert.Equal(code, fail0.FailureCode);
            Assert.Equal(kind, fail0.Failure);
            Assert.Equal(0, fail0.TrailingByteCount);

            Assert.True(CharacterManagePayloads.TryParseCreateResult(new byte[] { 0, code, 0xAA, 0xBB }, out CharacterCreateResult fail2));
            Assert.Equal(code, fail2.FailureCode);
            Assert.Equal(kind, fail2.Failure);
            Assert.Equal(2, fail2.TrailingByteCount);
        }

        // Unknown codes (including boundary neighbors +-1) preserve raw FailureCode
        foreach (byte unknownCode in new byte[] { 0x00, 0x01, 0x03, 0x06, 0x09, 0x11, 0x15, 0x19, 0x5e, 0x61, 0x9e, 0xa0, 0xd0, 0xd2, 0xfe, 0xff })
        {
            Assert.True(CharacterManagePayloads.TryParseCreateResult(new byte[] { 0, unknownCode }, out CharacterCreateResult unknown));
            Assert.False(unknown.IsSuccess);
            Assert.Equal(unknownCode, unknown.FailureCode);
            Assert.Equal(CharacterCreateFailure.Unknown, unknown.Failure);
        }
    }

    [Fact]
    public void BuildDeleteCharacter_AndTryParseRequest_RoundTripExactWireBytes()
    {
        // Min boundary: slot 0, 1-byte confirmation name
        byte[] minPayload = CharacterManagePayloads.BuildDeleteCharacter(0, new byte[] { 0x5A });
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x5A }, minPayload);

        Assert.True(CharacterManagePayloads.TryParseDeleteCharacterRequest(minPayload, out DeleteCharacterRequest minReq));
        Assert.Equal((byte)0, minReq.SlotIndex);
        Assert.Equal(new byte[] { 0x5A }, minReq.CopyNameBytes());
        Assert.Equal(0, minReq.TrailingByteCount);

        // Max boundary: slot 255, 29-byte name (< 30 buffer), trailing bytes
        byte[] name29 = Encoding.ASCII.GetBytes("Char_901_Delete_Confirm_Max29");
        Assert.Equal(29, name29.Length);
        byte[] maxPayload = CharacterManagePayloads.BuildDeleteCharacter(255, name29);
        Assert.Equal(34, maxPayload.Length);
        Assert.Equal((byte)255, maxPayload[0]);
        Assert.Equal((byte)29, maxPayload[1]);

        byte[] maxWithTail = new byte[maxPayload.Length + 3];
        Buffer.BlockCopy(maxPayload, 0, maxWithTail, 0, maxPayload.Length);
        maxWithTail[34] = 0x11;
        maxWithTail[35] = 0x22;
        maxWithTail[36] = 0x33;

        Assert.True(CharacterManagePayloads.TryParseDeleteCharacterRequest(maxWithTail, out DeleteCharacterRequest maxReq));
        Assert.Equal((byte)255, maxReq.SlotIndex);
        Assert.Equal(name29, maxReq.CopyNameBytes());
        Assert.Equal(3, maxReq.TrailingByteCount);

        // Defensive copy & readonly enforcement
        byte[] caller = new byte[] { 0x4B, 0x39, 0x30 };
        byte[] built = CharacterManagePayloads.BuildDeleteCharacter(3, caller);
        caller[0] = 0x00;
        Assert.True(CharacterManagePayloads.TryParseDeleteCharacterRequest(built, out DeleteCharacterRequest copyReq));
        Assert.Equal(new byte[] { 0x4B, 0x39, 0x30 }, copyReq.CopyNameBytes());
        byte[] secondCopy = copyReq.CopyNameBytes();
        secondCopy[0] = 0x20;
        Assert.Equal((byte)0x4B, copyReq.NameBytes[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<byte>)copyReq.NameBytes)[0] = 0x77);
    }

    [Fact]
    public void BuildDeleteCharacter_AndTryParseRequest_RejectInvalidInputsAndTruncations()
    {
        Assert.Throws<ArgumentNullException>(() => CharacterManagePayloads.BuildDeleteCharacter(0, null));
        Assert.False(CharacterManagePayloads.TryBuildDeleteCharacter(0, null, out byte[] nullPayload));
        Assert.Null(nullPayload);

        // Bad lengths: 0, 30, 31
        foreach (int len in new[] { 0, 30, 31 })
        {
            byte[] bad = new byte[len];
            for (int i = 0; i < len; i++) bad[i] = 0x43;
            Assert.False(CharacterManagePayloads.TryBuildDeleteCharacter(1, bad, out _));
            Assert.Throws<ArgumentException>(() => CharacterManagePayloads.BuildDeleteCharacter(1, bad));
        }

        // Embedded NUL in 1..29 byte range
        foreach (byte[] badNul in new[]
        {
            new byte[] { 0x00 },
            new byte[] { 0x00, 0x41 },
            new byte[] { 0x41, 0x00 },
            new byte[] { 0x41, 0x00, 0x42 },
        })
        {
            Assert.False(CharacterManagePayloads.TryBuildDeleteCharacter(2, badNul, out _));
            Assert.Throws<ArgumentException>(() => CharacterManagePayloads.BuildDeleteCharacter(2, badNul));
        }

        // Parser rejections
        Assert.False(CharacterManagePayloads.TryParseDeleteCharacterRequest(null, out _));
        byte[] valid = new byte[] { 5, 3, 0, 0, 0, 0x41, 0x42, 0x43 };
        for (int len = 0; len < valid.Length; len++)
        {
            Assert.False(CharacterManagePayloads.TryParseDeleteCharacterRequest(valid.AsSpan(0, len).ToArray(), out DeleteCharacterRequest r));
            Assert.Null(r);
        }

        // Length 0, negative, or >= 30
        Assert.False(CharacterManagePayloads.TryParseDeleteCharacterRequest(new byte[] { 0, 0, 0, 0, 0 }, out _));
        Assert.False(CharacterManagePayloads.TryParseDeleteCharacterRequest(new byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x41 }, out _));
        byte[] len30 = new byte[1 + 4 + 30];
        len30[1] = 30;
        for (int i = 0; i < 30; i++) len30[5 + i] = 0x44;
        Assert.False(CharacterManagePayloads.TryParseDeleteCharacterRequest(len30, out _));

        // Embedded NUL in payload
        Assert.False(CharacterManagePayloads.TryParseDeleteCharacterRequest(
            new byte[] { 1, 3, 0, 0, 0, 0x41, 0x00, 0x43 }, out _));
    }

    [Fact]
    public void TryParseDeleteResult_HandlesSuccessEventByteFailureAndUnknownCodes()
    {
        // Null, empty, 1-byte truncated success [1] / [255], 1-byte truncated failure [0]
        Assert.False(CharacterManagePayloads.TryParseDeleteResult(null, out _));
        Assert.False(CharacterManagePayloads.TryParseDeleteResult(Array.Empty<byte>(), out _));
        Assert.False(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 1 }, out _));
        Assert.False(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 255 }, out _));
        Assert.False(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 0 }, out _));

        // Success with event flag = 0
        Assert.True(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 1, 0 }, out CharacterDeleteResult ok0));
        Assert.True(ok0.IsSuccess);
        Assert.Equal((byte)1, ok0.Flag);
        Assert.Equal((byte)0, ok0.EventCharacClearedFlag);
        Assert.False(ok0.IsEventCharacterFlagCleared);
        Assert.Equal((byte)0, ok0.FailureCode);
        Assert.Equal(CharacterDeleteFailure.None, ok0.Failure);
        Assert.Equal(0, ok0.TrailingByteCount);

        // Success with event flag = 1 and flag = 2 + trailing bytes
        Assert.True(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 1, 1 }, out CharacterDeleteResult ok1));
        Assert.True(ok1.IsSuccess);
        Assert.Equal((byte)1, ok1.EventCharacClearedFlag);
        Assert.True(ok1.IsEventCharacterFlagCleared);

        Assert.True(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 2, 2, 0xE1, 0xE2, 0xE3, 0xE4 }, out CharacterDeleteResult ok2));
        Assert.True(ok2.IsSuccess);
        Assert.Equal((byte)2, ok2.Flag);
        Assert.Equal((byte)2, ok2.EventCharacClearedFlag);
        Assert.True(ok2.IsEventCharacterFlagCleared);
        Assert.Equal(CharacterDeleteFailure.None, ok2.Failure);
        Assert.Equal(4, ok2.TrailingByteCount);

        // Every mapped Delete error code
        var expected = new (byte Code, CharacterDeleteFailure Kind)[]
        {
            (0x02, CharacterDeleteFailure.DatabaseOrUnknownError),
            (0x13, CharacterDeleteFailure.StateLocked),
            (0x15, CharacterDeleteFailure.EmptySlot),
            (0x16, CharacterDeleteFailure.CooldownOrActiveCharacter),
            (0x24, CharacterDeleteFailure.GuildMasterCannotDelete),
            (0x28, CharacterDeleteFailure.ClientDeletionBlocked),
            (0x72, CharacterDeleteFailure.TradePunished),
            (0xcf, CharacterDeleteFailure.RestingUserRestricted),
            (0xd1, CharacterDeleteFailure.ServiceRestricted),
            (0xd5, CharacterDeleteFailure.ItemLockActive),
            (0xe4, CharacterDeleteFailure.TradePunishRestricted),
            (0xe5, CharacterDeleteFailure.RestorePunishRestricted),
            (0xe9, CharacterDeleteFailure.SecurityServiceRequired),
            (0x4d, CharacterDeleteFailure.SharedSecurityRestriction),
            (0x73, CharacterDeleteFailure.SharedSecurityRestriction),
            (0x78, CharacterDeleteFailure.SharedSecurityRestriction),
            (0x7a, CharacterDeleteFailure.SharedSecurityRestriction),
            (0x7b, CharacterDeleteFailure.SharedSecurityRestriction),
            (0x7c, CharacterDeleteFailure.SharedSecurityRestriction),
            (0x88, CharacterDeleteFailure.SharedSecurityRestriction),
            (0x89, CharacterDeleteFailure.SharedSecurityRestriction),
            (0x8a, CharacterDeleteFailure.SharedSecurityRestriction),
            (0xa7, CharacterDeleteFailure.SharedSecurityRestriction),
            (0xec, CharacterDeleteFailure.SharedSecurityRestriction),
            (0xee, CharacterDeleteFailure.SharedSecurityRestriction),
        };

        foreach ((byte code, CharacterDeleteFailure kind) in expected)
        {
            Assert.True(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 0, code, 0xCC }, out CharacterDeleteResult fail));
            Assert.False(fail.IsSuccess);
            Assert.Equal((byte)0, fail.Flag);
            Assert.Equal((byte)0, fail.EventCharacClearedFlag);
            Assert.False(fail.IsEventCharacterFlagCleared);
            Assert.Equal(code, fail.FailureCode);
            Assert.Equal(kind, fail.Failure);
            Assert.Equal(1, fail.TrailingByteCount);
        }

        // Unknown error codes including boundary neighbors preserve raw FailureCode
        foreach (byte unknownCode in new byte[]
        {
            0x00, 0x01, 0x03, 0x12, 0x14, 0x17, 0x23, 0x25, 0x27, 0x29,
            0x4c, 0x4e, 0x71, 0x74, 0x77, 0x79, 0x7d, 0x87, 0x8b, 0xa6,
            0xa8, 0xce, 0xd0, 0xd2, 0xd4, 0xd6, 0xe3, 0xe6, 0xe8, 0xea,
            0xeb, 0xed, 0xef, 0xff,
        })
        {
            Assert.True(CharacterManagePayloads.TryParseDeleteResult(new byte[] { 0, unknownCode }, out CharacterDeleteResult unknown));
            Assert.False(unknown.IsSuccess);
            Assert.Equal(unknownCode, unknown.FailureCode);
            Assert.Equal(CharacterDeleteFailure.Unknown, unknown.Failure);
        }
    }

    [Fact]
    public void CharacterNameRules_ForbiddenAsciiListAndNeighbors_AreExact()
    {
        byte[] expectedBanned = { 0x27, 0x20, 0x09, 0x5c, 0x25, 0x3c, 0x3e, 0x22 };
        Assert.Equal(expectedBanned.Length, CharacterNameRules.ForbiddenCreateAsciiBytes.Count);
        for (int i = 0; i < expectedBanned.Length; i++)
        {
            Assert.Equal(expectedBanned[i], CharacterNameRules.ForbiddenCreateAsciiBytes[i]);
            Assert.True(CharacterNameRules.IsForbiddenCreateAsciiByte(expectedBanned[i]));
        }

        Assert.Throws<NotSupportedException>(() => ((IList<byte>)CharacterNameRules.ForbiddenCreateAsciiBytes)[0] = 0x41);

        var bannedSet = new HashSet<byte>(expectedBanned);
        for (int b = 0; b <= 255; b++)
        {
            byte val = (byte)b;
            Assert.Equal(bannedSet.Contains(val), CharacterNameRules.IsForbiddenCreateAsciiByte(val));
        }
    }

    [Fact]
    public void CharacterNameRules_ValidateCreateName_ChecksAllStructuralBoundaries()
    {
        Assert.Equal(CharacterNameValidationError.Null, CharacterNameRules.ValidateCreateName(null));

        // Length < 4 -> TooShort (even if bytes are NUL or invalid UTF-8)
        Assert.Equal(CharacterNameValidationError.TooShort, CharacterNameRules.ValidateCreateName(Array.Empty<byte>()));
        Assert.Equal(CharacterNameValidationError.TooShort, CharacterNameRules.ValidateCreateName(new byte[] { 0x41 }));
        Assert.Equal(CharacterNameValidationError.TooShort, CharacterNameRules.ValidateCreateName(new byte[] { 0x00 }));
        Assert.Equal(CharacterNameValidationError.TooShort, CharacterNameRules.ValidateCreateName(new byte[] { 0xFF, 0x41, 0x42 }));

        // Length > 18 -> TooLong (even if bytes contain NUL)
        byte[] len19WithNul = new byte[19];
        for (int i = 1; i < 19; i++) len19WithNul[i] = 0x41;
        Assert.Equal(CharacterNameValidationError.TooLong, CharacterNameRules.ValidateCreateName(len19WithNul));
        Assert.Equal(CharacterNameValidationError.TooLong, CharacterNameRules.ValidateCreateName(Encoding.ASCII.GetBytes("NameLengthNineteen9")));

        // Valid lengths 4..18 including multi-byte BMP UTF-8 and non-zero 0x01 control byte (no NUL)
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateCreateName(Encoding.ASCII.GetBytes("Ab91")));
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateCreateName(new byte[] { 0x01, 0x41, 0x42, 0x43 }));
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateCreateName(Encoding.UTF8.GetBytes("αβ")));       // 4 bytes
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateCreateName(Encoding.UTF8.GetBytes("测试")));      // 6 bytes
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateCreateName(Encoding.UTF8.GetBytes("甲乙丙丁戊己"))); // 18 bytes
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateCreateName(new byte[] { 0xEF, 0xBF, 0xAF, 0x41 })); // U+FFEF + 'A'
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateCreateName(new byte[] { 0xEF, 0xBF, 0xBF, 0x41 })); // U+FFFF + 'A' (0xEF < 0xF0 boundary)

        // Embedded NUL at start, middle, end
        Assert.Equal(CharacterNameValidationError.ContainsNullByte, CharacterNameRules.ValidateCreateName(new byte[] { 0x00, 0x41, 0x42, 0x43 }));
        Assert.Equal(CharacterNameValidationError.ContainsNullByte, CharacterNameRules.ValidateCreateName(new byte[] { 0x41, 0x00, 0x42, 0x43 }));
        Assert.Equal(CharacterNameValidationError.ContainsNullByte, CharacterNameRules.ValidateCreateName(new byte[] { 0x41, 0x42, 0x43, 0x00 }));

        // Invalid UTF-8 (runs before NonBmp/ForbiddenAscii)
        Assert.Equal(CharacterNameValidationError.InvalidUtf8, CharacterNameRules.ValidateCreateName(new byte[] { 0xFF, 0x41, 0x42, 0x43 }));
        Assert.Equal(CharacterNameValidationError.InvalidUtf8, CharacterNameRules.ValidateCreateName(new byte[] { 0xE4, 0xB8, 0x41, 0x42 }));
        Assert.Equal(CharacterNameValidationError.InvalidUtf8, CharacterNameRules.ValidateCreateName(new byte[] { 0xC0, 0xAF, 0x41, 0x42 }));
        Assert.Equal(CharacterNameValidationError.InvalidUtf8, CharacterNameRules.ValidateCreateName(new byte[] { 0xED, 0xA0, 0x80, 0x41 }));
        Assert.Equal(CharacterNameValidationError.InvalidUtf8, CharacterNameRules.ValidateCreateName(new byte[] { 0xFF, 0x41, 0x42, (byte)'\'' }));

        // Non-BMP (4-byte UTF-8 > U+FFFF; checked at index 0 and before ForbiddenAsciiCharacter)
        Assert.Equal(CharacterNameValidationError.NonBmpCharacter, CharacterNameRules.ValidateCreateName(new byte[] { 0xF0, 0x90, 0x80, 0x80 })); // U+10000 boundary (0xF0)
        Assert.Equal(CharacterNameValidationError.NonBmpCharacter, CharacterNameRules.ValidateCreateName(new byte[] { 0xF0, 0x9F, 0x98, 0x80 })); // U+1F600 (4 bytes)
        Assert.Equal(CharacterNameValidationError.NonBmpCharacter, CharacterNameRules.ValidateCreateName(new byte[] { 0x41, 0xF0, 0x90, 0x80, 0x80 })); // 'A' + U+10000
        Assert.Equal(CharacterNameValidationError.NonBmpCharacter, CharacterNameRules.ValidateCreateName(new byte[] { 0xF0, 0x9F, 0x98, 0x80, (byte)'\'' }));

        // Forbidden 8 ASCII chars checked on normal path (placed at index 0, middle, end)
        foreach (string badName in new[]
        {
            "'abc",
            "a bc",
            "ab\tc",
            "abc\\",
            "ab%c",
            "<abc",
            "abc>",
            "a\"bc",
        })
        {
            Assert.Equal(
                CharacterNameValidationError.ForbiddenAsciiCharacter,
                CharacterNameRules.ValidateCreateName(Encoding.ASCII.GetBytes(badName)));
        }
    }

    [Fact]
    public void CharacterNameRules_ValidateDeleteConfirmationName_ChecksAllStructuralBoundaries()
    {
        Assert.Equal(CharacterNameValidationError.Null, CharacterNameRules.ValidateDeleteConfirmationName(null));
        Assert.Equal(CharacterNameValidationError.TooShort, CharacterNameRules.ValidateDeleteConfirmationName(Array.Empty<byte>()));

        // Valid 1 byte (incl. 0x01), 4-byte non-BMP, create-forbidden ASCII, and max 29 bytes
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateDeleteConfirmationName(new byte[] { 0x41 }));
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateDeleteConfirmationName(new byte[] { 0x01 }));
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateDeleteConfirmationName(Encoding.UTF8.GetBytes("a' b<\t\U0001F600")));
        Assert.Equal(CharacterNameValidationError.Valid, CharacterNameRules.ValidateDeleteConfirmationName(Encoding.ASCII.GetBytes("Char_Delete_Confirm_Max_29_91")));

        // TooLong at 30 bytes (even with NUL)
        byte[] len30 = new byte[30];
        len30[0] = 0x00;
        for (int i = 1; i < 30; i++) len30[i] = 0x42;
        Assert.Equal(CharacterNameValidationError.TooLong, CharacterNameRules.ValidateDeleteConfirmationName(len30));

        // NUL at 1 byte or inside 29-byte name
        Assert.Equal(CharacterNameValidationError.ContainsNullByte, CharacterNameRules.ValidateDeleteConfirmationName(new byte[] { 0x00 }));
        byte[] len29EndNul = Encoding.ASCII.GetBytes("Char_Delete_Confirm_Max_29_9\0");
        Assert.Equal(29, len29EndNul.Length);
        Assert.Equal(CharacterNameValidationError.ContainsNullByte, CharacterNameRules.ValidateDeleteConfirmationName(len29EndNul));

        // Invalid UTF-8 in 1..29 byte range
        Assert.Equal(CharacterNameValidationError.InvalidUtf8, CharacterNameRules.ValidateDeleteConfirmationName(new byte[] { 0xFE }));
        Assert.Equal(CharacterNameValidationError.InvalidUtf8, CharacterNameRules.ValidateDeleteConfirmationName(new byte[] { 0xE4, 0xB8, 0x41 }));
    }
}
