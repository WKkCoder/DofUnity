using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Dnf.Play
{
    public sealed partial class OnlineShellFlow
    {
        private CharacterCreateModal _createModal;
        private CharacterDeleteModal _deleteModal;
        private byte _pendingCreateJob;
        private byte[] _pendingCreateNameBytes;
        private int _pendingDeleteSlot = -1;

        public CharacterCreateModal CreateModal => _createModal;
        public CharacterDeleteModal DeleteModal => _deleteModal;
        public bool HasCharacterModalOpen => _createModal != null || _deleteModal != null;

        private ICharacterManageModal ActiveModal => (ICharacterManageModal)_createModal ?? _deleteModal;

        private void ModalReset()
        {
            _createModal = null;
            _deleteModal = null;
            _pendingCreateNameBytes = null;
            _pendingDeleteSlot = -1;
            if (_selectScene != null)
            {
                _selectScene.Modal = null;
            }
        }

        private void ModalType(string text)
        {
            ActiveModal?.Type(text);
        }

        private bool ModalKey(OnlineShellKey key)
        {
            ICharacterManageModal modal = ActiveModal;
            if (modal == null)
            {
                return false;
            }
            return modal.Key(key);
        }

        private bool ModalClick(int x, int y)
        {
            ICharacterManageModal modal = ActiveModal;
            if (modal == null)
            {
                return false;
            }
            modal.Click(x, y);
            return true;
        }

        private void ModalAdvance(double deltaMs)
        {
            if (Stage == OnlineShellStage.CharacterSelect)
            {
                ActiveModal?.Advance(deltaMs);
            }
        }

        private bool ModalIntents()
        {
            if (_createModal != null)
            {
                CharacterManageModalAction action = _createModal.TakeAction();
                if (action == CharacterManageModalAction.Cancel)
                {
                    if (_op != null && _op.Kind == OpKind.CreateCharacter)
                    {
                        DropOperation();
                        _createModal.SetFailure(OnlineCharacterManageStatus.Cancelled);
                        Log(OnlineShellStage.CharacterSelect, "fail", "create_char_cancelled");
                    }
                    else
                    {
                        ModalReset();
                    }
                }
                else if (action == CharacterManageModalAction.Submit && _op == null)
                {
                    StartCreateCharacter();
                }
                return true;
            }

            if (_deleteModal != null)
            {
                CharacterManageModalAction action = _deleteModal.TakeAction();
                if (action == CharacterManageModalAction.Cancel)
                {
                    if (_op != null && _op.Kind == OpKind.DeleteCharacter)
                    {
                        DropOperation();
                        _deleteModal.SetFailure(OnlineCharacterManageStatus.Cancelled);
                        Log(OnlineShellStage.CharacterSelect, "fail", "delete_char_cancelled");
                    }
                    else
                    {
                        ModalReset();
                    }
                }
                else if (action == CharacterManageModalAction.Submit && _op == null)
                {
                    StartDeleteCharacter();
                }
                return true;
            }

            return false;
        }

        private void OpenCreateModal()
        {
            if (!CanManageCharacters || _op != null)
            {
                return;
            }
            ICharacterTitles titles = _options.CharacterSelect?.Titles;
            ICharacterAvatars avatars = _options.Art?.Avatars;
            _createModal = new CharacterCreateModal(titles, avatars, _options.Fonts);
            _deleteModal = null;
            if (_selectScene != null)
            {
                _selectScene.Modal = _createModal;
            }
            SetNotice(null);
            Log(OnlineShellStage.CharacterSelect, "open", "create_modal");
        }

        private void OpenDeleteModal()
        {
            if (!CanManageCharacters || _op != null || _select == null || _select.Selected == null)
            {
                return;
            }
            int slotIndex = _select.Index;
            if (slotIndex < 0 || slotIndex > byte.MaxValue || Characters == null || slotIndex >= Characters.Entries.Count)
            {
                return;
            }
            CharacterSlot selected = _select.Selected;
            _deleteModal = new CharacterDeleteModal(
                (byte)slotIndex,
                selected.Name,
                selected.JobName,
                selected.Level,
                _options.Fonts);
            _createModal = null;
            if (_selectScene != null)
            {
                _selectScene.Modal = _deleteModal;
            }
            SetNotice(null);
            Log(OnlineShellStage.CharacterSelect, "open", "delete_modal");
        }

        private void StartCreateCharacter()
        {
            ICharacterManageBackend backend = _options.CharacterManage;
            if (backend == null || !backend.AllowCharacterWrite)
            {
                _createModal.SetFailure(OnlineCharacterManageStatus.Disabled);
                Log(OnlineShellStage.CharacterSelect, "fail", "create_char_disabled");
                return;
            }
            byte job = _createModal.SelectedJob;
            string name = _createModal.Name;
            if (!OnlineCharacterNameRules.TryEncodeStrictUtf8(name, out byte[] nameBytes)
                || OnlineCharacterNameRules.ValidateCreateName(nameBytes) != OnlineCharacterNameError.Valid)
            {
                _createModal.SetFailure(OnlineCharacterManageStatus.InvalidInput);
                Log(OnlineShellStage.CharacterSelect, "fail", "create_char_invalid");
                return;
            }
            _pendingCreateJob = job;
            _pendingCreateNameBytes = nameBytes;
            _createModal.SetBusy(true);
            _op = Begin(OpKind.CreateCharacter, ct => backend.CreateCharacterAsync(job, name, ct));
        }

        private void StartDeleteCharacter()
        {
            ICharacterManageBackend backend = _options.CharacterManage;
            if (backend == null || !backend.AllowCharacterWrite)
            {
                _deleteModal.SetFailure(OnlineCharacterManageStatus.Disabled);
                Log(OnlineShellStage.CharacterSelect, "fail", "delete_char_disabled");
                return;
            }
            byte slot = _deleteModal.TargetSlot;
            string confirm = _deleteModal.ConfirmName;
            if (!_deleteModal.IsNameMatched)
            {
                _deleteModal.SetFailure(OnlineCharacterManageStatus.InvalidInput);
                Log(OnlineShellStage.CharacterSelect, "fail", "delete_char_invalid");
                return;
            }
            _pendingDeleteSlot = slot;
            _deleteModal.SetBusy(true);
            _op = Begin(OpKind.DeleteCharacter, ct => backend.DeleteCharacterAsync(slot, confirm, ct));
        }

        private void FinishManageCharacter(Operation op, Exception error, bool isCreate)
        {
            if (error != null)
            {
                if (IsConnectionError(error))
                {
                    ModalReset();
                    GoToLogin(new OnlineNotice(OnlineNoticeKind.Disconnected), "disconnected");
                    return;
                }
                OnlineCharacterManageStatus errStatus = error is OperationCanceledException
                    ? OnlineCharacterManageStatus.Cancelled
                    : OnlineCharacterManageStatus.ProtocolError;
                ApplyManageFailure(isCreate, errStatus, null);
                return;
            }

            OnlineCharacterManageOutcome outcome = ((Task<OnlineCharacterManageOutcome>)op.Task).Result;
            if (outcome == null)
            {
                ApplyManageFailure(isCreate, OnlineCharacterManageStatus.ProtocolError, null);
                return;
            }
            if (outcome.Status == OnlineCharacterManageStatus.Disconnected)
            {
                ModalReset();
                GoToLogin(new OnlineNotice(OnlineNoticeKind.Disconnected), "disconnected");
                return;
            }
            if (!outcome.Success || outcome.Characters == null)
            {
                ApplyManageFailure(isCreate, outcome.Status, outcome.RawErrorCode);
                return;
            }

            OnlineCharacterList previous = Characters;
            OnlineCharacterList refreshed = outcome.Characters;
            int nextIndex;
            if (isCreate)
            {
                nextIndex = FindCreatedCharacterIndex(previous, refreshed, _pendingCreateJob, _pendingCreateNameBytes);
            }
            else
            {
                nextIndex = refreshed.Entries.Count == 0
                    ? -1
                    : Math.Max(0, Math.Min(_pendingDeleteSlot, refreshed.Entries.Count - 1));
            }

            Characters = refreshed;
            if (SelectedSlot >= 0)
            {
                SelectedSlot = refreshed.Entries.Count == 0
                    ? -1
                    : Math.Max(0, Math.Min(SelectedSlot, refreshed.Entries.Count - 1));
            }
            ModalReset();
            Log(OnlineShellStage.CharacterSelect, "ok", isCreate ? "create_char" : "delete_char");
            OnlineNotice notice = refreshed.Entries.Count == 0 ? new OnlineNotice(OnlineNoticeKind.NoCharacters) : null;
            EnterCharacterSelect(nextIndex, notice, isCreate ? "char_created" : "char_deleted");
        }

        private void TimeoutManageCharacter(bool isCreate)
        {
            DropOperation();
            ApplyManageFailure(isCreate, OnlineCharacterManageStatus.NoReply, null);
        }

        private void ApplyManageFailure(bool isCreate, OnlineCharacterManageStatus status, byte? rawErrorCode)
        {
            if (isCreate)
            {
                _createModal?.SetFailure(status, rawErrorCode);
                Log(OnlineShellStage.CharacterSelect, "fail", "create_char_" + ManageReasonCode(status));
            }
            else
            {
                _deleteModal?.SetFailure(status, rawErrorCode);
                Log(OnlineShellStage.CharacterSelect, "fail", "delete_char_" + ManageReasonCode(status));
            }
        }

        private static string ManageReasonCode(OnlineCharacterManageStatus status)
        {
            return status.ToString().ToLowerInvariant();
        }

        private static int FindCreatedCharacterIndex(
            OnlineCharacterList previous,
            OnlineCharacterList refreshed,
            byte job,
            byte[] nameBytes)
        {
            if (refreshed == null || refreshed.Entries.Count == 0)
            {
                return -1;
            }
            var oldUids = new HashSet<ushort>();
            if (previous != null)
            {
                for (int i = 0; i < previous.Entries.Count; i++)
                {
                    oldUids.Add(previous.Entries[i].Uid);
                }
            }
            int nameMatchIndex = -1;
            for (int i = 0; i < refreshed.Entries.Count; i++)
            {
                OnlineCharacterEntry entry = refreshed.Entries[i];
                if (entry.Job == job && BytesEqual(entry.NameBytes, nameBytes))
                {
                    if (!oldUids.Contains(entry.Uid))
                    {
                        return i;
                    }
                    nameMatchIndex = i;
                }
            }
            return nameMatchIndex >= 0 ? nameMatchIndex : refreshed.Entries.Count - 1;
        }

        private static bool BytesEqual(IReadOnlyList<byte> left, byte[] right)
        {
            if (left == null || right == null || left.Count != right.Length)
            {
                return false;
            }
            for (int i = 0; i < right.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
