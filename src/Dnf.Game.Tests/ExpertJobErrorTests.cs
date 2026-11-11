namespace Dnf.Game.Tests;

/// <summary>
/// 错误号:数值就是服务端 ENUM_ERROR 的编号(宿主可能原样发给客户端),出处写在 <see cref="ExpertJobError"/> 每一项的注释里。
/// 只有最后一项 <see cref="ExpertJobError.UnknownProduct"/> 是这一线自己加的,故意放在服务端不会用到的范围。
/// </summary>
public class ExpertJobErrorTests
{
    [Theory]
    [InlineData(ExpertJobError.Ok, 0)]
    [InlineData(ExpertJobError.LimitReached, 1)]
    [InlineData(ExpertJobError.NoCharacter, 3)]
    [InlineData(ExpertJobError.NoSpace, 4)]
    [InlineData(ExpertJobError.NotAvailable, 8)]
    [InlineData(ExpertJobError.GoldLimit, 10)]
    [InlineData(ExpertJobError.LevelTooHigh, 13)]
    [InlineData(ExpertJobError.LevelTooLow, 14)]
    [InlineData(ExpertJobError.InvalidItem, 17)]
    [InlineData(ExpertJobError.NotPermitted, 19)]
    [InlineData(ExpertJobError.NotEnoughGold, 21)]
    [InlineData(ExpertJobError.Refused, 22)]
    [InlineData(ExpertJobError.Unidentified, 174)]
    [InlineData(ExpertJobError.MachineBroken, 189)]
    [InlineData(ExpertJobError.UnidentifiedNoResult, 211)]
    [InlineData(ExpertJobError.ItemLocked, 213)]
    [InlineData(ExpertJobError.UnknownProduct, 1000)]
    public void EachErrorKeepsItsNumber(ExpertJobError error, int number)
    {
        Assert.Equal(number, (int)error);
    }

    [Fact]
    public void NoTwoErrorsShareANumber()
    {
        int[] numbers = Array.ConvertAll((ExpertJobError[])Enum.GetValues(typeof(ExpertJobError)), e => (int)e);
        Assert.Equal(numbers.Length, new HashSet<int>(numbers).Count);
        Assert.Equal(17, numbers.Length);
    }

    [Fact]
    public void OnlyTheLocallyAddedErrorLiesOutsideTheServerRange()
    {
        foreach (ExpertJobError error in Enum.GetValues(typeof(ExpertJobError)))
        {
            if (error != ExpertJobError.UnknownProduct)
            {
                Assert.InRange((int)error, 0, 255);
            }
        }
        Assert.True((int)ExpertJobError.UnknownProduct > 255);
    }
}
