using System;
using System.Threading.Tasks;

namespace MuteMeControl.Tests;

public class MuteMeColorTest
{
    [Test]
    public async Task ColorsContainRedAndGreen()
    {
        string[] colors = Enum.GetNames(typeof(MuteMeColor));
        await Assert.That(colors).Contains("Red");
        await Assert.That(colors).Contains("Green");
    }
}