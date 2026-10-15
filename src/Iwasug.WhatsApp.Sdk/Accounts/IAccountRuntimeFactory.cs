namespace Iwasug.WhatsApp.Sdk.Accounts;

using Iwasug.WhatsApp.Sdk.Hosting;

/// <summary>Constructs one isolated account runtime.</summary>
internal interface IAccountRuntimeFactory
{
    ValueTask<AccountRuntime> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);

    ValueTask<AccountRuntime> CreateAsync(AccountDescriptor descriptor, CancellationToken cancellationToken = default);
}