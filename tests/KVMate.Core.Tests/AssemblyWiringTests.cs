using System.Reflection;
using Xunit;

namespace KVMate.Core.Tests;

public class AssemblyWiringTests
{
    [Fact]
    public void Core_assembly_loads()
    {
        var core = Assembly.Load("KVMate.Core");

        Assert.Equal("KVMate.Core", core.GetName().Name);
    }
}
