using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.StoreContract;

namespace Iwasug.WhatsApp.Sdk.Tests.StoreContract;

/// <summary>
/// Second-implementation binding: the SAME kit contract class runs against
/// <see cref="ScratchStoreFactory"/> with zero SDK changes (success criterion 3 / D-11).
/// </summary>
public sealed class ScratchIdentityStoreContractTests : IdentityStoreContractTests
{
    protected override IIdentityStoreFactory CreateFactory() => new ScratchStoreFactory();
}
