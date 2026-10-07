using Iwasug.WhatsApp.Sdk.Testing;

namespace Iwasug.WhatsApp.Sdk.Tests;

/// <summary>
/// Runs the kit's completeness backstop in this test assembly. A concrete subclass must
/// exist for the inherited <c>[Fact]</c> to be discovered — that is exactly the property
/// the check enforces for every other contract class.
/// </summary>
public sealed class ComplianceTests : ComplianceTestBase
{
}
