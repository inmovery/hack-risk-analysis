using AutoFixture;
using AutoFixture.AutoMoq;

using Microsoft.Extensions.Logging;

using Moq;

using Xunit;

namespace Aeza.Risk.UnitTests;

/// <summary>Базовый класс для unit-тестов: фикстура AutoFixture (+AutoMoq), мок логгера, контроль ошибок в логе и время выполнения</summary>
public abstract class BaseUnitTest : IDisposable
{
    protected readonly Mock<ILogger> Logger;
    protected readonly ITestOutputHelper Output;
    protected readonly System.Diagnostics.Stopwatch Stopwatch = System.Diagnostics.Stopwatch.StartNew();
    protected readonly IFixture Fixture;

    protected BaseUnitTest(ITestOutputHelper output)
    {
        Output = output;
        Logger = new Mock<ILogger>();
        Fixture = new Fixture().Customize(new AutoMoqCustomization());
        Logger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(() => true);
    }

    public virtual void Dispose()
    {
        Stopwatch.Stop();
        Output.WriteLine($"Execution time: {Stopwatch.Elapsed.TotalSeconds}");
        AssertLogErrors(Times.Never());
        GC.SuppressFinalize(this);
    }

    protected virtual void AssertLogErrors(Times times)
    {
        Logger.Verify(x => x.Log(
                It.Is<LogLevel>(level => level >= LogLevel.Warning),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
    }
}
