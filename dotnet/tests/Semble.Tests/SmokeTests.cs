using Xunit;

namespace Semble.Tests;

public class SmokeTests
{
    [Fact]
    public void Pipeline_Compiles_And_Runs()
    {
        Assert.Equal(2, 1 + 1);
    }
}
