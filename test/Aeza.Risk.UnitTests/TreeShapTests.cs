using Aeza.Risk.Core;

using Xunit;

namespace Aeza.Risk.UnitTests;

/// <summary>TreeSHAP совпадает с точными значениями Шепли полным перебором и в сумме даёт логит прогноза</summary>
public sealed class TreeShapTests
{
    private const int Features = 4;

    /// <summary>Случайное дерево: покрытие узла — сумма покрытий потомков</summary>
    private static TreeModel RandomTree(Random rnd, int depth)
    {
        var feature = new List<int>();
        var threshold = new List<double>();
        var left = new List<int>();
        var right = new List<int>();
        var leafValue = new List<double>();
        var nodeCover = new List<double>();
        var leafCover = new List<double>();

        (int Index, double Cover) Build(int level)
        {
            if (level == depth || (level > 0 && rnd.NextDouble() < 0.25))
            {
                leafValue.Add(rnd.NextDouble() * 4 - 2);
                var c = 1 + rnd.Next(1, 50);
                leafCover.Add(c);
                return (~(leafValue.Count - 1), c);
            }
            var n = feature.Count;
            feature.Add(rnd.Next(Features));
            threshold.Add(rnd.NextDouble());
            left.Add(0);
            right.Add(0);
            nodeCover.Add(0);
            var (l, lc) = Build(level + 1);
            var (r, rc) = Build(level + 1);
            left[n] = l;
            right[n] = r;
            nodeCover[n] = lc + rc;
            return (n, lc + rc);
        }

        Build(0);
        return new TreeModel(0.5 + rnd.NextDouble(), feature.ToArray(), threshold.ToArray(), left.ToArray(), right.ToArray(), leafValue.ToArray(),
            nodeCover.ToArray(), leafCover.ToArray());
    }

    /// <summary>E[f(x) | x_S] по покрытию узлов (алгоритм 1 статьи)</summary>
    private static double Conditional(TreeModel t, float[] x, ISet<int> s, int node)
    {
        if (node < 0) return t.LeafValue[~node];
        double Cover(int n) => n < 0 ? t.LeafCover[~n] : t.NodeCover[n];
        if (s.Contains(t.Feature[node])) return Conditional(t, x, s, x[t.Feature[node]] <= t.Threshold[node] ? t.Left[node] : t.Right[node]);
        return (Conditional(t, x, s, t.Left[node]) * Cover(t.Left[node]) + Conditional(t, x, s, t.Right[node]) * Cover(t.Right[node])) / t.NodeCover[node];
    }

    private static double[] BruteForce(TreeEnsemble m, float[] x)
    {
        double V(ISet<int> s) => m.Slope * (m.Bias + m.Trees.Sum(t => t.Weight * Conditional(t, x, s, 0))) + m.Offset;
        static double Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);
        var phi = new double[Features];
        for (var i = 0; i < Features; i++)
            for (var mask = 0; mask < 1 << Features; mask++)
            {
                if ((mask & (1 << i)) != 0) continue;
                var s = Enumerable.Range(0, Features).Where(f => (mask & (1 << f)) != 0).ToHashSet();
                var weight = Factorial(s.Count) * Factorial(Features - s.Count - 1) / Factorial(Features);
                var with = new HashSet<int>(s) { i };
                phi[i] += weight * (V(with) - V(s));
            }
        return phi;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MatchesExactShapley_AndSumsToLogit(int seed)
    {
        var rnd = new Random(seed);
        var model = new TreeEnsemble(Enumerable.Range(0, Features).Select(i => $"f{i}").ToList(), 0.3, 1.7, -0.4,
            Enumerable.Range(0, 3).Select(_ => RandomTree(rnd, 4)).ToList());
        for (var k = 0; k < 20; k++)
        {
            var x = Enumerable.Range(0, Features).Select(_ => (float)rnd.NextDouble()).ToArray();
            var shap = TreeShap.Explain(model, x);
            var exact = BruteForce(model, x);
            for (var i = 0; i < Features; i++) Assert.Equal(exact[i], shap.Phi[i], 9);
            Assert.Equal(model.Logit(x), shap.Base + shap.Phi.Sum(), 9);
        }
    }

    [Fact]
    public void FeatureNotInAnySplit_GetsZero()
    {
        var tree = new TreeModel(1, [0], [0.5], [~0], [~1], [-1, 1], [10], [5, 5]);
        var model = new TreeEnsemble(["a", "b"], 0, 1, 0, [tree]);
        var shap = TreeShap.Explain(model, [0.9f, 0.1f]);
        Assert.Equal(0, shap.Phi[1]);
        Assert.Equal(1, shap.Phi[0], 9); // ожидание 0, у строки 1
        Assert.Equal(0, shap.Base, 9);
    }
}
