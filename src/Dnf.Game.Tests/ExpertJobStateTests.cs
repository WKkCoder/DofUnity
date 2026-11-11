using static Dnf.Game.Tests.ExpertJobTestData;

namespace Dnf.Game.Tests;

/// <summary>
/// 副职业角色状态:就职、放弃、配方位图、读档还原。依据:服务端 CUser::quest_reward 0x0866ea92(就职)、CExpertJobMgr::OnGiveup 0x0849e60c(放弃)、
/// CCharacExpertJob::_reset 0x0832b064 / InitScript 0x0832b36c / set_recipe 0x0832b264(静态读码)。
/// 合成脚本:放弃费用 1000、2000、3000,放弃要完成任务 901 与 902,初始耐久 5,技能 901、902。
/// </summary>
public class ExpertJobStateTests
{
    private static readonly ExpertJobScript Script = Disjointer();

    private static ExpertJobState Hired()
    {
        var state = new ExpertJobState();
        Assert.True(state.Acquire(ExpertJobKind.Disjointer, Script).Ok);
        return state;
    }

    private static bool AllCleared(int quest) => true;

    // ------------------------------------------------------------------ 新角色与就职

    [Fact]
    public void ANewCharacterHasNoJobAndZeroEverything()
    {
        var state = new ExpertJobState();
        Assert.Equal(ExpertJobKind.None, state.Kind);
        Assert.False(state.HasJob);
        Assert.Equal(0, state.Exp);          // 新角色是 0,放弃之后才是 −1
        Assert.Equal(0, state.GiveupCount);
        Assert.Equal(0, state.MachineEndurance);
        Assert.Equal(0, state.MachineGrade);
        Assert.Empty(state.LearnedRecipes());
    }

    [Fact]
    public void TakingTheJobSetsTheKindInitialisesTheMachineAndListsTheSkillsToGrant()
    {
        var state = new ExpertJobState();
        ExpertJobChange change = state.Acquire(ExpertJobKind.Disjointer, Script);
        Assert.True(change.Ok);
        Assert.Equal(ExpertJobKind.Disjointer, state.Kind);
        Assert.True(state.HasJob);
        Assert.Equal(0, state.Exp);
        Assert.Equal(5, state.MachineEndurance);   // [endurance initial value]
        Assert.Equal(0, state.MachineGrade);
        Assert.Equal(2, change.SkillsToGrant.Count);
        Assert.Equal(901, change.SkillsToGrant[0].SkillId);
        Assert.Equal(1, change.SkillsToGrant[0].Level);
        Assert.Equal(902, change.SkillsToGrant[1].SkillId);
        Assert.Empty(change.SkillsToRemove);
        Assert.Empty(change.QuestsToReset);
        Assert.Equal(0, change.GoldCost);
    }

    [Fact]
    public void TakingTheJobAfterAGiveUpKeepsTheGiveUpCountAndResetsTheExp()
    {
        var state = new ExpertJobState();
        Assert.True(state.Load(0, -1, 2, 0, 0, null));
        Assert.Equal(-1, state.Exp);
        Assert.True(state.Acquire(ExpertJobKind.Disjointer, Script).Ok);
        Assert.Equal(0, state.Exp);               // 再就职时经验清零
        Assert.Equal(2, state.GiveupCount);       // InitScript 不动放弃次数
    }

    [Fact]
    public void TakingAJobTheStateCannotHoldIsRefusedAndChangesNothing()
    {
        var none = new ExpertJobState();
        Assert.Equal(ExpertJobError.NotAvailable, none.Acquire(ExpertJobKind.None, Script).Error);
        Assert.Equal(ExpertJobError.NotAvailable, none.Acquire(ExpertJobKind.Disjointer, null).Error);
        Assert.Equal(ExpertJobError.NotAvailable, none.Acquire(ExpertJobKind.Alchemist, Script).Error);   // 脚本种类对不上
        Assert.Equal(ExpertJobError.NotAvailable, none.Acquire((ExpertJobKind)9, Script).Error);
        Assert.False(none.HasJob);

        ExpertJobState hired = Hired();
        hired.SetExp(40);
        hired.SetMachine(1, 77);
        Assert.Equal(ExpertJobError.NotAvailable, hired.Acquire(ExpertJobKind.Disjointer, Script).Error);   // 已有副职业
        Assert.Equal(40, hired.Exp);
        Assert.Equal(77, hired.MachineEndurance);
        Assert.Equal(1, hired.MachineGrade);
    }

    [Fact]
    public void AScriptThatClaimsTheNoneKindOrAnUnknownKindCannotBeTakenEither()
    {
        // 请求的种类与脚本的种类一致也不行:种类本身必须是真正的副职业(1 到 4)。
        var state = new ExpertJobState();
        ExpertJobScript noneScript = ExpertJobScript.Parse(ExpertJobKind.None, DisjointerLines());
        ExpertJobScript oddScript = ExpertJobScript.Parse((ExpertJobKind)9, DisjointerLines());
        Assert.Equal(ExpertJobError.NotAvailable, state.Acquire(ExpertJobKind.None, noneScript).Error);
        Assert.Equal(ExpertJobError.NotAvailable, state.Acquire((ExpertJobKind)9, oddScript).Error);
        Assert.False(state.HasJob);
        Assert.Equal(0, state.MachineEndurance);
    }

    [Fact]
    public void InitScriptResetsTheMachineAndRecipesButNotTheJobExpOrGiveUps()
    {
        ExpertJobState state = Hired();
        state.SetExp(300);
        state.SetMachine(2, 99);
        Assert.True(state.TrySetRecipe(4));
        state.Load(3, 300, 2, 99, 2, state.RecipeBitmap());
        state.InitScript(Script);
        Assert.Equal(5, state.MachineEndurance);
        Assert.Equal(0, state.MachineGrade);
        Assert.Empty(state.LearnedRecipes());
        Assert.Equal(ExpertJobKind.Disjointer, state.Kind);
        Assert.Equal(300, state.Exp);
        Assert.Equal(2, state.GiveupCount);
    }

    // ------------------------------------------------------------------ 放弃

    [Fact]
    public void GivingUpRequiresAJobAndAScript()
    {
        Assert.Equal(ExpertJobError.NotAvailable, new ExpertJobState().PlanGiveUp(Script, 99999, AllCleared).Error);
        Assert.Equal(ExpertJobError.NotAvailable, Hired().PlanGiveUp(null, 99999, AllCleared).Error);
    }

    [Fact]
    public void GivingUpChecksTheCountThenGoldThenQuestsInThatOrder()
    {
        // 次数已到费用表长(读档来的 3)→ 1,哪怕金币和任务也都不行。
        var spent = new ExpertJobState();
        spent.Load(3, 0, 3, 5, 0, null);
        Assert.Equal(ExpertJobError.LimitReached, spent.PlanGiveUp(Script, 0, _ => false).Error);

        // 金币不够 → 0x15,哪怕任务也没完成。
        ExpertJobState hired = Hired();
        Assert.Equal(ExpertJobError.NotEnoughGold, hired.PlanGiveUp(Script, 999, _ => false).Error);

        // 金币刚好够、任务没完成 → 8;任务回调为空也当没完成。
        Assert.Equal(ExpertJobError.NotAvailable, hired.PlanGiveUp(Script, 1000, q => q == 901).Error);
        Assert.Equal(ExpertJobError.NotAvailable, hired.PlanGiveUp(Script, 1000, q => q == 902).Error);
        Assert.Equal(ExpertJobError.NotAvailable, hired.PlanGiveUp(Script, 1000, null).Error);
    }

    [Fact]
    public void ASuccessfulGiveUpPlanListsTheCostSkillsAndQuestsWithoutTouchingTheState()
    {
        ExpertJobState hired = Hired();
        hired.SetExp(250);
        ExpertJobChange plan = hired.PlanGiveUp(Script, 1000, AllCleared);
        Assert.True(plan.Ok);
        Assert.Equal(1000, plan.GoldCost);
        Assert.Equal(new[] { 901, 902 }, plan.SkillsToRemove.ToArray());
        Assert.Equal(new[] { 901, 902 }, plan.QuestsToReset.ToArray());
        Assert.Empty(plan.SkillsToGrant);
        Assert.Equal(ExpertJobKind.Disjointer, hired.Kind);
        Assert.Equal(250, hired.Exp);
        Assert.Equal(0, hired.GiveupCount);
    }

    [Fact]
    public void TheCostFollowsTheGiveUpCountAndTheLastPriceRepeatsBecauseTheCountIsCapped()
    {
        var state = new ExpertJobState();
        int[] expected = { 1000, 2000, 3000, 3000, 3000 };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(state.Acquire(ExpertJobKind.Disjointer, Script).Ok);
            ExpertJobChange plan = state.PlanGiveUp(Script, 1_000_000, AllCleared);
            Assert.True(plan.Ok);
            Assert.Equal(expected[i], plan.GoldCost);
            state.ApplyGiveUp(Script);
            Assert.Equal(Math.Min(i + 1, 2), state.GiveupCount);   // 费用表长 3,次数封顶 2
        }
    }

    [Fact]
    public void GivingUpClearsTheJobWritesMinusOneExpAndWipesTheMachineAndRecipes()
    {
        ExpertJobState state = Hired();
        state.SetExp(450);
        state.SetMachine(2, 17);
        state.TrySetRecipe(9);
        state.ApplyGiveUp(Script);
        Assert.Equal(ExpertJobKind.None, state.Kind);
        Assert.False(state.HasJob);
        Assert.Equal(-1, state.Exp);
        Assert.Equal(0, state.MachineEndurance);
        Assert.Equal(0, state.MachineGrade);
        Assert.Empty(state.LearnedRecipes());
        Assert.Equal(1, state.GiveupCount);
    }

    [Fact]
    public void AnEmptyCostTableKeepsTheGiveUpCountAtZero()
    {
        ExpertJobScript noCosts = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[] { L("[base const]", 5) });
        var state = new ExpertJobState();
        state.Load(3, 0, 0, 0, 0, null);
        state.ApplyGiveUp(noCosts);
        Assert.Equal(0, state.GiveupCount);
        // 没有费用表就一次也放弃不了(次数 0 已经到了表长 0)。
        Assert.Equal(ExpertJobError.LimitReached, Hired().PlanGiveUp(noCosts, 99999, AllCleared).Error);
    }

    // ------------------------------------------------------------------ 配方位图

    [Fact]
    public void RecipesAreBitsInAThirtyByteBitmap()
    {
        ExpertJobState state = Hired();
        Assert.True(state.TrySetRecipe(0));
        Assert.True(state.TrySetRecipe(9));
        Assert.True(state.TrySetRecipe(239));
        Assert.False(state.TrySetRecipe(9));          // 学过了
        Assert.True(state.HasRecipe(0));
        Assert.True(state.HasRecipe(9));
        Assert.True(state.HasRecipe(239));
        Assert.False(state.HasRecipe(1));
        Assert.Equal(new[] { 0, 9, 239 }, state.LearnedRecipes().ToArray());

        byte[] bitmap = state.RecipeBitmap();
        Assert.Equal(ExpertJobState.RecipeBytes, bitmap.Length);
        Assert.Equal(30, bitmap.Length);
        Assert.Equal(0b0000_0001, bitmap[0]);
        Assert.Equal(0b0000_0010, bitmap[1]);
        Assert.Equal(0b1000_0000, bitmap[29]);
        Assert.Equal(240, ExpertJobState.RecipeCount);
    }

    [Theory]
    [InlineData(240)]
    [InlineData(1000)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RecipesOutsideTheBitmapAreRefused(int index)
    {
        ExpertJobState state = Hired();
        Assert.False(state.TrySetRecipe(index));
        Assert.False(state.HasRecipe(index));
        Assert.Empty(state.LearnedRecipes());
    }

    [Fact]
    public void ARecipeBitmapCopyIsIndependentOfTheState()
    {
        ExpertJobState state = Hired();
        state.TrySetRecipe(3);
        byte[] copy = state.RecipeBitmap();
        copy[0] = 0xFF;
        Assert.Equal(new[] { 3 }, state.LearnedRecipes().ToArray());
    }

    // ------------------------------------------------------------------ 复制与读档

    [Fact]
    public void ACloneIsIndependent()
    {
        ExpertJobState state = Hired();
        state.SetExp(120);
        state.SetMachine(1, 44);
        state.TrySetRecipe(5);
        Assert.True(state.Load(3, 120, 7, 44, 1, state.RecipeBitmap()));   // 放弃过 7 次
        ExpertJobState copy = state.Clone();
        Assert.Equal(ExpertJobKind.Disjointer, copy.Kind);
        Assert.Equal(120, copy.Exp);
        Assert.Equal(7, copy.GiveupCount);
        Assert.Equal(44, copy.MachineEndurance);
        Assert.Equal(1, copy.MachineGrade);
        Assert.Equal(new[] { 5 }, copy.LearnedRecipes().ToArray());

        copy.SetExp(999);
        copy.TrySetRecipe(6);
        copy.ApplyGiveUp(Script);
        Assert.Equal(2, copy.GiveupCount);          // 费用表三项,次数夹到 2
        Assert.Equal(120, state.Exp);
        Assert.Equal(7, state.GiveupCount);
        Assert.Equal(new[] { 5 }, state.LearnedRecipes().ToArray());
        Assert.True(state.HasJob);
    }

    [Fact]
    public void LoadingValidFieldsRestoresThemAsTheyAre()
    {
        var bitmap = new byte[30];
        bitmap[1] = 0b10;
        var state = new ExpertJobState();
        Assert.True(state.Load(3, 450, 2, 17, 2, bitmap));
        Assert.Equal(ExpertJobKind.Disjointer, state.Kind);
        Assert.Equal(450, state.Exp);
        Assert.Equal(2, state.GiveupCount);
        Assert.Equal(17, state.MachineEndurance);
        Assert.Equal(2, state.MachineGrade);
        Assert.Equal(new[] { 9 }, state.LearnedRecipes().ToArray());
    }

    [Fact]
    public void LoadingFieldsThatAreOutOfRangeCorrectsThemAndReportsIt()
    {
        var state = new ExpertJobState();
        Assert.False(state.Load(7, 0, 0, 0, 0, null));
        Assert.Equal(ExpertJobKind.None, state.Kind);

        Assert.False(state.Load(3, 0, -4, 0, 0, null));
        Assert.Equal(0, state.GiveupCount);
        Assert.False(state.Load(3, 0, 70000, 0, 0, null));
        Assert.Equal(65535, state.GiveupCount);
        Assert.False(state.Load(3, 0, 0, 0, -2, null));
        Assert.Equal(0, state.MachineGrade);
        Assert.False(state.Load(3, 0, 0, 0, 0, new byte[29]));    // 位图长度不对 → 当空
        Assert.Empty(state.LearnedRecipes());

        Assert.True(state.Load(0, 0, 0, 0, 0, null));             // 没有副职业的合法存档
        Assert.Equal(ExpertJobKind.None, state.Kind);
    }

    [Fact]
    public void TheGiveUpCountMayBeExactlyTheSixteenBitLimitOrZero()
    {
        var state = new ExpertJobState();
        Assert.True(state.Load(3, 0, 65535, 0, 0, null));
        Assert.Equal(65535, state.GiveupCount);
        Assert.False(state.Load(3, 0, 65536, 0, 0, null));
        Assert.Equal(65535, state.GiveupCount);
        Assert.True(state.Load(3, 0, 0, 0, 0, null));
        Assert.Equal(0, state.GiveupCount);
        Assert.False(state.Load(3, 0, -1, 0, 0, null));
        Assert.Equal(0, state.GiveupCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(60)]
    public void ARecipeBitmapOfTheWrongLengthIsNotTrustedInEitherDirection(int length)
    {
        var bitmap = new byte[length];
        Array.Fill(bitmap, (byte)0xFF);
        var state = new ExpertJobState();
        Assert.False(state.Load(3, 0, 0, 0, 0, bitmap));
        Assert.Empty(state.LearnedRecipes());
    }

    [Fact]
    public void LoadingReplacesWhateverWasThereBefore()
    {
        ExpertJobState state = Hired();
        state.TrySetRecipe(7);
        state.Load(0, 0, 0, 0, 0, null);
        Assert.Empty(state.LearnedRecipes());
        Assert.Equal(0, state.MachineEndurance);
    }
}
