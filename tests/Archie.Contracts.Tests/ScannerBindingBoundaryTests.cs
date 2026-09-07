using Xunit;

namespace Archie.Contracts.Tests;

public sealed class ScannerBindingBoundaryTests
{
    [Fact]
    public void ScannerBindingDoesNotReferenceCoreContracts()
    {
        var references = typeof(ProtocolMessage).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => reference.Name == "Archie.Contracts");
        Assert.Null(typeof(ProtocolMessage).Assembly.GetType("Archie.Contracts.GraphSnapshot"));
    }
}
