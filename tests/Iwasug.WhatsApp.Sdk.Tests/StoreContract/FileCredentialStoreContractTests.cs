using AwesomeAssertions;
using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Samples;
using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.StoreContract;
using Xunit;

namespace Iwasug.WhatsApp.Sdk.Tests.StoreContract;

/// <summary>
/// File-backed binding: the SAME kit contract class runs against the sample's
/// <see cref="FileCredentialStore"/> over real file IO (plan 03-06 Task 3, CR-04 —
/// the must-not-lose round-trip facts now guard the sample store, so a dropped
/// field like the adv secret fails CI). Each test gets a unique temp directory
/// that is deleted when the test instance is disposed.
/// </summary>
public sealed class FileCredentialStoreContractTests : CredentialStoreContractTests, IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "sdk-wa-fcs-" + Guid.NewGuid().ToString("N"));

    protected override ICredentialStoreFactory CreateFactory() => new TempDirCredentialStoreFactory(_dir);

    /// <summary>
    /// Torn-write backstop (T-3-12): a half-written row (crash mid-write) must be a
    /// loud <see cref="SdkStoreException"/>, never a silent partial record and never
    /// a missing row (failure ≠ missing, D-19).
    /// </summary>
    [Fact]
    public async Task Torn_write_read_throws_SdkStoreException_never_returns_partial_row()
    {
        ICredentialStoreFactory factory = CreateFactory();
        ICredentialStore store = await factory.CreateAsync(AccountId.Create("acct-A"), CancellationToken.None);
        await store.PutAsync(TestRow(), CancellationToken.None);

        // Simulate a crash that left a truncated file behind.
        string file = Directory.EnumerateFiles(_dir, "*.json").Single();
        byte[] bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        bytes.Length.Should().BeGreaterThan(2, "the written row must be large enough to truncate meaningfully");
        await File.WriteAllBytesAsync(file, bytes[..(bytes.Length / 2)], TestContext.Current.CancellationToken);

        Func<Task<CredentialRecord?>> act = async () => await store.GetAsync(CancellationToken.None);
        await act.Should().ThrowAsync<SdkStoreException>("a torn row is corruption, not absence (D-19)");
        await store.DisposeAsync();
    }

    private static CredentialRecord TestRow() => new(
        new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
        "android",
        FixedBytes(0x01),
        FixedBytes(0x21),
        FixedBytes(0x41),
        FixedBytes(0x61),
        "15551234567",
        1,
        42);

    private static byte[] FixedBytes(int start)
    {
        byte[] bytes = new byte[32];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup — never fail a passing test on teardown.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup — never fail a passing test on teardown.
        }
    }

    /// <summary>
    /// Factory over one temp directory: every instance resolves to the same files,
    /// which is what proves cross-instance durability (D-06) against real file IO.
    /// </summary>
    private sealed class TempDirCredentialStoreFactory(string dir) : ICredentialStoreFactory
    {
        public ValueTask<ICredentialStore> CreateAsync(
            AccountId accountId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ICredentialStore>(new FileCredentialStore(accountId, dir));
    }
}
