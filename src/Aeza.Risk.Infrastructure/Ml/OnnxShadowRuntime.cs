using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Aeza.Risk.Infrastructure.Ml;

/// <summary>ONNX Runtime in-process (CPU) для теневой модели</summary>
public sealed class OnnxShadowRuntime : IDisposable
{
    public const string Input = "Features";
    public const string Output = "Probability.output";

    private readonly Lock _gate = new();
    private InferenceSession? _session;

    /// <summary>Номер загруженной модели</summary>
    public long? LoadedModelId { get; private set; }

    public void Load(long modelId, byte[] onnx)
    {
        lock (_gate)
        {
            if (LoadedModelId == modelId && _session is not null) return;
            var session = Open(onnx);
            _session?.Dispose();
            _session = session;
            LoadedModelId = modelId;
        }
    }

    /// <summary>Вероятности для <paramref name="rows"/> строк; дольше <paramref name="timeout"/> — <see cref="TimeoutException"/></summary>
    public float[] Predict(float[] batch, int rows, TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_session is null) throw new InvalidOperationException("Модель не загружена");
            return Run(_session, batch, rows, timeout);
        }
    }

    /// <summary>Проверка ONNX перед включением</summary>
    public static void Validate(byte[] onnx)
    {
        InferenceSession session;
        try
        {
            session = Open(onnx);
        }
        catch (OnnxRuntimeException)
        {
            throw new MlModelRejectedException("onnx_invalid", "ONNX не открывается ONNX Runtime");
        }
        using (session)
        {
            if (!session.InputMetadata.TryGetValue(Input, out var input) || input.ElementType != typeof(float)
                || input.Dimensions.Length != 2 || input.Dimensions[1] != MlFeatures.Names.Count)
                throw new MlModelRejectedException("onnx_invalid", $"Вход ONNX должен быть {Input}: float [N × {MlFeatures.Names.Count}]");
            if (!session.OutputMetadata.ContainsKey(Output))
                throw new MlModelRejectedException("onnx_invalid", $"В ONNX нет выхода {Output}");
            var probe = new float[2 * MlFeatures.Names.Count];
            probe[MlFeatures.Names.Count + MlFeatures.Index["cpu_ratio"]] = 1;
            probe[MlFeatures.Names.Count + MlFeatures.Index["csat_min_30"]] = 5;
            probe[MlFeatures.Names.Count + MlFeatures.Index["nps_min_90"]] = 10;
            float[] p;
            try
            {
                p = Run(session, probe, 2, TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or TimeoutException or InvalidOperationException)
            {
                throw new MlModelRejectedException("onnx_output_invalid", "ONNX не выполнился на проверочных строках");
            }
            if (p.Length != 2 || p.Any(x => !float.IsFinite(x) || x < 0 || x > 1))
                throw new MlModelRejectedException("onnx_output_invalid", $"{Output} должен давать вероятность 0..1 на строку");
        }
    }

    private static InferenceSession Open(byte[] onnx)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC };
        return new InferenceSession(onnx, options);
    }

    private static float[] Run(InferenceSession session, float[] batch, int rows, TimeSpan timeout)
    {
        var tensor = new DenseTensor<float>(batch, [rows, MlFeatures.Names.Count]);
        using var run = new RunOptions();
        using var cts = new CancellationTokenSource(timeout);
        using var registration = cts.Token.Register(() => run.Terminate = true);
        try
        {
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(Input, tensor)], [Output], run);
            var p = results.Single().AsEnumerable<float>().ToArray();
            // Выход может быть [N] или [N × 1]
            return p.Length == rows ? p : throw new InvalidOperationException("Размер выхода не совпадает с числом строк");
        }
        catch (OnnxRuntimeException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"Инференс дольше {timeout.TotalSeconds:0} с");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            LoadedModelId = null;
        }
    }
}
