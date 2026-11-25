using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>叠画在选角界面之上的角色创建/删除模态面板契约。</summary>
    public interface ICharacterManageModal
    {
        void Advance(double deltaMs);
        void Type(string text);
        bool Key(OnlineShellKey key);
        bool Click(int x, int y);
        void BuildInto(List<SceneSprite> into, ref int order, long timeMs);
    }

    /// <summary>创建/删除模态面板提交或取消意图。</summary>
    public enum CharacterManageModalAction
    {
        None = 0,
        Submit = 1,
        Cancel = 2,
    }

    /// <summary>
    /// 角色创建/删除失败码与本地校验的自拟文案表（不抄原版字符串）。
    /// 无字体时只使用点阵字库支持的英文大写字符、数字与基础符号。
    /// </summary>
    public static class OnlineCharacterManageText
    {
        public static string Message(OnlineCharacterManageStatus status, bool cjk, byte? rawErrorCode = null)
        {
            switch (status)
            {
                case OnlineCharacterManageStatus.Disabled:
                    return cjk ? "角色写操作未开启" : "CHARACTER WRITE DISABLED";
                case OnlineCharacterManageStatus.InvalidInput:
                    return cjk ? "输入内容不符合规则" : "INVALID CHARACTER INPUT";
                case OnlineCharacterManageStatus.Cancelled:
                    return cjk ? "操作已取消" : "OPERATION CANCELLED";
                case OnlineCharacterManageStatus.Disconnected:
                    return cjk ? "连接已断开，请重新登录" : "DISCONNECTED FROM SERVER";
                case OnlineCharacterManageStatus.NoReply:
                    return cjk ? "等待服务器响应超时" : "SERVER REPLY TIMEOUT";
                case OnlineCharacterManageStatus.ProtocolError:
                    return cjk ? "服务器响应格式异常" : "SERVER PROTOCOL ERROR";
                case OnlineCharacterManageStatus.RosterMissing:
                    return cjk ? "操作已执行，但未收到刷新列表" : "NO REFRESHED ROSTER RECEIVED";
                case OnlineCharacterManageStatus.RosterMalformed:
                    return cjk ? "操作已执行，但刷新列表格式异常" : "MALFORMED REFRESHED ROSTER";
                case OnlineCharacterManageStatus.DatabaseOrUnknownError:
                    return cjk ? "服务器处理失败，请稍后再试" : "SERVER FAILED TO PROCESS";
                case OnlineCharacterManageStatus.SlotLimitReached:
                    return cjk ? "角色栏位已满（最多 36 个）" : "CHARACTER SLOTS FULL";
                case OnlineCharacterManageStatus.ClientRestricted:
                    return cjk ? "当前受限，无法创建角色" : "CREATION RESTRICTED";
                case OnlineCharacterManageStatus.CreationRestrictedEmptyAccount:
                    return cjk ? "空账号活动规则限制了创建" : "EMPTY ACCOUNT CREATION RESTRICTED";
                case OnlineCharacterManageStatus.CreationRestrictedByEvent:
                    return cjk ? "活动规则限制了角色创建" : "EVENT CREATION RESTRICTED";
                case OnlineCharacterManageStatus.DuplicateActiveName:
                    return cjk ? "该角色名已被使用" : "CHARACTER NAME ALREADY EXISTS";
                case OnlineCharacterManageStatus.StateLocked:
                    return cjk ? "账号状态忙碌或被锁定，请稍后再试" : "ACCOUNT STATE LOCKED";
                case OnlineCharacterManageStatus.EventCharacterConditionFailed:
                    return cjk ? "活动角色条件不满足" : "EVENT CHARACTER CONDITION FAILED";
                case OnlineCharacterManageStatus.ClientSilentIgnored:
                    return cjk ? "当前无法创建该角色" : "CANNOT CREATE CHARACTER";
                case OnlineCharacterManageStatus.CreationBlocked:
                    return cjk ? "该渠道或当前状态禁止创建角色" : "CHARACTER CREATION BLOCKED";
                case OnlineCharacterManageStatus.DuplicateDeletedName:
                    return cjk ? "该名字与近期删除的角色冲突" : "NAME CONFLICTS WITH DELETED ROLE";
                case OnlineCharacterManageStatus.DailyCreationLimitReached:
                    return cjk ? "今日或上限创建次数已达限制" : "CREATION LIMIT REACHED";
                case OnlineCharacterManageStatus.ChannelNotAllowed:
                    return cjk ? "当前频道不允许创建角色" : "CHANNEL NOT ALLOWED";
                case OnlineCharacterManageStatus.InvalidName:
                    return cjk ? "角色名未通过服务器检查" : "CHARACTER NAME REJECTED";
                case OnlineCharacterManageStatus.ServiceRestricted:
                    return cjk ? "安全服务受限，无法执行此操作" : "SERVICE RESTRICTED";
                case OnlineCharacterManageStatus.EmptySlot:
                    return cjk ? "目标槽位没有可删除的角色" : "TARGET SLOT IS EMPTY";
                case OnlineCharacterManageStatus.CooldownOrActiveCharacter:
                    return cjk ? "新建冷却中或不可删除当前角色" : "CHARACTER IN COOLDOWN OR ACTIVE";
                case OnlineCharacterManageStatus.GuildMasterCannotDelete:
                    return cjk ? "公会会长角色不能直接删除" : "GUILD MASTER CANNOT BE DELETED";
                case OnlineCharacterManageStatus.ClientDeletionBlocked:
                    return cjk ? "当前禁止删除角色" : "CHARACTER DELETION BLOCKED";
                case OnlineCharacterManageStatus.TradePunished:
                    return cjk ? "账号处于交易惩罚保护中" : "ACCOUNT UNDER TRADE PUNISHMENT";
                case OnlineCharacterManageStatus.RestingUserRestricted:
                    return cjk ? "回归休眠保护期间不可删除" : "RESTING ACCOUNT RESTRICTED";
                case OnlineCharacterManageStatus.ItemLockActive:
                    return cjk ? "存在物品锁定期，暂不可删除" : "ITEM LOCK ACTIVE";
                case OnlineCharacterManageStatus.TradePunishRestricted:
                    return cjk ? "交易限制保护期间不可删除" : "TRADE PUNISH RESTRICTED";
                case OnlineCharacterManageStatus.RestorePunishRestricted:
                    return cjk ? "账号恢复保护期间不可删除" : "RESTORE PROTECTION RESTRICTED";
                case OnlineCharacterManageStatus.SecurityServiceRequired:
                    return cjk ? "需要启用安全服务后才可删除" : "SECURITY SERVICE REQUIRED";
                case OnlineCharacterManageStatus.SharedSecurityRestriction:
                    return cjk ? "账号安全限制，暂不可执行" : "ACCOUNT SECURITY RESTRICTION";
                case OnlineCharacterManageStatus.UnknownError:
                    string hex = rawErrorCode.HasValue
                        ? "0X" + rawErrorCode.Value.ToString("X2", CultureInfo.InvariantCulture)
                        : string.Empty;
                    return cjk
                        ? (hex.Length > 0 ? "服务器返回未知错误（" + hex + "）" : "服务器返回未知错误")
                        : (hex.Length > 0 ? "UNKNOWN SERVER ERROR " + hex : "UNKNOWN SERVER ERROR");
                default:
                    return string.Empty;
            }
        }

        public static string NameError(OnlineCharacterNameError error, bool cjk)
        {
            switch (error)
            {
                case OnlineCharacterNameError.Null:
                case OnlineCharacterNameError.TooShort:
                    return cjk ? "角色名至少需要 4 个 UTF-8 字节" : "NAME NEEDS AT LEAST 4 BYTES";
                case OnlineCharacterNameError.TooLong:
                    return cjk ? "角色名不能超过 18 个 UTF-8 字节" : "NAME EXCEEDS 18 BYTES";
                case OnlineCharacterNameError.ContainsNullByte:
                    return cjk ? "角色名不能包含空字符" : "NAME CANNOT CONTAIN NUL";
                case OnlineCharacterNameError.InvalidUtf8:
                    return cjk ? "角色名编码无效" : "INVALID CHARACTER ENCODING";
                case OnlineCharacterNameError.NonBmpCharacter:
                    return cjk ? "不支持超出基本多文种平面的字符" : "NON-BMP CHARACTER NOT ALLOWED";
                case OnlineCharacterNameError.ForbiddenAsciiCharacter:
                    return cjk ? "不能包含空格、引号、尖括号、斜杠或%" : "CONTAINS FORBIDDEN CHARACTER";
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>
    /// 联网创建角色模态面板（11 个职业选择 + 默认站立形象预览 + 名字输入框 + 确定/取消）。
    /// 布局未对拍原版（无原版创角界面基线与读码；按自家版式实现，职业预览复用 <see cref="DnfJobAvatars"/> 默认站立动画）。
    /// </summary>
    public sealed class CharacterCreateModal : ICharacterManageModal
    {
        public const int MaxInputChars = 32;

        // ---- 布局常量（800×600 画布，自拟版式，非对拍）
        public const int CardX = 100;
        public const int CardY = 88;
        public const int CardWidth = 600;
        public const int CardHeight = 424;

        public const int JobColumns = 2;
        public const int JobGridX = 124;
        public const int JobGridY = 156;
        public const int JobCellWidth = 144;
        public const int JobCellHeight = 30;
        public const int JobPitchX = 152;
        public const int JobPitchY = 34;

        public const int PreviewX = 444;
        public const int PreviewY = 156;
        public const int PreviewWidth = 232;
        public const int PreviewHeight = 200;
        public const int PreviewFootX = 560;
        public const int PreviewFootY = 304;

        public static readonly HitRect NameBoxRect = new HitRect(124, 370, 310, 36);
        public static readonly HitRect ConfirmButtonRect = new HitRect(444, 370, 110, 36);
        public static readonly HitRect CancelButtonRect = new HitRect(566, 370, 110, 36);
        public static readonly HitRect NoticeRect = new HitRect(124, 418, 552, OnlineShellArt.NoticeBarHeight);

        private static readonly ShellBox DimStyle = new ShellBox(0xAA040812u, 0xAA040812u, 0, 0, 0);
        private static readonly ShellBox JobNormalStyle = new ShellBox(0xE0162542u, 0xE0101B32u, 0xFF3E5E92u, 0xFF2B456Eu, 1);
        private static readonly ShellBox JobSelectedStyle = new ShellBox(0xFF2D4B7Au, 0xFF1E355Au, 0xFFFFD25Eu, 0xFFE0A93Eu, 1);
        private static readonly ShellBox PreviewStyle = new ShellBox(0xE00C162Au, 0xE0091122u, 0xFF35517Du, 0xFF263B5Eu, 1);
        private static readonly ShellBox InputStyle = new ShellBox(0xFF0A1324u, 0xFF0D182Eu, 0xFFFFD25Eu, 0xFFE0A93Eu, 1);
        private static readonly ShellBox InputInvalidStyle = new ShellBox(0xFF1A1018u, 0xFF140B12u, 0xFFFF7A45u, 0xFFB84924u, 1);
        private static readonly ShellBox PrimaryStyle = new ShellBox(0xFFFFD36Bu, 0xFFE59A2Bu, 0xFFFFE9A8u, 0xFFB8741Au, 1, 36, 0);
        private static readonly ShellBox PrimaryOffStyle = new ShellBox(0xFF46536Bu, 0xFF363F52u, 0xFF5A6882u, 0xFF2F3748u, 1);
        private static readonly ShellBox SecondaryStyle = new ShellBox(0xE01A2C4Au, 0xE0132039u, 0xFF4B72ABu, 0xFF35568Au, 1, 12, 0);

        private readonly OverlayArt _art;
        private readonly ShellPaint _paint;
        private readonly ICharacterTitles _titles;
        private readonly ICharacterAvatars _avatars;
        private byte _selectedJob;
        private string _name = string.Empty;
        private double _ageMs;
        private CharacterManageModalAction _pending;
        private string _errorText;
        private bool _hasServerError;

        public CharacterCreateModal(ICharacterTitles titles = null, ICharacterAvatars avatars = null, IGlyphFonts fonts = null)
        {
            _titles = titles ?? PlaceholderTitles.Instance;
            _avatars = avatars;
            _art = new OverlayArt { Localize = false, Fonts = fonts };
            _paint = new ShellPaint(_art);
        }

        public byte SelectedJob => _selectedJob;
        public string Name => _name;
        public bool Busy { get; private set; }
        public string ErrorText => _errorText;

        public int NameByteCount
        {
            get
            {
                int count = OnlineCharacterNameRules.GetUtf8ByteCount(_name);
                return count < 0 ? 0 : count;
            }
        }

        public OnlineCharacterNameError NameError => OnlineCharacterNameRules.ValidateCreateName(_name);

        public static HitRect JobRect(int job)
        {
            int col = job % JobColumns;
            int row = job / JobColumns;
            return new HitRect(JobGridX + col * JobPitchX, JobGridY + row * JobPitchY, JobCellWidth, JobCellHeight);
        }

        public static int HitJob(int x, int y)
        {
            for (int job = 0; job <= OnlineCharacterNameRules.MaxCreateJobId; job++)
            {
                if (JobRect(job).Contains(x, y))
                {
                    return job;
                }
            }
            return -1;
        }

        public void SelectJob(byte job)
        {
            if (Busy || job > OnlineCharacterNameRules.MaxCreateJobId)
            {
                return;
            }
            _selectedJob = job;
        }

        public void MoveJob(int dx, int dy)
        {
            if (Busy)
            {
                return;
            }
            int col = _selectedJob % JobColumns;
            int row = _selectedJob / JobColumns;
            int newCol = Math.Max(0, Math.Min(JobColumns - 1, col + dx));
            int newRow = Math.Max(0, Math.Min(5, row + dy));
            int target = newRow * JobColumns + newCol;
            if (target <= OnlineCharacterNameRules.MaxCreateJobId)
            {
                _selectedJob = (byte)target;
            }
            else if (dx != 0)
            {
                _selectedJob = OnlineCharacterNameRules.MaxCreateJobId;
            }
        }

        public void Advance(double deltaMs)
        {
            if (deltaMs > 0.0)
            {
                _ageMs += deltaMs;
            }
        }

        public void Type(string text)
        {
            if (Busy || string.IsNullOrEmpty(text))
            {
                return;
            }
            bool changed = false;
            foreach (char c in text)
            {
                if (c == '\b')
                {
                    if (_name.Length > 0)
                    {
                        _name = _name.Substring(0, _name.Length - 1);
                        changed = true;
                    }
                }
                else if (c >= ' ' && c != '\u007f' && _name.Length < MaxInputChars)
                {
                    _name += c;
                    changed = true;
                }
            }
            if (changed)
            {
                _errorText = null;
                _hasServerError = false;
            }
        }

        public bool Key(OnlineShellKey key)
        {
            switch (key)
            {
                case OnlineShellKey.Left: MoveJob(-1, 0); return true;
                case OnlineShellKey.Right: MoveJob(1, 0); return true;
                case OnlineShellKey.Up: MoveJob(0, -1); return true;
                case OnlineShellKey.Down: MoveJob(0, 1); return true;
                case OnlineShellKey.Tab:
                    if (!Busy)
                    {
                        SelectJob((byte)((_selectedJob + 1) % (OnlineCharacterNameRules.MaxCreateJobId + 1)));
                    }
                    return true;
                case OnlineShellKey.ShiftTab:
                    if (!Busy)
                    {
                        int count = OnlineCharacterNameRules.MaxCreateJobId + 1;
                        SelectJob((byte)((_selectedJob + count - 1) % count));
                    }
                    return true;
                case OnlineShellKey.Enter: Submit(); return true;
                case OnlineShellKey.Escape: Cancel(); return true;
                case OnlineShellKey.Space: return true;
                default: return true;
            }
        }

        public bool Click(int x, int y)
        {
            if (CancelButtonRect.Contains(x, y))
            {
                Cancel();
                return true;
            }
            if (Busy)
            {
                return true;
            }
            if (ConfirmButtonRect.Contains(x, y))
            {
                Submit();
                return true;
            }
            int hitJob = HitJob(x, y);
            if (hitJob >= 0)
            {
                SelectJob((byte)hitJob);
                return true;
            }
            return true;
        }

        public void Submit()
        {
            if (Busy || _pending != CharacterManageModalAction.None)
            {
                return;
            }
            OnlineCharacterNameError error = NameError;
            if (error != OnlineCharacterNameError.Valid)
            {
                _errorText = OnlineCharacterManageText.NameError(error, _paint.HasFonts);
                _hasServerError = false;
                return;
            }
            _errorText = null;
            _hasServerError = false;
            _pending = CharacterManageModalAction.Submit;
        }

        public void Cancel()
        {
            _pending = CharacterManageModalAction.Cancel;
        }

        public CharacterManageModalAction TakeAction()
        {
            CharacterManageModalAction action = _pending;
            _pending = CharacterManageModalAction.None;
            return action;
        }

        public void SetBusy(bool busy)
        {
            Busy = busy;
            if (busy)
            {
                _errorText = null;
                _hasServerError = false;
            }
        }

        public void SetFailure(OnlineCharacterManageStatus status, byte? rawErrorCode = null)
        {
            Busy = false;
            _errorText = OnlineCharacterManageText.Message(status, _paint.HasFonts, rawErrorCode);
            _hasServerError = true;
        }

        public string JobLabel(int job)
        {
            return _titles.ClassName(job, 0, 0) ?? OnlineCharacterRoster.PlaceholderClassName(job, 0, 0);
        }

        public void BuildInto(List<SceneSprite> into, ref int order, long timeMs)
        {
            bool cjk = _paint.HasFonts;
            OnlineShellArt.Add(into, ref order, _paint.Box(OnlineShellArt.ScreenWidth, OnlineShellArt.ScreenHeight, 0, DimStyle), 0, 0, "create-modal-dim");
            OnlineShellArt.AddCard(into, ref order, _paint, CardX, CardY, CardWidth, CardHeight, "create-modal-panel");

            string title = cjk ? "创建角色" : "CREATE CHARACTER";
            OnlineShellArt.Add(into, ref order, _paint.Line(title, 22, ShellPaint.Ink, ShellPaint.Navy, CardWidth - 48, true),
                CardX + 24, CardY + 18, "create-modal-title");
            OnlineShellArt.Add(into, ref order, _paint.Rule(CardWidth - 48, 1, 0x7FA6E0u, 150, 60, 0),
                CardX + 24, CardY + 54, "create-modal-divider");

            for (int job = 0; job <= OnlineCharacterNameRules.MaxCreateJobId; job++)
            {
                HitRect cell = JobRect(job);
                bool selected = job == _selectedJob;
                OnlineShellArt.Add(into, ref order,
                    _paint.Box(cell.Width, cell.Height, 6, selected ? JobSelectedStyle : JobNormalStyle),
                    cell.X, cell.Y, selected ? "create-modal-job-selected" : "create-modal-job");
                SpriteFrame label = _paint.Line(JobLabel(job), 13, selected ? ShellPaint.Gold : ShellPaint.Ink, ShellPaint.Navy, cell.Width - 12);
                if (label != null)
                {
                    OnlineShellArt.Add(into, ref order, label,
                        cell.X + (cell.Width - label.Width) / 2, cell.Y + (cell.Height - label.Height) / 2,
                        "create-modal-job-text");
                }
            }

            OnlineShellArt.Add(into, ref order, _paint.Box(PreviewWidth, PreviewHeight, 8, PreviewStyle),
                PreviewX, PreviewY, "create-modal-preview-box");
            string selectedJobName = JobLabel(_selectedJob);
            if (_avatars != null)
            {
                var dummyEntry = new OnlineCharacterEntry(
                    0, Array.Empty<byte>(), 0, _selectedJob, 0, 1, 0, 0,
                    Array.Empty<OnlineCharacterEquip>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                var previewSlot = new CharacterSlot(
                    string.Empty, selectedJobName, 1,
                    new CharacterSlotOnline(0, dummyEntry, _selectedJob, 0, 0, CharacterBlock.None));
                long animMs = (long)_ageMs > 0 ? (long)_ageMs : timeMs;
                foreach (SceneSprite s in _avatars.Pose(previewSlot, animMs, PreviewFootX, PreviewFootY))
                {
                    into.Add(new SceneSprite(
                        s.Frame, s.ScreenX, s.ScreenY, order++, -3, "create-modal-avatar",
                        s.AniFrame, s.Mirrored, s.Blend, s.TintAlpha, OverlayArt.UiRank));
                }
            }
            SpriteFrame previewLabel = _paint.Line(selectedJobName, 15, ShellPaint.GoldPale, ShellPaint.Navy, PreviewWidth - 16, true);
            if (previewLabel != null)
            {
                OnlineShellArt.Add(into, ref order, previewLabel,
                    PreviewX + (PreviewWidth - previewLabel.Width) / 2, PreviewY + PreviewHeight - 28,
                    "create-modal-preview-job");
            }

            OnlineCharacterNameError validation = NameError;
            bool showInvalidBox = _name.Length > 0 && validation != OnlineCharacterNameError.Valid;
            OnlineShellArt.Add(into, ref order,
                _paint.Box(NameBoxRect.Width, NameBoxRect.Height, 7, showInvalidBox ? InputInvalidStyle : InputStyle),
                NameBoxRect.X, NameBoxRect.Y, "create-modal-name-box");
            if (_name.Length > 0)
            {
                SpriteFrame nameFrame = _paint.Data(_name, 16, ShellPaint.Ink, ShellPaint.Navy, NameBoxRect.Width - 20);
                if (nameFrame != null)
                {
                    OnlineShellArt.Add(into, ref order, nameFrame,
                        NameBoxRect.X + 10, NameBoxRect.Y + (NameBoxRect.Height - nameFrame.Height) / 2,
                        "create-modal-name-text");
                }
            }
            else
            {
                string ph = cjk ? "请输入角色名（4-18字节）" : "NAME (4-18 BYTES)";
                SpriteFrame phFrame = _paint.Line(ph, 14, ShellPaint.Faint, ShellPaint.Navy, NameBoxRect.Width - 20);
                if (phFrame != null)
                {
                    OnlineShellArt.Add(into, ref order, phFrame,
                        NameBoxRect.X + 10, NameBoxRect.Y + (NameBoxRect.Height - phFrame.Height) / 2,
                        "create-modal-name-placeholder");
                }
            }

            bool confirmDisabled = Busy;
            OnlineShellArt.Add(into, ref order,
                _paint.Box(ConfirmButtonRect.Width, ConfirmButtonRect.Height, 8, confirmDisabled ? PrimaryOffStyle : PrimaryStyle),
                ConfirmButtonRect.X, ConfirmButtonRect.Y,
                confirmDisabled ? "create-modal-confirm-disabled" : "create-modal-confirm");
            SpriteFrame confirmText = _paint.Line(cjk ? "确认创建" : "CONFIRM", 15,
                confirmDisabled ? ShellPaint.Muted : ShellPaint.Warm,
                confirmDisabled ? ShellPaint.Navy : 0xF0B640u,
                ConfirmButtonRect.Width - 12, true);
            if (confirmText != null)
            {
                OnlineShellArt.Add(into, ref order, confirmText,
                    ConfirmButtonRect.X + (ConfirmButtonRect.Width - confirmText.Width) / 2,
                    ConfirmButtonRect.Y + (ConfirmButtonRect.Height - confirmText.Height) / 2,
                    "create-modal-confirm-text");
            }

            OnlineShellArt.Add(into, ref order,
                _paint.Box(CancelButtonRect.Width, CancelButtonRect.Height, 8, SecondaryStyle),
                CancelButtonRect.X, CancelButtonRect.Y, "create-modal-cancel");
            SpriteFrame cancelText = _paint.Line(cjk ? "取消" : "CANCEL", 15, ShellPaint.Ink, ShellPaint.Navy, CancelButtonRect.Width - 12);
            if (cancelText != null)
            {
                OnlineShellArt.Add(into, ref order, cancelText,
                    CancelButtonRect.X + (CancelButtonRect.Width - cancelText.Width) / 2,
                    CancelButtonRect.Y + (CancelButtonRect.Height - cancelText.Height) / 2,
                    "create-modal-cancel-text");
            }

            if (Busy)
            {
                string waitText = cjk ? "正在创建角色…" : "CREATING CHARACTER...";
                OnlineShellArt.AddNoticeBar(into, ref order, _paint, ShellTone.Wait, waitText,
                    NoticeRect.X, NoticeRect.Y, NoticeRect.Width, timeMs, "create-modal-busy");
            }
            else if (!string.IsNullOrEmpty(_errorText))
            {
                OnlineShellArt.AddNoticeBar(into, ref order, _paint, ShellTone.Error, _errorText,
                    NoticeRect.X, NoticeRect.Y, NoticeRect.Width, timeMs,
                    _hasServerError ? "create-modal-error" : "create-modal-validation-error");
            }
            else
            {
                int bytes = NameByteCount;
                string statusLine;
                ShellTone tone;
                if (_name.Length > 0 && validation != OnlineCharacterNameError.Valid)
                {
                    tone = ShellTone.Error;
                    statusLine = OnlineCharacterManageText.NameError(validation, cjk) + " (" + bytes + "/18)";
                }
                else
                {
                    tone = validation == OnlineCharacterNameError.Valid ? ShellTone.Success : ShellTone.Info;
                    statusLine = cjk
                        ? "当前字节数：" + bytes + "/18（4-18字节，不含空格与特殊符号）"
                        : "UTF-8 BYTES: " + bytes + "/18 (4-18 BYTES)";
                }
                OnlineShellArt.AddNoticeBar(into, ref order, _paint, tone, statusLine,
                    NoticeRect.X, NoticeRect.Y, NoticeRect.Width, timeMs, "create-modal-status", false);
            }

            ShellHint[] hints =
            {
                new ShellHint(cjk ? "方向键" : "ARROWS", cjk ? "选职业" : "JOB"),
                new ShellHint(cjk ? "Enter" : "ENTER", cjk ? "确认" : "CONFIRM"),
                new ShellHint(cjk ? "Esc" : "ESC", cjk ? "取消" : "CANCEL"),
            };
            OnlineShellArt.AddHints(into, ref order, _paint, hints, OnlineShellArt.ScreenWidth / 2, CardY + CardHeight - 44, "create-modal-hint");
        }
    }

    /// <summary>
    /// 联网删除角色模态面板（显示目标角色信息 + 要求逐字输入确认名 + 确定/取消）。
    /// 布局未对拍原版（无原版删角确认模态基线；按自家版式实现）。
    /// </summary>
    public sealed class CharacterDeleteModal : ICharacterManageModal
    {
        public const int MaxInputChars = 64;
        public const int CardX = 160;
        public const int CardY = 148;
        public const int CardWidth = 480;
        public const int CardHeight = 304;

        public static readonly HitRect ConfirmBoxRect = new HitRect(184, 272, 432, 36);
        public static readonly HitRect NoticeRect = new HitRect(184, 318, 432, OnlineShellArt.NoticeBarHeight);
        public static readonly HitRect ConfirmButtonRect = new HitRect(184, 366, 208, 36);
        public static readonly HitRect CancelButtonRect = new HitRect(408, 366, 208, 36);

        private static readonly ShellBox DimStyle = new ShellBox(0xAA040812u, 0xAA040812u, 0, 0, 0);
        private static readonly ShellBox InputStyle = new ShellBox(0xFF0A1324u, 0xFF0D182Eu, 0xFFFFD25Eu, 0xFFE0A93Eu, 1);
        private static readonly ShellBox DangerStyle = new ShellBox(0xFFD94A38u, 0xFFA82C20u, 0xFFFF9A8Cu, 0xFF7D1D14u, 1, 24, 0);
        private static readonly ShellBox DisabledStyle = new ShellBox(0xFF46536Bu, 0xFF363F52u, 0xFF5A6882u, 0xFF2F3748u, 1);
        private static readonly ShellBox SecondaryStyle = new ShellBox(0xE01A2C4Au, 0xE0132039u, 0xFF4B72ABu, 0xFF35568Au, 1, 12, 0);

        private readonly OverlayArt _art;
        private readonly ShellPaint _paint;
        private string _confirmName = string.Empty;
        private string _errorText;
        private CharacterManageModalAction _pending;

        public CharacterDeleteModal(byte targetSlot, string targetName, string targetJobName, int targetLevel, IGlyphFonts fonts = null)
        {
            TargetSlot = targetSlot;
            TargetName = targetName ?? string.Empty;
            TargetJobName = targetJobName ?? string.Empty;
            TargetLevel = Math.Max(1, targetLevel);
            _art = new OverlayArt { Localize = false, Fonts = fonts };
            _paint = new ShellPaint(_art);
        }

        public byte TargetSlot { get; }
        public string TargetName { get; }
        public string TargetJobName { get; }
        public int TargetLevel { get; }
        public string ConfirmName => _confirmName;
        public bool Busy { get; private set; }
        public string ErrorText => _errorText;

        public bool IsNameMatched =>
            string.Equals(_confirmName, TargetName, StringComparison.Ordinal)
            && OnlineCharacterNameRules.ValidateDeleteConfirmationName(_confirmName) == OnlineCharacterNameError.Valid;

        public void Advance(double deltaMs)
        {
        }

        public void Type(string text)
        {
            if (Busy || string.IsNullOrEmpty(text))
            {
                return;
            }
            bool changed = false;
            foreach (char c in text)
            {
                if (c == '\b')
                {
                    if (_confirmName.Length > 0)
                    {
                        _confirmName = _confirmName.Substring(0, _confirmName.Length - 1);
                        changed = true;
                    }
                }
                else if (c >= ' ' && c != '\u007f' && _confirmName.Length < MaxInputChars)
                {
                    _confirmName += c;
                    changed = true;
                }
            }
            if (changed)
            {
                _errorText = null;
            }
        }

        public bool Key(OnlineShellKey key)
        {
            switch (key)
            {
                case OnlineShellKey.Enter: Submit(); return true;
                case OnlineShellKey.Escape: Cancel(); return true;
                default: return true;
            }
        }

        public bool Click(int x, int y)
        {
            if (CancelButtonRect.Contains(x, y))
            {
                Cancel();
                return true;
            }
            if (!Busy && ConfirmButtonRect.Contains(x, y))
            {
                Submit();
                return true;
            }
            return true;
        }

        public void Submit()
        {
            if (Busy || _pending != CharacterManageModalAction.None)
            {
                return;
            }
            if (!IsNameMatched)
            {
                _errorText = _paint.HasFonts
                    ? "输入的确认名与目标角色名不一致"
                    : "CONFIRMATION NAME DOES NOT MATCH";
                return;
            }
            _errorText = null;
            _pending = CharacterManageModalAction.Submit;
        }

        public void Cancel()
        {
            _pending = CharacterManageModalAction.Cancel;
        }

        public CharacterManageModalAction TakeAction()
        {
            CharacterManageModalAction action = _pending;
            _pending = CharacterManageModalAction.None;
            return action;
        }

        public void SetBusy(bool busy)
        {
            Busy = busy;
            if (busy)
            {
                _errorText = null;
            }
        }

        public void SetFailure(OnlineCharacterManageStatus status, byte? rawErrorCode = null)
        {
            Busy = false;
            _errorText = OnlineCharacterManageText.Message(status, _paint.HasFonts, rawErrorCode);
        }

        public void BuildInto(List<SceneSprite> into, ref int order, long timeMs)
        {
            bool cjk = _paint.HasFonts;
            OnlineShellArt.Add(into, ref order, _paint.Box(OnlineShellArt.ScreenWidth, OnlineShellArt.ScreenHeight, 0, DimStyle), 0, 0, "delete-modal-dim");
            OnlineShellArt.AddCard(into, ref order, _paint, CardX, CardY, CardWidth, CardHeight, "delete-modal-panel");

            string title = cjk ? "删除角色确认" : "DELETE CHARACTER";
            OnlineShellArt.Add(into, ref order, _paint.Line(title, 22, ShellPaint.Ink, ShellPaint.Navy, CardWidth - 48, true),
                CardX + 24, CardY + 18, "delete-modal-title");

            string info = "Lv" + TargetLevel + " " + TargetName + "  (" + TargetJobName + ")";
            OnlineShellArt.Add(into, ref order, _paint.Data(info, 16, ShellPaint.Gold, ShellPaint.Navy, CardWidth - 48),
                CardX + 24, CardY + 60, "delete-modal-target");

            string prompt = cjk ? "删除可能不可恢复，请逐字输入角色名确认：" : "TYPE EXACT CHARACTER NAME TO CONFIRM:";
            OnlineShellArt.Add(into, ref order, _paint.Line(prompt, 14, ShellPaint.Muted, ShellPaint.Navy, CardWidth - 48),
                CardX + 24, CardY + 92, "delete-modal-prompt");

            OnlineShellArt.Add(into, ref order, _paint.Box(ConfirmBoxRect.Width, ConfirmBoxRect.Height, 7, InputStyle),
                ConfirmBoxRect.X, ConfirmBoxRect.Y, "delete-modal-input-box");
            if (_confirmName.Length > 0)
            {
                SpriteFrame typed = _paint.Data(_confirmName, 16, ShellPaint.Ink, ShellPaint.Navy, ConfirmBoxRect.Width - 20);
                if (typed != null)
                {
                    OnlineShellArt.Add(into, ref order, typed,
                        ConfirmBoxRect.X + 10, ConfirmBoxRect.Y + (ConfirmBoxRect.Height - typed.Height) / 2,
                        "delete-modal-input-text");
                }
            }
            else
            {
                string ph = cjk ? "请输入要删除的角色名" : "TYPE CHARACTER NAME";
                SpriteFrame phFrame = _paint.Line(ph, 14, ShellPaint.Faint, ShellPaint.Navy, ConfirmBoxRect.Width - 20);
                if (phFrame != null)
                {
                    OnlineShellArt.Add(into, ref order, phFrame,
                        ConfirmBoxRect.X + 10, ConfirmBoxRect.Y + (ConfirmBoxRect.Height - phFrame.Height) / 2,
                        "delete-modal-input-placeholder");
                }
            }

            if (Busy)
            {
                OnlineShellArt.AddNoticeBar(into, ref order, _paint, ShellTone.Wait,
                    cjk ? "正在删除角色…" : "DELETING CHARACTER...",
                    NoticeRect.X, NoticeRect.Y, NoticeRect.Width, timeMs, "delete-modal-busy");
            }
            else if (!string.IsNullOrEmpty(_errorText))
            {
                OnlineShellArt.AddNoticeBar(into, ref order, _paint, ShellTone.Error, _errorText,
                    NoticeRect.X, NoticeRect.Y, NoticeRect.Width, timeMs, "delete-modal-error");
            }

            OnlineShellArt.Add(into, ref order,
                _paint.Box(ConfirmButtonRect.Width, ConfirmButtonRect.Height, 8, Busy ? DisabledStyle : DangerStyle),
                ConfirmButtonRect.X, ConfirmButtonRect.Y,
                Busy ? "delete-modal-confirm-disabled" : "delete-modal-confirm");
            SpriteFrame confirmText = _paint.Line(cjk ? "确认删除" : "DELETE", 15, ShellPaint.Ink, ShellPaint.Navy, ConfirmButtonRect.Width - 12, true);
            if (confirmText != null)
            {
                OnlineShellArt.Add(into, ref order, confirmText,
                    ConfirmButtonRect.X + (ConfirmButtonRect.Width - confirmText.Width) / 2,
                    ConfirmButtonRect.Y + (ConfirmButtonRect.Height - confirmText.Height) / 2,
                    "delete-modal-confirm-text");
            }

            OnlineShellArt.Add(into, ref order,
                _paint.Box(CancelButtonRect.Width, CancelButtonRect.Height, 8, SecondaryStyle),
                CancelButtonRect.X, CancelButtonRect.Y, "delete-modal-cancel");
            SpriteFrame cancelText = _paint.Line(cjk ? "取消" : "CANCEL", 15, ShellPaint.Ink, ShellPaint.Navy, CancelButtonRect.Width - 12);
            if (cancelText != null)
            {
                OnlineShellArt.Add(into, ref order, cancelText,
                    CancelButtonRect.X + (CancelButtonRect.Width - cancelText.Width) / 2,
                    CancelButtonRect.Y + (CancelButtonRect.Height - cancelText.Height) / 2,
                    "delete-modal-cancel-text");
            }

            ShellHint[] hints =
            {
                new ShellHint(cjk ? "Enter" : "ENTER", cjk ? "确认删除" : "CONFIRM"),
                new ShellHint(cjk ? "Esc" : "ESC", cjk ? "取消" : "CANCEL"),
            };
            OnlineShellArt.AddHints(into, ref order, _paint, hints, OnlineShellArt.ScreenWidth / 2, CardY + CardHeight - 36, "delete-modal-hint");
        }
    }
}
