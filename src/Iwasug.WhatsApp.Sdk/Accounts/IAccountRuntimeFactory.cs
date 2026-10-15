namespace Iwasug.WhatsApp.Sdk.Accounts;

/// <summary>Constructs one isolated account runtime.</summary>
internal interface IAccountRuntimeFactory
{
    ValueTask<AccountRuntime> CreateAsync(AccountId accountId, CancellationToken cancellationToken = default);
}