using System.Threading;
using System.Threading.Tasks;

using AudioControl;

using Microsoft.Extensions.Logging;

using Moq;

using MuteMeControl.Services;

using Utilities;

namespace MuteMeControl.Tests;

[Explicit]
public class MuteMeButtonTests
{
    [Test]
    public async Task CanCycleColors()
    {
        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
        });

        ILogger<Button> logger = loggerFactory.CreateLogger<Button>();

        Mock<IMicrophone> microphone = new();

        IBackgroundQueue queue = new Mock<IBackgroundQueue>().Object;

        Button device = Button.FromMicrophoneAndQueueAndLogger(microphone.Object, queue, logger);
        await device.CycleColors(CancellationToken.None);
    }
}