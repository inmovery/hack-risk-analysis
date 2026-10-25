using System.Text.Json;

namespace Aeza.Risk.Core;

/// <summary>Дерево решений (структура FastTree). Узлы 0…n−1: признак, порог, потомки «≤ порога» и «> порога»;
/// отрицательный потомок — лист с индексом ~child. Покрытие — сколько обучающих строк прошло через узел или лист</summary>
public sealed record TreeModel(
    double Weight, int[] Feature, double[] Threshold, int[] Left, int[] Right, double[] LeafValue, double[] NodeCover, double[] LeafCover)
{
    /// <summary>Значение листа, в который попадает строка</summary>
    public double Value(ReadOnlySpan<float> x)
    {
        if (Feature.Length == 0) return LeafValue[0];
        var n = 0;
        while (true)
        {
            var next = x[Feature[n]] <= Threshold[n] ? Left[n] : Right[n];
            if (next < 0) return LeafValue[~next];
            n = next;
        }
    }
}

/// <summary>Ансамбль деревьев с калибровкой: логит = Slope · (Bias + Σ вес · лист) + Offset, вероятность = σ(логит)</summary>
public sealed record TreeEnsemble(IReadOnlyList<string> Features, double Bias, double Slope, double Offset, IReadOnlyList<TreeModel> Trees)
{
    public double Score(ReadOnlySpan<float> x)
    {
        var s = Bias;
        foreach (var t in Trees) s += t.Weight * t.Value(x);
        return s;
    }

    public double Logit(ReadOnlySpan<float> x) => Slope * Score(x) + Offset;

    public double Probability(ReadOnlySpan<float> x) => Sigmoid(Logit(x));

    public static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));
}

/// <summary>Файл деревьев в пакете модели (trees.json)</summary>
public static class TreeEnsembleJson
{
    public const string FileName = "trees.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(TreeEnsemble model) => JsonSerializer.Serialize(model, Options);

    public static TreeEnsemble Parse(string json) => JsonSerializer.Deserialize<TreeEnsemble>(json, Options) ?? throw new JsonException("trees.json пуст");
}

/// <summary>Вклады признаков в логит прогноза: Base + Σ Phi = логит строки</summary>
/// <param name="Base">Логит при ожидаемом по обучающим данным значении каждого дерева</param>
/// <param name="Phi">Вклад каждого признака в логит</param>
public sealed record ShapValues(double Base, double[] Phi);

/// <summary>Точный TreeSHAP для деревьев (Lundberg et al., 2018, алгоритм 2): условные ожидания — по покрытию узлов обучающими данными</summary>
public static class TreeShap
{
    private struct PathElement
    {
        public int Feature;
        public double Zero;
        public double One;
        public double Weight;
    }

    public static ShapValues Explain(TreeEnsemble model, ReadOnlySpan<float> x)
    {
        var phi = new double[model.Features.Count];
        var expected = model.Bias;
        var treePhi = new double[phi.Length];
        foreach (var tree in model.Trees)
        {
            Array.Clear(treePhi);
            expected += tree.Weight * Expected(tree);
            if (tree.Feature.Length > 0) Recurse(tree, x, treePhi, 0, [], 1, 1, -1);
            for (var i = 0; i < phi.Length; i++) phi[i] += tree.Weight * treePhi[i];
        }
        for (var i = 0; i < phi.Length; i++) phi[i] *= model.Slope;
        return new ShapValues(model.Slope * expected + model.Offset, phi);
    }

    /// <summary>Среднее значение дерева по обучающим данным</summary>
    public static double Expected(TreeModel tree)
    {
        var total = tree.LeafCover.Sum();
        return total <= 0 ? tree.LeafValue.Average() : tree.LeafValue.Select((v, i) => v * tree.LeafCover[i]).Sum() / total;
    }

    private static double Cover(TreeModel t, int node) => node < 0 ? t.LeafCover[~node] : t.NodeCover[node];

    private static void Recurse(TreeModel t, ReadOnlySpan<float> x, double[] phi, int node, PathElement[] path, double zero, double one, int feature)
    {
        path = Extend(path, zero, one, feature);
        if (node < 0)
        {
            var value = t.LeafValue[~node];
            for (var i = 1; i < path.Length; i++)
            {
                var w = Unwind(path, i).Sum(p => p.Weight);
                phi[path[i].Feature] += w * (path[i].One - path[i].Zero) * value;
            }
            return;
        }
        var d = t.Feature[node];
        var (hot, cold) = x[d] <= t.Threshold[node] ? (t.Left[node], t.Right[node]) : (t.Right[node], t.Left[node]);
        double iz = 1, io = 1;
        var k = Array.FindIndex(path, 1, p => p.Feature == d);
        if (k > 0)
        {
            (iz, io) = (path[k].Zero, path[k].One);
            path = Unwind(path, k);
        }
        var cover = Cover(t, node);
        var share = (int child) => cover > 0 ? Cover(t, child) / cover : 0;
        Recurse(t, x, phi, hot, path, iz * share(hot), io, d);
        Recurse(t, x, phi, cold, path, iz * share(cold), 0, d);
    }

    private static PathElement[] Extend(PathElement[] m, double zero, double one, int feature)
    {
        var l = m.Length;
        var r = new PathElement[l + 1];
        Array.Copy(m, r, l);
        r[l] = new PathElement { Feature = feature, Zero = zero, One = one, Weight = l == 0 ? 1 : 0 };
        for (var i = l - 1; i >= 0; i--)
        {
            r[i + 1].Weight += one * r[i].Weight * (i + 1) / (l + 1);
            r[i].Weight = zero * r[i].Weight * (l - i) / (l + 1);
        }
        return r;
    }

    private static PathElement[] Unwind(PathElement[] m, int i)
    {
        var l = m.Length - 1;
        var (zero, one) = (m[i].Zero, m[i].One);
        var n = m[l].Weight;
        var r = new PathElement[l];
        Array.Copy(m, r, l);
        for (var j = l - 1; j >= 0; j--)
        {
            if (one != 0)
            {
                var tmp = r[j].Weight;
                r[j].Weight = n * (l + 1) / ((j + 1) * one);
                n = tmp - r[j].Weight * zero * (l - j) / (l + 1);
            }
            else
            {
                r[j].Weight = r[j].Weight * (l + 1) / (zero * (l - j));
            }
        }
        for (var j = i; j < l; j++)
        {
            r[j].Feature = m[j + 1].Feature;
            r[j].Zero = m[j + 1].Zero;
            r[j].One = m[j + 1].One;
        }
        return r;
    }
}
