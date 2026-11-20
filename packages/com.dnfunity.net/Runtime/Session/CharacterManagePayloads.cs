using System;
using System.Collections.Generic;
using System.Text;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 角色创建回复（<c>(1, 5) CREATE_CHARACTER</c>）在失败体（首字节为 0）中次字节的映射。
    /// <para>枚举数值不代表线上原码；线上原始字节始终保留在 <see cref="CharacterCreateResult.FailureCode"/> 中。</para>
    /// </summary>
    public enum CharacterCreateFailure
    {
        /// <summary>无失败（成功回复，首字节非 0）。</summary>
        None = 0,

        /// <summary>码 0x02：服务端数据库执行或其它创建失败（客户端与服务端双向确认）。</summary>
        DatabaseOrUnknownError,

        /// <summary>码 0x04：角色栏位已满或达到服务端上限 36 格（客户端与服务端双向确认）。</summary>
        SlotLimitReached,

        /// <summary>码 0x05：客户端特判创建受限（仅客户端 0x0071d1e1 映射到提示 ID 0xc42f，本地服务端未见发送路径）。</summary>
        ClientRestricted,

        /// <summary>码 0x07：账号零角色时的活动创建限制（服务端 <c>CRestrictCharacCreationEvent</c> 与客户端 0x0071d078 双向确认）。</summary>
        CreationRestrictedEmptyAccount,

        /// <summary>码 0x08：活动创建限制分支（服务端 <c>CRestrictCharacCreationEvent</c> 与客户端 0x0071d12c 双向确认）。</summary>
        CreationRestrictedByEvent,

        /// <summary>码 0x12：与现存未删除角色重名（服务端数据库与客户端 0x0071cf52 双向确认）。</summary>
        DuplicateActiveName,

        /// <summary>码 0x13：当前正处于删除角色锁或数据加载锁状态（服务端与客户端 0x0071cf65 双向确认）。</summary>
        StateLocked,

        /// <summary>码 0x14：活动/跳跃角色条件或数据库更新失败（服务端与客户端 0x0071d069 双向确认）。</summary>
        EventCharacterConditionFailed,

        /// <summary>码 0x16：客户端收到后显式静默忽略（客户端 0x0071cf7e 跳过提示直接返回）。</summary>
        ClientSilentIgnored,

        /// <summary>码 0x17：客户端特判拒绝创建（仅客户端 0x0071cf86 映射到提示 ID 0x2ad）。</summary>
        CreationBlocked,

        /// <summary>码 0x18：与带删除标记的已删角色重名（服务端数据库与客户端 0x0071cfc7 双向确认）。</summary>
        DuplicateDeletedName,

        /// <summary>码 0x5f：每日或上限建角次数受限（服务端数据库与客户端 0x0071d00e 双向确认）。</summary>
        DailyCreationLimitReached,

        /// <summary>码 0x60：客户端判定当前频道不允许创建角色（仅客户端 0x0071d055 特判提示）。</summary>
        ChannelNotAllowed,

        /// <summary>码 0x9f：角色名未通过服务端字符范围、特殊字符、禁用前缀或敏感词检查（服务端与客户端 0x0071cf61 双向确认）。</summary>
        InvalidName,

        /// <summary>码 0xd1：服务端 <c>ServiceRestrictManager</c> 拒绝（仅服务端 0x081c08b0 观察，客户端 0x0071ce80 未显式分派）。</summary>
        ServiceRestricted,

        /// <summary>其它未识别错误码；原始字节保存在 <see cref="CharacterCreateResult.FailureCode"/>。</summary>
        Unknown,
    }

    /// <summary>
    /// 角色删除回复（<c>(1, 6) DELETE_CHARACTER</c>）在失败体（首字节为 0）中次字节的映射。
    /// <para>枚举数值不代表线上原码；线上原始字节始终保留在 <see cref="CharacterDeleteResult.FailureCode"/> 中。</para>
    /// </summary>
    public enum CharacterDeleteFailure
    {
        /// <summary>无失败（成功回复，首字节非 0）。</summary>
        None = 0,

        /// <summary>码 0x02：服务端数据库删除失败（服务端与客户端 0x007080a8 双向确认）。</summary>
        DatabaseOrUnknownError,

        /// <summary>码 0x13：处于数据加载锁等忙碌状态（服务端与客户端 0x007080c2 双向确认）。</summary>
        StateLocked,

        /// <summary>码 0x15：目标槽位角色编号为空或无效（服务端 <c>DisPatcher_DeleteCharac::process</c> 与客户端 0x007080d0 双向确认）。</summary>
        EmptySlot,

        /// <summary>码 0x16：新创建角色同日冷却未过，或尝试删除当前登录角色（服务端与客户端 0x00708110 双向确认）。</summary>
        CooldownOrActiveCharacter,

        /// <summary>码 0x24：目标角色是公会会长，不能直接删除（服务端数据库与客户端 0x0070812c 双向确认）。</summary>
        GuildMasterCannotDelete,

        /// <summary>码 0x28：客户端特判禁止删除（仅客户端 0x0070811e 映射到提示 ID 0x2b5）。</summary>
        ClientDeletionBlocked,

        /// <summary>码 0x72：安全保护惩罚禁止交易/敏感操作（服务端与客户端共用映射 0x0105be56 双向确认）。</summary>
        TradePunished,

        /// <summary>码 0xcf：休眠/回归安全状态限制（服务端与客户端共用映射 0x0105bf5e 双向确认）。</summary>
        RestingUserRestricted,

        /// <summary>码 0xd1：服务限制管理器拒绝（服务端与客户端共用映射 0x0105c14f 双向确认）。</summary>
        ServiceRestricted,

        /// <summary>码 0xd5：角色身上存在锁定期内道具锁，72 小时内不可删除（服务端数据库与客户端共用映射 0x0105c124 双向确认）。</summary>
        ItemLockActive,

        /// <summary>码 0xe4：交易惩罚位（0x20）限制（服务端与客户端共用映射 0x0105bea8 双向确认）。</summary>
        TradePunishRestricted,

        /// <summary>码 0xe5：账号恢复保护位（0x80）限制（服务端与客户端共用映射 0x0105bec7 双向确认）。</summary>
        RestorePunishRestricted,

        /// <summary>码 0xe9：未启用所需安全服务（服务端 <c>CheckEtcType</c> 与客户端共用映射 0x0105c05e 双向确认）。</summary>
        SecurityServiceRequired,

        /// <summary>客户端共用安全/限制提示分发器（0x0105be50）中声明的其它保护与限制错误码（0x4d/0x73/0x78/0x7a/0x7b/0x7c/0x88/0x89/0x8a/0xa7/0xec/0xee）。</summary>
        SharedSecurityRestriction,

        /// <summary>其它未识别错误码；原始字节保存在 <see cref="CharacterDeleteResult.FailureCode"/>。</summary>
        Unknown,
    }

    /// <summary>角色名结构性校验结果。</summary>
    public enum CharacterNameValidationError
    {
        /// <summary>结构校验通过（不包含依赖外部词表/区表的内容过滤）。</summary>
        Valid = 0,

        /// <summary>输入字节数组为 null。</summary>
        Null,

        /// <summary>UTF-8 字节数低于最小长度下限（创建为 4，删除确认为 1）。</summary>
        TooShort,

        /// <summary>UTF-8 字节数超过最大长度上限（创建为 18，删除确认为 29）。</summary>
        TooLong,

        /// <summary>包含 0x00（NUL）字节，会截断服务端定长缓冲读取与比对。</summary>
        ContainsNullByte,

        /// <summary>不是合法的 UTF-8 字节序列（含孤立代理项、超长编码或截断序列）。</summary>
        InvalidUtf8,

        /// <summary>包含超出 BMP（大于 U+FFFF，4 字节 UTF-8）的字符；原版客户端编码器 0x011a0190 仅编码 1..3 字节 BMP 码点。</summary>
        NonBmpCharacter,

        /// <summary>包含原版常规创角路径在客户端显式拒绝的 8 个 ASCII 字符之一（'、空格、\t、\、%、&lt;、&gt;、"）。</summary>
        ForbiddenAsciiCharacter,
    }

    /// <summary><c>1/5 CREATE_CHARACTER</c> 上行请求只读解析结果。</summary>
    public sealed class CreateCharacterRequest
    {
        internal CreateCharacterRequest(byte job, byte[] nameBytes, byte eventFlag, int trailingByteCount)
        {
            Job = job;
            NameBytes = Array.AsReadOnly(nameBytes);
            EventFlag = eventFlag;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>基础职业编号（服务端读码合法范围 0..10）。</summary>
        public byte Job { get; }

        /// <summary>角色名原始 UTF-8 字节序列的只读包装。</summary>
        public IReadOnlyList<byte> NameBytes { get; }

        /// <summary>活动/跳跃角色标志字节（服务端仅接受有符号非负值 0..127；常规创建写 0）。</summary>
        public byte EventFlag { get; }

        /// <summary>读完请求字段后剩余的尾字节数（用于与 4 字节折叠完整性尾对账）。</summary>
        public int TrailingByteCount { get; }

        /// <summary>返回角色名原始字节的独立副本。</summary>
        public byte[] CopyNameBytes()
        {
            var copy = new byte[NameBytes.Count];
            for (int i = 0; i < NameBytes.Count; i++)
            {
                copy[i] = NameBytes[i];
            }
            return copy;
        }
    }

    /// <summary><c>1/6 DELETE_CHARACTER</c> 上行请求只读解析结果。</summary>
    public sealed class DeleteCharacterRequest
    {
        internal DeleteCharacterRequest(byte slotIndex, byte[] nameBytes, int trailingByteCount)
        {
            SlotIndex = slotIndex;
            NameBytes = Array.AsReadOnly(nameBytes);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>待删除角色在名册中的槽位索引（<c>u8</c>，由服务端校验是否小于当前角色总数）。</summary>
        public byte SlotIndex { get; }

        /// <summary>删除确认角色名原始 UTF-8 字节序列的只读包装。</summary>
        public IReadOnlyList<byte> NameBytes { get; }

        /// <summary>读完请求字段后剩余的尾字节数。</summary>
        public int TrailingByteCount { get; }

        /// <summary>返回确认角色名原始字节的独立副本。</summary>
        public byte[] CopyNameBytes()
        {
            var copy = new byte[NameBytes.Count];
            for (int i = 0; i < NameBytes.Count; i++)
            {
                copy[i] = NameBytes[i];
            }
            return copy;
        }
    }

    /// <summary><c>(1, 5) CREATE_CHARACTER</c> 服务端回复只读记录。</summary>
    public sealed class CharacterCreateResult
    {
        internal CharacterCreateResult(byte flag, byte failureCode, int trailingByteCount)
        {
            Flag = flag;
            FailureCode = failureCode;
            Failure = flag != 0 ? CharacterCreateFailure.None : CharacterManagePayloads.MapCreateFailureCode(failureCode);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>首字节原始成功标志（客户端 0x0071ceab 判非 0 即成功）。</summary>
        public byte Flag { get; }

        /// <summary>是否创建成功：<see cref="Flag"/> 非 0。</summary>
        public bool IsSuccess => Flag != 0;

        /// <summary>失败体次字节原始错误码；成功体恒为 0。</summary>
        public byte FailureCode { get; }

        /// <summary>失败码映射；成功体为 <see cref="CharacterCreateFailure.None"/>。</summary>
        public CharacterCreateFailure Failure { get; }

        /// <summary>读完成功或失败字段后剩余未消费的尾字节数（服务端在 1..0x190 字节回复末尾会附带 4 字节折叠哈希尾）。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary><c>(1, 6) DELETE_CHARACTER</c> 服务端回复只读记录。</summary>
    public sealed class CharacterDeleteResult
    {
        internal CharacterDeleteResult(byte flag, byte eventCharacClearedFlag, byte failureCode, int trailingByteCount)
        {
            Flag = flag;
            EventCharacClearedFlag = eventCharacClearedFlag;
            FailureCode = failureCode;
            Failure = flag != 0 ? CharacterDeleteFailure.None : CharacterManagePayloads.MapDeleteFailureCode(failureCode);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>首字节原始成功标志（客户端 0x00708033 判非 0 即成功）。</summary>
        public byte Flag { get; }

        /// <summary>是否删除成功：<see cref="Flag"/> 非 0。</summary>
        public bool IsSuccess => Flag != 0;

        /// <summary>成功体第 2 字节：是否同步清除了活动角色标记（服务端写入 0/1，客户端非 0 时清零本地标记）；失败体恒为 0。</summary>
        public byte EventCharacClearedFlag { get; }

        /// <summary>是否同步清除了活动角色标记：<see cref="EventCharacClearedFlag"/> 非 0。</summary>
        public bool IsEventCharacterFlagCleared => EventCharacClearedFlag != 0;

        /// <summary>失败体次字节原始错误码；成功体恒为 0。</summary>
        public byte FailureCode { get; }

        /// <summary>失败码映射；成功体为 <see cref="CharacterDeleteFailure.None"/>。</summary>
        public CharacterDeleteFailure Failure { get; }

        /// <summary>读完回复字段后剩余未消费的尾字节数。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 角色名结构性规则校验器（纯函数，无外部脚本/词表 I/O）。
    /// <para>
    /// 边界说明：本类仅包含从客户端与服务端可执行体读出的结构性约束（UTF-8 字节长度、禁止 NUL、合法 UTF-8、BMP 字符范围与客户端 8 个禁用 ASCII 字符）。
    /// 服务端与客户端运行期依赖外部脚本/数据表的敏感词过滤、禁用前缀、特殊字符全集以及 Unicode 分区允许表在本机缺失，
    /// 不在此处臆造；通过结构性校验的角色名在真服仍可能收到 <c>0x9f</c>（非法名）。
    /// </para>
    /// </summary>
    public static class CharacterNameRules
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>原版常规创角客户端路径（0x01100262..0x0110033c）显式拒绝的 8 个 ASCII 字节：<c>'</c>、空格、<c>\t</c>、<c>\</c>、<c>%</c>、<c>&lt;</c>、<c>&gt;</c>、<c>"</c>。</summary>
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

        /// <summary>判断单个字节是否属于客户端常规创角拒绝的 8 个 ASCII 字符之一。</summary>
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

        /// <summary>
        /// 按结构性规则校验用于创建角色（<c>1/5</c>）的 UTF-8 角色名字节序列：
        /// 非 null、字节数 4..18（含）、不含 NUL（0x00）、严格合法 UTF-8、仅限 BMP 码点（不含 4 字节 UTF-8）、
        /// 且不含客户端常规路径拒绝的 8 个 ASCII 字符。
        /// </summary>
        public static CharacterNameValidationError ValidateCreateName(byte[] utf8Bytes)
        {
            CharacterNameValidationError coreError = ValidateWireNameCore(
                utf8Bytes,
                CharacterManagePayloads.MinCreateNameByteLength,
                CharacterManagePayloads.MaxCreateNameByteLength);
            if (coreError != CharacterNameValidationError.Valid)
            {
                return coreError;
            }

            // 经 StrictUtf8 验通的 UTF-8 序列中，大于 U+FFFF 的非 BMP 码点当且仅当首字节 >= 0xF0（4 字节 UTF-8）。
            for (int i = 0; i < utf8Bytes.Length; i++)
            {
                if (utf8Bytes[i] >= 0xF0)
                {
                    return CharacterNameValidationError.NonBmpCharacter;
                }
            }

            for (int i = 0; i < utf8Bytes.Length; i++)
            {
                if (IsForbiddenCreateAsciiByte(utf8Bytes[i]))
                {
                    return CharacterNameValidationError.ForbiddenAsciiCharacter;
                }
            }

            return CharacterNameValidationError.Valid;
        }

        /// <summary>
        /// 按结构性规则校验用于删除确认（<c>1/6</c>）的 UTF-8 角色名字节序列：
        /// 非 null、字节数 1..29（含，服务端 <c>get_str(..., 0x1e, len)</c> 要求长度严格小于 30 且非空）、不含 NUL（0x00）、严格合法 UTF-8。
        /// 不额外限制客户端创角时的 8 个 ASCII 字符或 BMP，以便准确回传服务端既有角色名。
        /// </summary>
        public static CharacterNameValidationError ValidateDeleteConfirmationName(byte[] utf8Bytes)
        {
            return ValidateWireNameCore(
                utf8Bytes,
                CharacterManagePayloads.MinDeleteNameByteLength,
                CharacterManagePayloads.MaxDeleteNameByteLength);
        }

        internal static bool IsValidWireNameBytes(byte[] nameBytes, int minLength, int maxLength)
        {
            if (nameBytes == null || nameBytes.Length < minLength || nameBytes.Length > maxLength)
            {
                return false;
            }

            for (int i = 0; i < nameBytes.Length; i++)
            {
                if (nameBytes[i] == 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static CharacterNameValidationError ValidateWireNameCore(
            byte[] utf8Bytes,
            int minLength,
            int maxLength)
        {
            if (utf8Bytes == null)
            {
                return CharacterNameValidationError.Null;
            }
            if (utf8Bytes.Length < minLength)
            {
                return CharacterNameValidationError.TooShort;
            }
            if (utf8Bytes.Length > maxLength)
            {
                return CharacterNameValidationError.TooLong;
            }

            for (int i = 0; i < utf8Bytes.Length; i++)
            {
                if (utf8Bytes[i] == 0)
                {
                    return CharacterNameValidationError.ContainsNullByte;
                }
            }

            try
            {
                StrictUtf8.GetCharCount(utf8Bytes);
            }
            catch (DecoderFallbackException)
            {
                return CharacterNameValidationError.InvalidUtf8;
            }

            return CharacterNameValidationError.Valid;
        }
    }

    /// <summary>
    /// 创建角色（类 1、包号 <see cref="CreateCharacterPacketId"/>，<c>CREATE_CHARACTER</c>）与
    /// 删除角色（类 1、包号 <see cref="DeleteCharacterPacketId"/>，<c>DELETE_CHARACTER</c>）的纯 Net 层载荷构造与解析器。
    /// <para>
    /// 交互闭环说明：
    /// 1) 独立“名字查重”包在角色创建链路中<b>不存在</b>（客户端仅在发送 1/5 前做本地检查，查重在服务端 1/5 异步数据库任务内完成；
    /// 邮件界面的名字检查按钮与角色创建无关）。
    /// 2) 当 <c>(1, 5)</c> 或 <c>(1, 6)</c> 服务端处理成功时，服务端会立即下发全量 <c>(0, 2) USERINFO</c> 模式 2 名册通知以刷新客户端列表。
    /// 3) 删除角色时必须原样提交选中槽位角色的完整名字字节串供服务端 <c>strncmp</c> 精确确认（并非二级密码或空串）。
    /// 4) 改名（<c>1/138</c>）、栏位扩展效果（<c>1/415</c>）仅在本类中记录包号常量，恢复已删角色协议在服务端未实现。
    /// </para>
    /// </summary>
    public static class CharacterManagePayloads
    {
        /// <summary>创建角色命令包号（类 1，即 <see cref="PacketClass.Cmd"/>，0x005）。</summary>
        public const ushort CreateCharacterPacketId = 5;

        /// <summary>删除角色命令包号（类 1，即 <see cref="PacketClass.Cmd"/>，0x006）。</summary>
        public const ushort DeleteCharacterPacketId = 6;

        /// <summary>成功创建或删除后服务端随之全量重发的选角名册通知包号（类 0，即 <see cref="PacketClass.Noti"/>，0x002，模式 2）。</summary>
        public const int UserInfoPacketId = 2;

        /// <summary>角色改名命令包号（类 1，0x08a；本线只记入口，不做构造与解析）。</summary>
        public const ushort ChangeCharacterNamePacketId = 138;

        /// <summary>角色栏位扩展效果命令包号（类 1，0x19f；本线只记入口，不做构造与解析）。</summary>
        public const ushort SlotExtendEffectPacketId = 415;

        /// <summary>创建角色请求中允许的最大职业编号（服务端与客户端界面均限定 0..10，拒绝 11 及负值）。</summary>
        public const byte MaxCreateJobId = 10;

        /// <summary>创建角色请求中活动标志字节允许的最大值（服务端校验有符号非负，即 0..127）。</summary>
        public const byte MaxEventFlag = 127;

        /// <summary>创建角色名字 UTF-8 字节最小长度（两端均为 4）。</summary>
        public const int MinCreateNameByteLength = 4;

        /// <summary>创建角色名字 UTF-8 字节最大长度（两端均为 18 / 0x12）。</summary>
        public const int MaxCreateNameByteLength = 18;

        /// <summary>服务端读取创建角色名使用的 <c>get_str</c> 缓冲大小（0x1d = 29 字节）。</summary>
        public const int CreateNameServerBufferSize = 29;

        /// <summary>删除角色确认名最小字节长度（1 字节，不能为空串）。</summary>
        public const int MinDeleteNameByteLength = 1;

        /// <summary>删除角色确认名最大字节长度（29 字节，因 <c>get_str</c> 缓冲大小为 0x1e = 30）。</summary>
        public const int MaxDeleteNameByteLength = 29;

        /// <summary>服务端读取删除确认名使用的 <c>get_str</c> 缓冲大小（0x1e = 30 字节）。</summary>
        public const int DeleteNameServerBufferSize = 30;

        /// <summary>服务端硬上限角色槽位数（0x24 = 36）。</summary>
        public const int MaxServerCharacterSlots = 36;

        /// <summary>
        /// 构造 <c>1/5 CREATE_CHARACTER</c> 请求载荷（小端：<c>u8 job, i32 nameLen, byte[nameLen] nameBytes, u8 eventFlag</c>）。
        /// </summary>
        /// <param name="job">基础职业编号（0..10）。</param>
        /// <param name="nameBytes">角色名 UTF-8 字节序列（4..18 字节，且不能包含 0x00）；会进行值拷贝。</param>
        /// <param name="eventFlag">活动/跳跃角色标志（0..127，常规创建为 0）。</param>
        /// <returns>编码后的请求明文载荷。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="nameBytes"/> 为 null。</exception>
        /// <exception cref="ArgumentException">职业越界、活动标志越界或名字字节长度/内容不满足线上格式。</exception>
        public static byte[] BuildCreateCharacter(byte job, byte[] nameBytes, byte eventFlag = 0)
        {
            if (nameBytes == null)
            {
                throw new ArgumentNullException(nameof(nameBytes));
            }
            if (!TryBuildCreateCharacter(job, nameBytes, out byte[] payload, eventFlag))
            {
                throw new ArgumentException("Invalid create-character wire arguments.", nameof(nameBytes));
            }
            return payload;
        }

        /// <summary>
        /// 尝试构造 <c>1/5 CREATE_CHARACTER</c> 请求载荷（小端：<c>u8 job, i32 nameLen, byte[nameLen] nameBytes, u8 eventFlag</c>）。
        /// <para>校验线上传输硬条件：职业 0..10、名字 4..18 字节且不含 0x00、活动标志 0..127。</para>
        /// </summary>
        public static bool TryBuildCreateCharacter(byte job, byte[] nameBytes, out byte[] payload, byte eventFlag = 0)
        {
            payload = null;
            if (job > MaxCreateJobId
                || eventFlag > MaxEventFlag
                || !CharacterNameRules.IsValidWireNameBytes(nameBytes, MinCreateNameByteLength, MaxCreateNameByteLength))
            {
                return false;
            }

            var writer = new PacketWriter(0);
            writer.PutByte(job);
            writer.PutInt(nameBytes.Length);
            writer.PutBinary(nameBytes);
            writer.PutByte(eventFlag);
            payload = writer.ToArray();
            return true;
        }

        /// <summary>
        /// 解析 <c>1/5 CREATE_CHARACTER</c> 上行请求明文载荷（互证构造往返用）。
        /// </summary>
        public static bool TryParseCreateCharacterRequest(byte[] payload, out CreateCharacterRequest request)
        {
            request = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte job)
                || job > MaxCreateJobId
                || !reader.TryGetInt(out int nameLength)
                || nameLength < MinCreateNameByteLength
                || nameLength > MaxCreateNameByteLength
                || !reader.TryGetString(CreateNameServerBufferSize, nameLength, out byte[] nameBytes)
                || nameBytes.Length != nameLength
                || !reader.TryGetByte(out byte eventFlag)
                || eventFlag > MaxEventFlag)
            {
                return false;
            }

            request = new CreateCharacterRequest(job, nameBytes, eventFlag, reader.Remaining);
            return true;
        }

        /// <summary>
        /// 解析 <c>(1, 5) CREATE_CHARACTER</c> 服务端回复明文载荷。
        /// <para>首字节非 0 为成功（无后续业务字节）；首字节为 0 时再读 1 字节失败码。</para>
        /// </summary>
        public static bool TryParseCreateResult(byte[] payload, out CharacterCreateResult result)
        {
            result = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte flag))
            {
                return false;
            }

            if (flag != 0)
            {
                result = new CharacterCreateResult(flag, 0, reader.Remaining);
                return true;
            }

            if (!reader.TryGetByte(out byte errorCode))
            {
                return false;
            }

            result = new CharacterCreateResult(flag, errorCode, reader.Remaining);
            return true;
        }

        /// <summary>将 <c>(1, 5)</c> 失败体次字节映射为 <see cref="CharacterCreateFailure"/>。</summary>
        public static CharacterCreateFailure MapCreateFailureCode(byte failureCode)
        {
            switch (failureCode)
            {
                case 0x02:
                    return CharacterCreateFailure.DatabaseOrUnknownError;
                case 0x04:
                    return CharacterCreateFailure.SlotLimitReached;
                case 0x05:
                    return CharacterCreateFailure.ClientRestricted;
                case 0x07:
                    return CharacterCreateFailure.CreationRestrictedEmptyAccount;
                case 0x08:
                    return CharacterCreateFailure.CreationRestrictedByEvent;
                case 0x12:
                    return CharacterCreateFailure.DuplicateActiveName;
                case 0x13:
                    return CharacterCreateFailure.StateLocked;
                case 0x14:
                    return CharacterCreateFailure.EventCharacterConditionFailed;
                case 0x16:
                    return CharacterCreateFailure.ClientSilentIgnored;
                case 0x17:
                    return CharacterCreateFailure.CreationBlocked;
                case 0x18:
                    return CharacterCreateFailure.DuplicateDeletedName;
                case 0x5f:
                    return CharacterCreateFailure.DailyCreationLimitReached;
                case 0x60:
                    return CharacterCreateFailure.ChannelNotAllowed;
                case 0x9f:
                    return CharacterCreateFailure.InvalidName;
                case 0xd1:
                    return CharacterCreateFailure.ServiceRestricted;
                default:
                    return CharacterCreateFailure.Unknown;
            }
        }

        /// <summary>
        /// 构造 <c>1/6 DELETE_CHARACTER</c> 请求载荷（小端：<c>u8 slotIndex, i32 nameLen, byte[nameLen] nameBytes</c>）。
        /// </summary>
        /// <param name="slotIndex">名册槽位索引。</param>
        /// <param name="nameBytes">待删角色的精确确认名字节序列（1..29 字节，不含 0x00）；会进行值拷贝。</param>
        /// <returns>编码后的请求明文载荷。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="nameBytes"/> 为 null。</exception>
        /// <exception cref="ArgumentException">名字字节长度不在 1..29 内或包含 0x00。</exception>
        public static byte[] BuildDeleteCharacter(byte slotIndex, byte[] nameBytes)
        {
            if (nameBytes == null)
            {
                throw new ArgumentNullException(nameof(nameBytes));
            }
            if (!TryBuildDeleteCharacter(slotIndex, nameBytes, out byte[] payload))
            {
                throw new ArgumentException("Invalid delete-character confirmation name wire bytes.", nameof(nameBytes));
            }
            return payload;
        }

        /// <summary>
        /// 尝试构造 <c>1/6 DELETE_CHARACTER</c> 请求载荷（小端：<c>u8 slotIndex, i32 nameLen, byte[nameLen] nameBytes</c>）。
        /// </summary>
        public static bool TryBuildDeleteCharacter(byte slotIndex, byte[] nameBytes, out byte[] payload)
        {
            payload = null;
            if (!CharacterNameRules.IsValidWireNameBytes(nameBytes, MinDeleteNameByteLength, MaxDeleteNameByteLength))
            {
                return false;
            }

            var writer = new PacketWriter(0);
            writer.PutByte(slotIndex);
            writer.PutInt(nameBytes.Length);
            writer.PutBinary(nameBytes);
            payload = writer.ToArray();
            return true;
        }

        /// <summary>
        /// 解析 <c>1/6 DELETE_CHARACTER</c> 上行请求明文载荷（互证构造往返用）。
        /// </summary>
        public static bool TryParseDeleteCharacterRequest(byte[] payload, out DeleteCharacterRequest request)
        {
            request = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte slotIndex)
                || !reader.TryGetInt(out int nameLength)
                || nameLength < MinDeleteNameByteLength
                || nameLength > MaxDeleteNameByteLength
                || !reader.TryGetString(DeleteNameServerBufferSize, nameLength, out byte[] nameBytes)
                || nameBytes.Length != nameLength)
            {
                return false;
            }

            request = new DeleteCharacterRequest(slotIndex, nameBytes, reader.Remaining);
            return true;
        }

        /// <summary>
        /// 解析 <c>(1, 6) DELETE_CHARACTER</c> 服务端回复明文载荷。
        /// <para>首字节非 0 为成功，紧跟 1 字节活动角色清理标记；首字节为 0 时紧跟 1 字节失败码。</para>
        /// </summary>
        public static bool TryParseDeleteResult(byte[] payload, out CharacterDeleteResult result)
        {
            result = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte flag))
            {
                return false;
            }

            if (flag != 0)
            {
                if (!reader.TryGetByte(out byte eventClearedFlag))
                {
                    return false;
                }
                result = new CharacterDeleteResult(flag, eventClearedFlag, 0, reader.Remaining);
                return true;
            }

            if (!reader.TryGetByte(out byte errorCode))
            {
                return false;
            }

            result = new CharacterDeleteResult(flag, 0, errorCode, reader.Remaining);
            return true;
        }

        /// <summary>将 <c>(1, 6)</c> 失败体次字节映射为 <see cref="CharacterDeleteFailure"/>。</summary>
        public static CharacterDeleteFailure MapDeleteFailureCode(byte failureCode)
        {
            switch (failureCode)
            {
                case 0x02:
                    return CharacterDeleteFailure.DatabaseOrUnknownError;
                case 0x13:
                    return CharacterDeleteFailure.StateLocked;
                case 0x15:
                    return CharacterDeleteFailure.EmptySlot;
                case 0x16:
                    return CharacterDeleteFailure.CooldownOrActiveCharacter;
                case 0x24:
                    return CharacterDeleteFailure.GuildMasterCannotDelete;
                case 0x28:
                    return CharacterDeleteFailure.ClientDeletionBlocked;
                case 0x72:
                    return CharacterDeleteFailure.TradePunished;
                case 0xcf:
                    return CharacterDeleteFailure.RestingUserRestricted;
                case 0xd1:
                    return CharacterDeleteFailure.ServiceRestricted;
                case 0xd5:
                    return CharacterDeleteFailure.ItemLockActive;
                case 0xe4:
                    return CharacterDeleteFailure.TradePunishRestricted;
                case 0xe5:
                    return CharacterDeleteFailure.RestorePunishRestricted;
                case 0xe9:
                    return CharacterDeleteFailure.SecurityServiceRequired;
                case 0x4d:
                case 0x73:
                case 0x78:
                case 0x7a:
                case 0x7b:
                case 0x7c:
                case 0x88:
                case 0x89:
                case 0x8a:
                case 0xa7:
                case 0xec:
                case 0xee:
                    return CharacterDeleteFailure.SharedSecurityRestriction;
                default:
                    return CharacterDeleteFailure.Unknown;
            }
        }
    }
}
