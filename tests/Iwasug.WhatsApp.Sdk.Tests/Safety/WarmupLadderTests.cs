#pragma warning disable xUnit1051
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Safety;
using Iwasug.WhatsApp.Sdk.Stores;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.Safety;

public sealed class WarmupLadderTests
{
    private static async Task<StoreSession> CreateStoresAsync(FakeTimeProvider clock, bool paired)
    {
        var registry = new StoreRegistry(new StoreRegistryOptions());
        var accountId = AccountId.Create("test-account");
        var session = await registry.CreateSessionAsync(accountId);
        if (paired)
        {
            await session.Credentials.PutAsync(new CredentialRecord(
                DateTimeOffset.UtcNow,
                "android",
                new byte[] { 1 },
                new byte[] { 2 },
                new byte[] { 3 },
                new byte[] { 4 },
                "15551234567",
                1,
                1234), default);
        }
        return session;
    }

    [Fact]
    public async Task new_account_starts_stage_0()
    {
        var clock = new FakeTimeProvider();
        var session = await CreateStoresAsync(clock, paired: true);
        var ladder = new WarmupLadder(clock);
        int stage = await ladder.GetCurrentStageAsync(session);
        Assert.Equal(0, stage);
    }

    [Fact]
    public async Task day_progression_advances_stage()
    {
        var clock = new FakeTimeProvider();
        var session = await CreateStoresAsync(clock, paired: true);
        var ladder = new WarmupLadder(clock);
        await ladder.GetCurrentStageAsync(session);
        clock.Advance(TimeSpan.FromDays(3));
        int stage = await ladder.GetCurrentStageAsync(session);
        Assert.Equal(3, stage);
    }

    [Fact]
    public async Task restart_returns_same_stage()
    {
        var clock = new FakeTimeProvider();
        var session = await CreateStoresAsync(clock, paired: true);
        var ladder1 = new WarmupLadder(clock);
        await ladder1.GetCurrentStageAsync(session);
        clock.Advance(TimeSpan.FromDays(2));
        var ladder2 = new WarmupLadder(clock);
        int stage = await ladder2.GetCurrentStageAsync(session);
        Assert.Equal(2, stage);
    }

    [Fact]
    public async Task single_write_creates_row_once()
    {
        var clock = new FakeTimeProvider();
        var session = await CreateStoresAsync(clock, paired: true);
        var ladder = new WarmupLadder(clock);
        await ladder.GetCurrentStageAsync(session);
        var first = await session.SafetyState.GetAsync(default);
        Assert.NotNull(first);
        DateTimeOffset created = first!.CreatedAt;
        await ladder.GetCurrentStageAsync(session);
        var second = await session.SafetyState.GetAsync(default);
        Assert.Equal(created, second!.CreatedAt);
    }

    [Fact]
    public async Task unpaired_no_write()
    {
        var clock = new FakeTimeProvider();
        var session = await CreateStoresAsync(clock, paired: false);
        var ladder = new WarmupLadder(clock);
        int stage = await ladder.GetCurrentStageAsync(session);
        Assert.Equal(0, stage);
        var row = await session.SafetyState.GetAsync(default);
        Assert.Null(row);
    }

    [Fact]
    public async Task created_at_stable()
    {
        var clock = new FakeTimeProvider();
        var session = await CreateStoresAsync(clock, paired: true);
        var ladder = new WarmupLadder(clock);
        await ladder.GetCurrentStageAsync(session);
        var row1 = await session.SafetyState.GetAsync(default);
        await ladder.GetCurrentStageAsync(session);
        var row2 = await session.SafetyState.GetAsync(default);
        Assert.Equal(row1!.CreatedAt, row2!.CreatedAt);
    }
}