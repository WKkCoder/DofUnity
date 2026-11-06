using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>客户端自我限流:60 秒窗口内最多 10 次(成功失败都算),滑动窗口。时间用注入的值,不依赖真实时钟。</summary>
public sealed class GatewayRateLimiterTests
{
    private static TimeSpan S(double seconds)
    {
        return TimeSpan.FromSeconds(seconds);
    }

    [Fact]
    public void DefaultsAreTenPerSixtySeconds()
    {
        GatewayRateLimiter limiter = new();
        Assert.Equal(10, limiter.MaxRequests);
        Assert.Equal(S(60), limiter.Window);
    }

    [Fact]
    public void TheEleventhRequestInTheSameInstantIsRefused()
    {
        GatewayRateLimiter limiter = new();
        for (int i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire(S(0)), "request " + (i + 1));
        }
        Assert.False(limiter.TryAcquire(S(0)));
        Assert.False(limiter.TryAcquire(S(1)));
        Assert.Equal(10, limiter.Used(S(1)));
    }

    [Fact]
    public void ARequestExpiresExactlyOneWindowAfterItWasAdmitted()
    {
        GatewayRateLimiter limiter = new();
        for (int i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire(S(0)));
        }
        Assert.False(limiter.TryAcquire(S(59.999)));
        Assert.False(limiter.TryAcquire(TimeSpan.FromTicks(S(60).Ticks - 1)));
        Assert.True(limiter.TryAcquire(S(60)));
    }

    [Fact]
    public void WindowSlidesInsteadOfResettingAtFixedBoundaries()
    {
        GatewayRateLimiter limiter = new();
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire(S(0)));
        }
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire(S(30)));
        }
        Assert.False(limiter.TryAcquire(S(59)));

        // 第 60 秒:t=0 的五次过期,t=30 的五次还在 → 只剩五个名额
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire(S(60)), "slot " + (i + 1));
        }
        Assert.False(limiter.TryAcquire(S(60)));
        Assert.False(limiter.TryAcquire(S(89)));

        // 第 90 秒:t=30 的五次过期,t=60 的五次还在
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire(S(90)), "slot " + (i + 1));
        }
        Assert.False(limiter.TryAcquire(S(90)));
    }

    [Fact]
    public void RefusedAttemptsAreNotRecorded()
    {
        GatewayRateLimiter limiter = new();
        for (int i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire(S(0)));
        }
        for (int t = 5; t < 60; t += 5)
        {
            Assert.False(limiter.TryAcquire(S(t)));
        }
        Assert.Equal(10, limiter.Used(S(59)));
        // 如果被拒的尝试也入账,这里还会被挡住
        Assert.True(limiter.TryAcquire(S(60)));
    }

    [Fact]
    public void RetryAfterIsZeroWhenAvailableAndTimeUntilTheOldestExpiresWhenFull()
    {
        GatewayRateLimiter limiter = new();
        Assert.Equal(TimeSpan.Zero, limiter.RetryAfter(S(0)));
        for (int i = 0; i < 9; i++)
        {
            limiter.TryAcquire(S(10));
        }
        Assert.Equal(TimeSpan.Zero, limiter.RetryAfter(S(11)));
        limiter.TryAcquire(S(20));
        Assert.Equal(S(50), limiter.RetryAfter(S(20)));
        Assert.Equal(S(0.5), limiter.RetryAfter(S(69.5)));
        Assert.Equal(TimeSpan.Zero, limiter.RetryAfter(S(70)));
        Assert.Equal(TimeSpan.Zero, limiter.RetryAfter(S(200)));
    }

    [Fact]
    public void RetryAfterAndUsedDoNotChangeState()
    {
        GatewayRateLimiter limiter = new();
        for (int i = 0; i < 10; i++)
        {
            limiter.TryAcquire(S(0));
        }
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(S(30), limiter.RetryAfter(S(30)));
            Assert.Equal(10, limiter.Used(S(30)));
        }
        Assert.True(limiter.TryAcquire(S(60)));
    }

    [Fact]
    public void UsedCountsOnlyWhatIsStillInsideTheWindow()
    {
        GatewayRateLimiter limiter = new();
        limiter.TryAcquire(S(0));
        limiter.TryAcquire(S(10));
        limiter.TryAcquire(S(20));
        Assert.Equal(3, limiter.Used(S(20)));
        Assert.Equal(3, limiter.Used(S(59.9)));
        Assert.Equal(2, limiter.Used(S(60)));
        Assert.Equal(2, limiter.Used(S(69.9)));
        Assert.Equal(1, limiter.Used(S(70)));
        Assert.Equal(0, limiter.Used(S(80)));
    }

    [Fact]
    public void PenalizeBlocksUntilTheEndOfTheCooldownWithoutConsumingSlots()
    {
        GatewayRateLimiter limiter = new();
        limiter.Penalize(S(5), S(30));
        Assert.Equal(0, limiter.Used(S(5)));
        Assert.False(limiter.TryAcquire(S(5)));
        Assert.Equal(S(10), limiter.RetryAfter(S(25)));
        Assert.False(limiter.TryAcquire(S(34.9)));
        Assert.True(limiter.TryAcquire(S(35)));
        Assert.Equal(1, limiter.Used(S(35)));
    }

    [Fact]
    public void ALaterPenaltyExtendsButAnEarlierEndNeverShortens()
    {
        GatewayRateLimiter limiter = new();
        limiter.Penalize(S(0), S(60));
        limiter.Penalize(S(10), S(10));
        Assert.False(limiter.TryAcquire(S(59)));
        Assert.True(limiter.TryAcquire(S(60)));

        GatewayRateLimiter second = new();
        second.Penalize(S(0), S(10));
        second.Penalize(S(5), S(30));
        Assert.False(second.TryAcquire(S(34)));
        Assert.True(second.TryAcquire(S(35)));
    }

    [Fact]
    public void NonPositivePenaltyIsIgnored()
    {
        GatewayRateLimiter limiter = new();
        limiter.Penalize(S(0), TimeSpan.Zero);
        limiter.Penalize(S(0), S(-5));
        Assert.True(limiter.TryAcquire(S(0)));
        Assert.Equal(TimeSpan.Zero, limiter.RetryAfter(S(0)));
    }

    [Fact]
    public void RetryAfterIsTheLaterOfTheCooldownAndTheWindow()
    {
        GatewayRateLimiter limiter = new();
        for (int i = 0; i < 10; i++)
        {
            limiter.TryAcquire(S(0));
        }
        limiter.Penalize(S(10), S(100));
        Assert.Equal(S(100), limiter.RetryAfter(S(10)));
        Assert.Equal(S(50), limiter.RetryAfter(S(60)));

        GatewayRateLimiter other = new();
        for (int i = 0; i < 10; i++)
        {
            other.TryAcquire(S(0));
        }
        other.Penalize(S(10), S(5));
        Assert.Equal(S(50), other.RetryAfter(S(10)));
    }

    [Fact]
    public void ResetClearsBothTheBookkeepingAndTheCooldown()
    {
        GatewayRateLimiter limiter = new();
        for (int i = 0; i < 10; i++)
        {
            limiter.TryAcquire(S(0));
        }
        limiter.Penalize(S(0), S(500));
        Assert.False(limiter.TryAcquire(S(1)));
        limiter.Reset();
        Assert.Equal(0, limiter.Used(S(1)));
        Assert.Equal(TimeSpan.Zero, limiter.RetryAfter(S(1)));
        Assert.True(limiter.TryAcquire(S(1)));
    }

    [Fact]
    public void CustomShapeIsHonored()
    {
        GatewayRateLimiter limiter = new(3, S(5));
        Assert.Equal(3, limiter.MaxRequests);
        Assert.Equal(S(5), limiter.Window);
        Assert.True(limiter.TryAcquire(S(0)));
        Assert.True(limiter.TryAcquire(S(1)));
        Assert.True(limiter.TryAcquire(S(2)));
        Assert.False(limiter.TryAcquire(S(4.9)));
        Assert.True(limiter.TryAcquire(S(5)));
        Assert.False(limiter.TryAcquire(S(5)));
        Assert.True(limiter.TryAcquire(S(6)));
    }

    [Fact]
    public void OneRequestPerWindowIsPossible()
    {
        GatewayRateLimiter limiter = new(1, S(2));
        Assert.True(limiter.TryAcquire(S(0)));
        Assert.False(limiter.TryAcquire(S(1.999)));
        Assert.True(limiter.TryAcquire(S(2)));
    }

    [Fact]
    public void InvalidShapesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayRateLimiter(0, S(60)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayRateLimiter(-1, S(60)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayRateLimiter(10, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GatewayRateLimiter(10, S(-1)));
    }

    [Fact]
    public async Task ConcurrentCallersNeverGetMoreThanTheLimit()
    {
        GatewayRateLimiter limiter = new();
        int admitted = 0;
        Task[] tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                if (limiter.TryAcquire(S(0)))
                {
                    Interlocked.Increment(ref admitted);
                }
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(10, admitted);
        Assert.Equal(10, limiter.Used(S(0)));
    }

    [Fact]
    public async Task StopwatchClockIsMonotonicAndMeasuresRealTime()
    {
        IGatewayClock clock = StopwatchGatewayClock.Default;
        TimeSpan a = clock.Now;
        await Task.Delay(120);
        TimeSpan b = clock.Now;
        Assert.True(b >= a);
        Assert.True(b - a >= TimeSpan.FromMilliseconds(60), "elapsed " + (b - a));
        Assert.True(b - a < TimeSpan.FromSeconds(30), "elapsed " + (b - a));
        Assert.True(a >= TimeSpan.Zero);
    }
}
