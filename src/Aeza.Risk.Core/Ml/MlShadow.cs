using System.Diagnostics;

namespace Aeza.Risk.Core;

/// <summary>Активная теневая модель для поколения</summary>
/// <param name="Id">Номер загруженной модели (ml_models)</param>
/// <param name="Version">Версия из карточки</param>
public sealed record MlModelRef(long Id, string Version);

/// <summary>Прогноз модели для услуги в области применения</summary>
/// <param name="ServiceId">Услуга</param>
/// <param name="Probability">Вероятность «не продлено» по модели (синтетика — не качество на реальных клиентах)</param>
/// <param name="DaysToExpiry">Дней до срока на момент оценки</param>
/// <param name="RulesScore">Балл правил той же услуги в этом поколении</param>
public sealed record MlShadowServiceDto(long ServiceId, double Probability, double DaysToExpiry, decimal? RulesScore);

/// <summary>Теневая модель в карточке клиента: не влияет на балл, уровень, сортировки и деньги</summary>
/// <param name="ModelId">Номер модели</param>
/// <param name="ModelVersion">Версия модели</param>
/// <param name="State">scored — есть прогноз; out_of_scope — нет услуг со сроком в окне модели; failed — инференс не удался (правила не затронуты)</param>
/// <param name="Probability">Наибольшая вероятность «не продлено» среди услуг клиента в окне модели</param>
/// <param name="ServiceId">Услуга с наибольшей вероятностью</param>
/// <param name="Services">Прогнозы по услугам</param>
public sealed record MlShadowDto(long ModelId, string ModelVersion, string State, double? Probability, long? ServiceId, IReadOnlyList<MlShadowServiceDto> Services);

/// <summary>Теневая модель в поколении</summary>
/// <param name="ModelId">Номер модели</param>
/// <param name="Version">Версия</param>
/// <param name="State">scored | failed</param>
/// <param name="ServicesInScope">Услуг в окне модели (active/suspended, срок через ≤ 14 дн. для месяца и дольше, ≤ 3 дн. для недели)</param>
/// <param name="CustomersScored">Клиентов с прогнозом</param>
/// <param name="TopK">Ёмкость сравнения: 5% услуг в окне</param>
/// <param name="TopKOverlap">Сколько услуг из top-K модели есть и в top-K правил</param>
/// <param name="MeanProbability">Средняя вероятность по услугам в окне</param>
/// <param name="DurationMs">Время инференса</param>
/// <param name="ErrorCode">Причина сбоя (timeout, inference_error)</param>
public sealed record MlShadowSummaryDto(
    long ModelId, string Version, string State, int ServicesInScope, int CustomersScored, int TopK, int TopKOverlap, double? MeanProbability, int DurationMs,
    string? ErrorCode);

/// <summary>Прогноз в точке решения E − k: копится для проверки модели на реальных исходах (expiry_outcomes)</summary>
public sealed record MlShadowPrediction(long ServiceId, long AccountId, DateTimeOffset ExpiresAt, int OffsetDays, double Probability, decimal? RulesScore);

/// <summary>Результат теневого расчёта</summary>
public sealed record MlShadowResult(MlShadowSummaryDto Summary, IReadOnlyDictionary<long, MlShadowDto> Customers, IReadOnlyList<MlShadowPrediction> Predictions);

/// <summary>Теневой инференс: те же признаки <see cref="MlFeatures"/>, что при обучении, на входах поколения</summary>
public static class MlShadow
{
    /// <summary>Доля услуг в окне модели для сравнения top-K с правилами (как при обучении)</summary>
    public const double CapacityShare = 0.05;

    /// <param name="model">Модель</param>
    /// <param name="result">Оценки правил этого поколения</param>
    /// <param name="data">Входы поколения</param>
    /// <param name="behavior">Поведение клиентов на момент оценки</param>
    /// <param name="asOf">Момент оценки</param>
    /// <param name="predict">Инференс: строки × признаки (подряд) → вероятности; исключение — сбой</param>
    public static MlShadowResult Run(
        MlModelRef model, EngineResult result, SourceData data, IReadOnlyDictionary<long, AccountBehavior> behavior, DateTimeOffset asOf,
        Func<float[], int, float[]> predict)
    {
        var sw = Stopwatch.StartNew();
        var servicesByOwner = data.Services.ToLookup(s => s.OwnerId);
        var accounts = data.Accounts.ToDictionary(a => a.Id);
        var rows = new List<(CustomerAssessment Customer, ServiceInput Service, decimal? Rules)>();
        foreach (var c in result.Customers)
        {
            var owned = servicesByOwner[c.Account.Id].ToList();
            foreach (var s in owned.Where(s => MlDecision.InScope(s, asOf)).OrderBy(s => s.Id))
                rows.Add((c, s, c.Services.FirstOrDefault(x => x.Id == s.Id)?.Score));
        }
        var n = MlFeatures.Names.Count;
        var batch = new float[rows.Count * n];
        for (var i = 0; i < rows.Count; i++)
        {
            var (c, s, _) = rows[i];
            var a = accounts.GetValueOrDefault(c.Account.Id) ?? c.Account;
            MlFeatures.Compute(s, a, servicesByOwner[a.Id].ToList(), behavior.GetValueOrDefault(a.Id), asOf).CopyTo(batch, i * n);
        }

        float[] p;
        try
        {
            p = rows.Count == 0 ? [] : predict(batch, rows.Count);
            if (p.Length != rows.Count || p.Any(x => !float.IsFinite(x) || x < 0 || x > 1))
                throw new InvalidOperationException("Модель вернула не вероятности");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var code = ex is TimeoutException ? "timeout" : "inference_error";
            var failed = new MlShadowSummaryDto(model.Id, model.Version, "failed", rows.Count, 0, 0, 0, null, (int)sw.ElapsedMilliseconds, code);
            var empty = result.Customers.ToDictionary(c => c.Account.Id, _ => new MlShadowDto(model.Id, model.Version, "failed", null, null, []));
            return new MlShadowResult(failed, empty, []);
        }

        var services = rows.Select((r, i) => (r.Customer.Account.Id, Dto: new MlShadowServiceDto(r.Service.Id, Math.Round(p[i], 4),
            Math.Round((r.Service.ExpiresAt!.Value - asOf).TotalDays, 2), r.Rules), r.Service)).ToList();
        var customers = new Dictionary<long, MlShadowDto>();
        var byCustomer = services.ToLookup(x => x.Id, x => x.Dto);
        foreach (var c in result.Customers)
        {
            var own = byCustomer[c.Account.Id].OrderByDescending(x => x.Probability).ThenBy(x => x.ServiceId).ToList();
            customers[c.Account.Id] = own.Count == 0
                ? new MlShadowDto(model.Id, model.Version, "out_of_scope", null, null, [])
                : new MlShadowDto(model.Id, model.Version, "scored", own[0].Probability, own[0].ServiceId, own);
        }

        var k = rows.Count == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(rows.Count * CapacityShare));
        var topModel = services.OrderByDescending(x => x.Dto.Probability).ThenBy(x => x.Dto.ServiceId).Take(k).Select(x => x.Dto.ServiceId).ToHashSet();
        var overlap = services.OrderByDescending(x => x.Dto.RulesScore ?? -1).ThenBy(x => x.Dto.ServiceId).Take(k).Count(x => topModel.Contains(x.Dto.ServiceId));
        var predictions = services.Select(x => new MlShadowPrediction(x.Dto.ServiceId, x.Id, x.Service.ExpiresAt!.Value,
            DecisionOffset(x.Service, asOf), x.Dto.Probability, x.Dto.RulesScore)).ToList();
        var summary = new MlShadowSummaryDto(model.Id, model.Version, "scored", rows.Count, customers.Values.Count(c => c.State == "scored"), k, overlap,
            rows.Count == 0 ? null : Math.Round(p.Average(x => (double)x), 4), (int)sw.ElapsedMilliseconds, null);
        return new MlShadowResult(summary, customers, predictions);
    }

    /// <summary>Ближайшая точка решения E − k, которую услуга уже прошла (k из <see cref="MlDecision.OffsetsDays"/>)</summary>
    public static int DecisionOffset(ServiceInput s, DateTimeOffset asOf)
    {
        var days = (s.ExpiresAt!.Value - asOf).TotalDays;
        return MlDecision.OffsetsDays(SourceEnums.ParseTerm(s.PaymentTerm)).Where(k => days <= k).DefaultIfEmpty(0).Min();
    }
}
