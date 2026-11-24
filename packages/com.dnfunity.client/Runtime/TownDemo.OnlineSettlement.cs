using Dnf.Play;
using Dnf.Render;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// <see cref="TownDemo"/> 的联网结算界面与菜单接线(r18-settleui):
    /// 仅在 <c>-dnfonline</c> 且挂载了 <see cref="OnlineDungeonFlowController"/> 时生效;
    /// 离线副本与 <c>-dnfnetdungeon</c> 老路径零改动。
    /// </summary>
    public sealed partial class TownDemo
    {
        private OnlineSettlementTownWorld _onlineSettlementTownWorld;
        private OnlineSettlementController _onlineSettlement;

        private bool IsOnlineSettlementActive => _onlineSettlement != null && _onlineSettlement.IsActive;

        private ITownWorld WrapOnlineSettlementTownWorld(ITownWorld townWorld)
        {
            if (_onlineTown == null || townWorld == null)
            {
                _onlineSettlementTownWorld = null;
                return townWorld;
            }
            _onlineSettlementTownWorld = new OnlineSettlementTownWorld(townWorld);
            return _onlineSettlementTownWorld;
        }

        private void AttachOnlineSettlement(TownAtlas atlas)
        {
            DisposeOnlineSettlement();
            if (_onlineDungeon == null || _flow == null || _shell == null || _onlineTown == null || _netDungeon)
            {
                return;
            }
            var options = new OnlineSettlementOptions
            {
                Assets = _assets,
                Art = _menuArt,
                Texts = WorldTexts.Shared,
                Atlas = atlas,
                GetProgress = () => _progress,
                GetSelfUniqueId = () => _onlineTown?.Profile?.ServerUniqueId,
                GetPartySlot = () => 0,
                GetCurrentTownPlace = () => _flow != null && _flow.Stage == GameStage.Town ? _flow.Place : null,
                ApplyReturnTarget = target => _onlineSettlementTownWorld?.ArmReturn(target),
                Log = line => Debug.Log(line),
                OnDisconnected = () => _shell?.Flow?.NotifyDisconnected(),
            };
            _onlineSettlement = new OnlineSettlementController(_flow, options);
            _onlineSettlement.Attach(_onlineDungeon);
        }

        private void PollOnlineSettlement(double deltaMs)
        {
            if (_onlineSettlement == null)
            {
                return;
            }
            if (_onlineDungeon == null || _onlineTown == null || _flow == null)
            {
                DisposeOnlineSettlement();
                return;
            }
            _onlineSettlement.Poll(OnlineNowMs(), deltaMs);
        }

        private bool TryChooseOnlineSettlement(SettlementChoice choice)
        {
            return _onlineSettlement != null && _onlineSettlement.IsActive && _onlineSettlement.Choose(choice);
        }

        private bool TryClickOnlineSettlement(int x, int y)
        {
            if (_onlineSettlement == null || !_onlineSettlement.IsActive)
            {
                return false;
            }
            _onlineSettlement.Click(x, y);
            return true;
        }

        private void UpdateOnlineSettlementPointer()
        {
            if (_onlineSettlement == null || !_onlineSettlement.IsActive)
            {
                return;
            }
            if (TryGetCanvasPoint(Input.mousePosition, out int cx, out int cy))
            {
                _onlineSettlement.SetPointer(cx, cy);
            }
            else
            {
                _onlineSettlement.ClearPointer();
            }
        }

        private void DisposeOnlineSettlement()
        {
            if (_onlineSettlement != null)
            {
                _onlineSettlement.Dispose();
                _onlineSettlement = null;
            }
            _onlineSettlementTownWorld?.ClearReturn();
        }
    }
}
