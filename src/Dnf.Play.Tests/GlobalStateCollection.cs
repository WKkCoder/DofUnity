namespace Dnf.Play.Tests;

/// <summary>
/// "全进程共用静态状态"测试集合的标记类。
/// 凡是会改全进程共用静态状态的测试(例如给 <c>OverlayArt.SharedFonts</c>、<c>WorldTexts.Shared</c> 赋值,
/// 改环境变量、当前目录、默认区域性、控制台输出等),都必须放进这个集合:在测试类上写
/// <c>[Collection(GlobalStateCollection.Name)]</c>,并在 <c>finally</c> 里把改过的状态写回"进入时的旧值"。
/// <para>
/// 为什么需要它:xUnit 默认让不同测试类(各自是一个集合)并行执行;一条测试在运行的那几十毫秒里改了静态状态,
/// 别的测试类(默认假设"没有共用字体集")就会看到被改过的值而随机失败。
/// <c>DisableParallelization = true</c> 的集合,xUnit 会等所有可并行的集合都跑完之后才逐个串行执行,
/// 这个集合里的测试运行时没有任何别的测试在跑(集合内部本来就是串行),所以谁也看不到被临时改过的状态。
/// </para>
/// <para>
/// 注意:xUnit 只会发现 public 的集合定义类,所以这个类必须是 public;它只是标记,不要往里放成员、构造函数或夹具。
/// </para>
/// </summary>
[CollectionDefinition(GlobalStateCollection.Name, DisableParallelization = true)]
public sealed class GlobalStateCollection
{
    /// <summary>集合名:测试类上写 <c>[Collection(GlobalStateCollection.Name)]</c> 引用它。</summary>
    public const string Name = "GlobalStaticStateSerial";
}
