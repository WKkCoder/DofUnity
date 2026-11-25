using System;
using System.Collections.Generic;
using System.Text;

namespace Dnf.Play
{
    /// <summary>Play 层角色名结构性校验结论（不依赖 Dnf.Net）。</summary>
    public enum OnlineCharacterNameError
    {
        Valid = 0,
        Null,
        TooShort,
        TooLong,
        ContainsNullByte,
        InvalidUtf8,
        NonBmpCharacter,
        ForbiddenAsciiCharacter,
    }

    /// <summary>
    /// 角色名结构性规则校验器（纯函数，Play 层镜像，不引用 Dnf.Net）。
    /// 创建名：UTF-8 4..18 字节、无 NUL、严格 UTF-8、仅 BMP（首字节 &lt; 0xF0）、禁用 8 个 ASCII 字符（'、空格、\t、\、%、&lt;、&gt;、"）。
    /// 删除确认名：UTF-8 1..29 字节、无 NUL、严格 UTF-8。
    /// </summary>
    public static class OnlineCharacterNameRules
    {
        public const byte MaxCreateJobId = 10;
        public const int MaxServerCharacterSlots = 36;
        public const int MinCreateNameBytes = 4;
        public const int MaxCreateNameBytes = 18;
        public const int MinDeleteNameBytes = 1;
        public const int MaxDeleteNameBytes = 29;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static readonly IReadOnlyList<byte> ForbiddenCreateAsciiBytes = Array.AsReadOnly(new byte[]
        {
            (byte)'\'',
            (byte)' ',
            (byte)'\t',
            (byte)'\\',
            (byte)'%',
            (byte)'<',
            (byte)'>',
            (byte)'"',
        });

        public static bool IsForbiddenCreateAsciiByte(byte value)
        {
            for (int i = 0; i < ForbiddenCreateAsciiBytes.Count; i++)
            {
                if (ForbiddenCreateAsciiBytes[i] == value)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool TryEncodeStrictUtf8(string text, out byte[] utf8Bytes)
        {
            utf8Bytes = null;
            if (text == null)
            {
                return false;
            }
            try
            {
                utf8Bytes = StrictUtf8.GetBytes(text);
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        public static int GetUtf8ByteCount(string text)
        {
            return TryEncodeStrictUtf8(text, out byte[] bytes) ? bytes.Length : -1;
        }

        public static OnlineCharacterNameError ValidateCreateName(string text)
        {
            if (text == null)
            {
                return OnlineCharacterNameError.Null;
            }
            if (!TryEncodeStrictUtf8(text, out byte[] bytes))
            {
                return OnlineCharacterNameError.InvalidUtf8;
            }
            return ValidateCreateName(bytes);
        }

        public static OnlineCharacterNameError ValidateCreateName(byte[] utf8Bytes)
        {
            OnlineCharacterNameError core = ValidateCore(utf8Bytes, MinCreateNameBytes, MaxCreateNameBytes);
            if (core != OnlineCharacterNameError.Valid)
            {
                return core;
            }
            for (int i = 0; i < utf8Bytes.Length; i++)
            {
                if (utf8Bytes[i] >= 0xF0)
                {
                    return OnlineCharacterNameError.NonBmpCharacter;
                }
            }
            for (int i = 0; i < utf8Bytes.Length; i++)
            {
                if (IsForbiddenCreateAsciiByte(utf8Bytes[i]))
                {
                    return OnlineCharacterNameError.ForbiddenAsciiCharacter;
                }
            }
            return OnlineCharacterNameError.Valid;
        }

        public static OnlineCharacterNameError ValidateDeleteConfirmationName(string text)
        {
            if (text == null)
            {
                return OnlineCharacterNameError.Null;
            }
            if (!TryEncodeStrictUtf8(text, out byte[] bytes))
            {
                return OnlineCharacterNameError.InvalidUtf8;
            }
            return ValidateDeleteConfirmationName(bytes);
        }

        public static OnlineCharacterNameError ValidateDeleteConfirmationName(byte[] utf8Bytes)
        {
            return ValidateCore(utf8Bytes, MinDeleteNameBytes, MaxDeleteNameBytes);
        }

        private static OnlineCharacterNameError ValidateCore(byte[] utf8Bytes, int minLength, int maxLength)
        {
            if (utf8Bytes == null)
            {
                return OnlineCharacterNameError.Null;
            }
            if (utf8Bytes.Length < minLength)
            {
                return OnlineCharacterNameError.TooShort;
            }
            if (utf8Bytes.Length > maxLength)
            {
                return OnlineCharacterNameError.TooLong;
            }
            for (int i = 0; i < utf8Bytes.Length; i++)
            {
                if (utf8Bytes[i] == 0)
                {
                    return OnlineCharacterNameError.ContainsNullByte;
                }
            }
            try
            {
                StrictUtf8.GetCharCount(utf8Bytes);
            }
            catch (DecoderFallbackException)
            {
                return OnlineCharacterNameError.InvalidUtf8;
            }
            return OnlineCharacterNameError.Valid;
        }
    }

    /// <summary>角色创建/删除结果状态（覆盖全部创建 15 码、删除 13 码、共用安全限制、未知码及本地/传输状态）。</summary>
    public enum OnlineCharacterManageStatus
    {
        Succeeded = 0,

        // ---- 本地 / 传输状态
        Disabled,
        InvalidInput,
        Cancelled,
        Disconnected,
        NoReply,
        ProtocolError,
        RosterMissing,
        RosterMalformed,
        UnknownError,

        // ---- 1/5 创建失败码（15 项；其中 DatabaseOrUnknownError / StateLocked / ServiceRestricted 与 1/6 共用）
        DatabaseOrUnknownError,
        SlotLimitReached,
        ClientRestricted,
        CreationRestrictedEmptyAccount,
        CreationRestrictedByEvent,
        DuplicateActiveName,
        StateLocked,
        EventCharacterConditionFailed,
        ClientSilentIgnored,
        CreationBlocked,
        DuplicateDeletedName,
        DailyCreationLimitReached,
        ChannelNotAllowed,
        InvalidName,
        ServiceRestricted,

        // ---- 1/6 删除专属失败码（其余 10 项 + 共用安全限制）
        EmptySlot,
        CooldownOrActiveCharacter,
        GuildMasterCannotDelete,
        ClientDeletionBlocked,
        TradePunished,
        RestingUserRestricted,
        ItemLockActive,
        TradePunishRestricted,
        RestorePunishRestricted,
        SecurityServiceRequired,
        SharedSecurityRestriction,
    }

    /// <summary>角色创建/删除操作的只读结局（含服务端推送刷新后的名册与原始错误码字节）。</summary>
    public sealed class OnlineCharacterManageOutcome
    {
        private OnlineCharacterManageOutcome(
            OnlineCharacterManageStatus status,
            bool commandSucceeded,
            byte? rawErrorCode,
            OnlineCharacterList characters,
            bool eventCharacterFlagCleared)
        {
            Status = status;
            CommandSucceeded = commandSucceeded;
            RawErrorCode = rawErrorCode;
            Characters = characters;
            EventCharacterFlagCleared = eventCharacterFlagCleared;
        }

        public bool Success => Status == OnlineCharacterManageStatus.Succeeded;

        public OnlineCharacterManageStatus Status { get; }

        public bool CommandSucceeded { get; }

        /// <summary>服务端返回业务拒绝时的原始错误字节；未知码也会原样保留在此。</summary>
        public byte? RawErrorCode { get; }

        /// <summary>创建或删除成功后由服务端 <c>0/2</c> 模式 2 刷新的全量名册；失败或缺失时为 null。</summary>
        public OnlineCharacterList Characters { get; }

        public bool EventCharacterFlagCleared { get; }

        public static OnlineCharacterManageOutcome Ok(OnlineCharacterList characters, bool eventCharacterFlagCleared = false)
        {
            if (characters == null)
            {
                return new OnlineCharacterManageOutcome(
                    OnlineCharacterManageStatus.RosterMissing,
                    true,
                    null,
                    null,
                    eventCharacterFlagCleared);
            }
            return new OnlineCharacterManageOutcome(
                OnlineCharacterManageStatus.Succeeded,
                true,
                null,
                characters,
                eventCharacterFlagCleared);
        }

        public static OnlineCharacterManageOutcome Fail(
            OnlineCharacterManageStatus status,
            byte? rawErrorCode = null,
            bool commandSucceeded = false)
        {
            OnlineCharacterManageStatus normalized = status == OnlineCharacterManageStatus.Succeeded
                ? OnlineCharacterManageStatus.ProtocolError
                : status;
            return new OnlineCharacterManageOutcome(normalized, commandSucceeded, rawErrorCode, null, false);
        }
    }
}
