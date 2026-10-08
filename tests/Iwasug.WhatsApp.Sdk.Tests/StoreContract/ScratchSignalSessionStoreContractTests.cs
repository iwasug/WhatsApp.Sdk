using Iwasug.WhatsApp.Sdk.Stores;
using Iwasug.WhatsApp.Sdk.Testing.StoreContract;

namespace Iwasug.WhatsApp.Sdk.Tests.StoreContract;

/// <summary>
/// Second-implementation binding: the SAME kit contract class runs against
/// <see cref="ScratchStoreFactory"/> (lock-guarded dictionaries) with zero SDK changes —
/// success criterion 3 / D-11 proven for the signal-session seam.
/// </summary>
public sealed class ScratchSignalSessionStoreContractTests : SignalSessionStoreContractTests
{
    protected override ISignalSessionStoreFactory CreateFactory() => new ScratchStoreFactory();
}
