using Aeza.Risk.Core;

using Microsoft.ML;
using Microsoft.ML.Calibrators;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;

namespace Aeza.Risk.Ml;

/// <summary>Структура обученного FastTree для TreeSHAP: деревья, калибровка Платта и покрытие узлов обучающими строками</summary>
public static class TreeExport
{
    /// <summary>Расхождение вероятности деревьев с ML.NET, при котором экспорт отклоняется</summary>
    public const double MaxParityDiff = 1e-4;

    /// <summary>Деревья модели или null, если модель не FastTree</summary>
    /// <param name="model">Обученная модель</param>
    /// <param name="features">Имена признаков в порядке вектора</param>
    /// <param name="train">Обучающие строки: по ним считается покрытие узлов (условные ожидания TreeSHAP)</param>
    /// <param name="probe">Строки для сверки с ML.NET</param>
    /// <param name="expected">Вероятности ML.NET на <paramref name="probe"/></param>
    public static TreeEnsemble? From(ITransformer model, IReadOnlyList<string> features, IReadOnlyList<float[]> train, IReadOnlyList<float[]> probe,
        IReadOnlyList<double> expected)
    {
        if (model is not BinaryPredictionTransformer<CalibratedModelParametersBase<FastTreeBinaryModelParameters, PlattCalibrator>> bt) return null;
        var ensemble = bt.Model.SubModel.TrainedTreeEnsemble;
        var trees = new List<TreeModel>(ensemble.Trees.Count);
        for (var t = 0; t < ensemble.Trees.Count; t++)
        {
            var tree = ensemble.Trees[t];
            if (tree.CategoricalSplitFlags.Any(c => c)) throw new InvalidOperationException("Категориальные разбиения не поддерживаются");
            var shape = new TreeModel(ensemble.TreeWeights[t], tree.NumericalSplitFeatureIndexes.ToArray(),
                tree.NumericalSplitThresholds.Select(x => (double)x).ToArray(), tree.LeftChild.ToArray(), tree.RightChild.ToArray(),
                tree.LeafValues.ToArray(), new double[tree.NumberOfNodes], new double[tree.NumberOfLeaves]);
            foreach (var x in train) Route(shape, x);
            trees.Add(shape);
        }

        // ML.NET: p = 1 / (1 + exp(−(a·s + b))); знак проверяется сверкой, чтобы не зависеть от версии
        var calibrator = bt.Model.Calibrator;
        foreach (var sign in new[] { 1.0, -1.0 })
        {
            var candidate = new TreeEnsemble(features, ensemble.Bias, sign * calibrator.Slope, sign * calibrator.Offset, trees);
            var diff = probe.Select((x, i) => Math.Abs(candidate.Probability(x) - expected[i])).DefaultIfEmpty(0).Max();
            if (diff <= MaxParityDiff) return candidate;
        }
        throw new InvalidOperationException("Деревья расходятся с ML.NET: структура модели прочитана неверно");
    }

    /// <summary>Строка проходит дерево: +1 к покрытию каждого узла пути и листа</summary>
    private static void Route(TreeModel t, float[] x)
    {
        if (t.Feature.Length == 0)
        {
            t.LeafCover[0]++;
            return;
        }
        var n = 0;
        while (true)
        {
            t.NodeCover[n]++;
            var next = x[t.Feature[n]] <= t.Threshold[n] ? t.Left[n] : t.Right[n];
            if (next < 0)
            {
                t.LeafCover[~next]++;
                return;
            }
            n = next;
        }
    }
}
