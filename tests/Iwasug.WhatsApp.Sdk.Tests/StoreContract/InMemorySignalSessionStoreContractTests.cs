using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Testing.StoreContract;

namespace Iwasug.WhatsApp.Sdk.Tests.StoreContract;

/// <summary>
/// Reference binding: runs the kit's store-contract suite against
/// <see cref="InMemoryStoreFactory"/> — the STR-02 proof that the shipped reference
/// implementation satisfies the contract.
/// </summary>
public sealed class InMemorySignalSessionStoreContractTests : SignalSessionStoreContractTests
{
    protected override ISignalSessionStoreFactory CreateFactory() => new InMemoryStoreFactory();
}
