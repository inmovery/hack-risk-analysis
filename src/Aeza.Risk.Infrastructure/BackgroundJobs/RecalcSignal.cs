using Aeza.Risk.Core.Abstractions;

namespace Aeza.Risk.Infrastructure.BackgroundJobs;

/// <summary>Сигнал worker'у «появилась работа» (in-process; durable часть — строка recalc_jobs в БД)</summary>
public sealed class RecalcSignal : IRecalcSignal, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    /// <inheritdoc />
    public void Notify() => _signal.Release();

    /// <summary>Ждать сигнала или таймаута; всплеск сигналов схлопывается</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _signal.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // таймаут ожидания
        }
        while (_signal.CurrentCount > 0) _signal.Wait(0);
    }

    /// <inheritdoc />
    public void Dispose() => _signal.Dispose();
}
