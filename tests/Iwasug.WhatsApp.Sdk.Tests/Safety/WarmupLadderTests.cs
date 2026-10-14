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

    [Fact]
    public async Task day_zero_ceiling_refuses_21st_send()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var session = await CreateStoresAsync(clock, paired: true);
        var ladder = new WarmupLadder(clock);
        int stage = await ladder.GetCurrentStageAsync(session);
        Assert.Equal(0, stage);
        Assert.Equal(20, ladder.CurrentStageCeiling(stage));

        for (int i = 0; i < 20; i++)
        {
            Assert.True(await ladder.AdmitAsync(stage));
            await ladder.RecordSendAsync(session);
        }

        Assert.False(await ladder.AdmitAsync(stage));
    }

    [Fact]
    public async Task counter_resets_at_utc_midnight()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var session = await CreateStoresAsync(clock, paired: true);
        var ladder = new WarmupLadder(clock);
        int stage = await ladder.GetCurrentStageAsync(session);

        for (int i = 0; i < 20; i++)
        {
            Assert.True(await ladder.AdmitAsync(stage));
            await ladder.RecordSendAsync(session);
        }
        Assert.False(await ladder.AdmitAsync(stage));

        clock.Advance(TimeSpan.FromHours(13));
        Assert.True(await ladder.AdmitAsync(stage));
    }

    [Fact]
    public async Task restart_preserves_stage_derived_ceiling()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var session = await CreateStoresAsync(clock, paired: true);
        var ladder1 = new WarmupLadder(clock);
        int stage = await ladder1.GetCurrentStageAsync(session);

        var ladder2 = new WarmupLadder(clock);
        int restarted = await ladder2.GetCurrentStageAsync(session);
        Assert.Equal(stage, restarted);

        for (int i = 0; i < 20; i++)
        {
            Assert.True(await ladder2.AdmitAsync(restarted));
            await ladder2.RecordSendAsync(session);
        }
        Assert.False(await ladder2.AdmitAsync(restarted));
    }
}