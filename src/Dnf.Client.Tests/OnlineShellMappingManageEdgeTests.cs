using Dnf.Net.Session;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// Net 层创建/删除失败枚举到外壳状态枚举的逐值映射表(r20fix-createcharui,相邻点补测)。
/// 两张表按"同名到同名"手写成字面量:任何两个值互换、落到默认的未知错误、或漏掉某一个,都要有一行红。
/// 不识别的值(含"无失败"与"未识别")统一得到未知错误。
/// </summary>
public sealed class OnlineShellMappingManageEdgeTests
{
    [Theory]
    [InlineData(CharacterCreateFailure.DatabaseOrUnknownError, OnlineCharacterManageStatus.DatabaseOrUnknownError)]
    [InlineData(CharacterCreateFailure.SlotLimitReached, OnlineCharacterManageStatus.SlotLimitReached)]
    [InlineData(CharacterCreateFailure.ClientRestricted, OnlineCharacterManageStatus.ClientRestricted)]
    [InlineData(CharacterCreateFailure.CreationRestrictedEmptyAccount, OnlineCharacterManageStatus.CreationRestrictedEmptyAccount)]
    [InlineData(CharacterCreateFailure.CreationRestrictedByEvent, OnlineCharacterManageStatus.CreationRestrictedByEvent)]
    [InlineData(CharacterCreateFailure.DuplicateActiveName, OnlineCharacterManageStatus.DuplicateActiveName)]
    [InlineData(CharacterCreateFailure.StateLocked, OnlineCharacterManageStatus.StateLocked)]
    [InlineData(CharacterCreateFailure.EventCharacterConditionFailed, OnlineCharacterManageStatus.EventCharacterConditionFailed)]
    [InlineData(CharacterCreateFailure.ClientSilentIgnored, OnlineCharacterManageStatus.ClientSilentIgnored)]
    [InlineData(CharacterCreateFailure.CreationBlocked, OnlineCharacterManageStatus.CreationBlocked)]
    [InlineData(CharacterCreateFailure.DuplicateDeletedName, OnlineCharacterManageStatus.DuplicateDeletedName)]
    [InlineData(CharacterCreateFailure.DailyCreationLimitReached, OnlineCharacterManageStatus.DailyCreationLimitReached)]
    [InlineData(CharacterCreateFailure.ChannelNotAllowed, OnlineCharacterManageStatus.ChannelNotAllowed)]
    [InlineData(CharacterCreateFailure.InvalidName, OnlineCharacterManageStatus.InvalidName)]
    [InlineData(CharacterCreateFailure.ServiceRestricted, OnlineCharacterManageStatus.ServiceRestricted)]
    [InlineData(CharacterCreateFailure.Unknown, OnlineCharacterManageStatus.UnknownError)]
    [InlineData(CharacterCreateFailure.None, OnlineCharacterManageStatus.UnknownError)]
    [InlineData((CharacterCreateFailure)999, OnlineCharacterManageStatus.UnknownError)]
    public void Each_create_failure_maps_to_its_own_status_and_anything_else_to_the_unknown_error(
        CharacterCreateFailure failure, OnlineCharacterManageStatus expected)
    {
        Assert.Equal(expected, OnlineShellMapping.CreateFailure(failure));
    }

    [Theory]
    [InlineData(CharacterDeleteFailure.DatabaseOrUnknownError, OnlineCharacterManageStatus.DatabaseOrUnknownError)]
    [InlineData(CharacterDeleteFailure.StateLocked, OnlineCharacterManageStatus.StateLocked)]
    [InlineData(CharacterDeleteFailure.EmptySlot, OnlineCharacterManageStatus.EmptySlot)]
    [InlineData(CharacterDeleteFailure.CooldownOrActiveCharacter, OnlineCharacterManageStatus.CooldownOrActiveCharacter)]
    [InlineData(CharacterDeleteFailure.GuildMasterCannotDelete, OnlineCharacterManageStatus.GuildMasterCannotDelete)]
    [InlineData(CharacterDeleteFailure.ClientDeletionBlocked, OnlineCharacterManageStatus.ClientDeletionBlocked)]
    [InlineData(CharacterDeleteFailure.TradePunished, OnlineCharacterManageStatus.TradePunished)]
    [InlineData(CharacterDeleteFailure.RestingUserRestricted, OnlineCharacterManageStatus.RestingUserRestricted)]
    [InlineData(CharacterDeleteFailure.ServiceRestricted, OnlineCharacterManageStatus.ServiceRestricted)]
    [InlineData(CharacterDeleteFailure.ItemLockActive, OnlineCharacterManageStatus.ItemLockActive)]
    [InlineData(CharacterDeleteFailure.TradePunishRestricted, OnlineCharacterManageStatus.TradePunishRestricted)]
    [InlineData(CharacterDeleteFailure.RestorePunishRestricted, OnlineCharacterManageStatus.RestorePunishRestricted)]
    [InlineData(CharacterDeleteFailure.SecurityServiceRequired, OnlineCharacterManageStatus.SecurityServiceRequired)]
    [InlineData(CharacterDeleteFailure.SharedSecurityRestriction, OnlineCharacterManageStatus.SharedSecurityRestriction)]
    [InlineData(CharacterDeleteFailure.Unknown, OnlineCharacterManageStatus.UnknownError)]
    [InlineData(CharacterDeleteFailure.None, OnlineCharacterManageStatus.UnknownError)]
    [InlineData((CharacterDeleteFailure)999, OnlineCharacterManageStatus.UnknownError)]
    public void Each_delete_failure_maps_to_its_own_status_and_anything_else_to_the_unknown_error(
        CharacterDeleteFailure failure, OnlineCharacterManageStatus expected)
    {
        Assert.Equal(expected, OnlineShellMapping.DeleteFailure(failure));
    }
}
