using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Stores.InMemory;
using Iwasug.WhatsApp.Sdk.Testing.StoreContract;

namespace Iwasug.WhatsApp.Sdk.Tests.StoreContract;

/// <summary>
/// Reference binding: runs the kit's safety-state-store contract against
/// <see cref="InMemoryStoreFactory"/> (STR-02).
/// </summary>
public sealed class InMemorySafetyStateStoreContractTests : SafetyStateStoreContractTests
{
    protected override ISafetyStateStoreFactory CreateFactory() => new InMemoryStoreFactory();
}
