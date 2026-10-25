using System.Diagnostics.Metrics;
using System.Globalization;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Generations;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Aeza.Risk.Infrastructure.Metrics;

/// <summary>Имена бизнес-метрик. В Prometheus точки заменяются подчёркиваниями, единица «s» даёт суффикс _seconds</summary>
public static class MetricNames
{
    /// <summary>Клиенты по уровню риска непродления (label risk_level: high/medium/low/unknown, взаимоисключающие)</summary>
    public const string Customers = "aeza.customers";

    /// <summary>Клиентов в расчёте</summary>
    public const string CustomersCount = "aeza.customers.count";

    /// <summary>Клиенты по состоянию оценки (label score_state)</summary>
    public const string CustomersByState = "aeza.customers.by_state";

    /// <summary>Клиенты по уровню индекса «уход целиком» (label risk_level)</summary>
    public const string FullChurn = "aeza.customers.full_churn";

    /// <summary>Клиенты по состоянию индекса «уход целиком» (label state; churned — уже ушли)</summary>
    public const string FullChurnByState = "aeza.customers.full_churn.by_state";

    /// <summary>Клиенты по тренду платежей (label trend)</summary>
    public const string PaymentTrend = "aeza.customers.payment_trend";

    /// <summary>Сумма score оценённых клиентов (среднее = sum / scored)</summary>
    public const string ScoreSum = "aeza.risk.score.sum";

    /// <summary>Оценённых клиентов</summary>
    public const string ScoredCustomers = "aeza.risk.scored_customers";

    /// <summary>Выручка под риском (главный KPI), евро</summary>
    public const string MoneyAtRisk = "aeza.money.at_risk.euros";

    /// <summary>Плановые продления, евро</summary>
    public const string MoneyPlanned = "aeza.money.planned_renewals.euros";

    /// <summary>Не покрыто балансом, евро</summary>
    public const string MoneyUnfunded = "aeza.money.unfunded.euros";

    /// <summary>Выручка под риском по уровню клиента (label risk_level), евро</summary>
    public const string MoneyAtRiskByLevel = "aeza.money.at_risk.by_level.euros";

    /// <summary>Исходы сроков из журнала (label outcome: renewed/not_renewed/excluded/pending)</summary>
    public const string ExpiryOutcomes = "aeza.expiry_outcomes";

    /// <summary>Горизонт денег и прогноза, дни</summary>
    public const string HorizonDays = "aeza.horizon.days";

    /// <summary>Номер опубликованного поколения расчёта (сверка с API и заголовком X-Risk-Generation)</summary>
    public const string Generation = "aeza.generation";

    /// <summary>Момент оценки поколения (asOf), unix-секунды</summary>
    public const string GenerationAsOf = "aeza.generation.as_of.timestamp";

    /// <summary>Возраст данных источника (с последнего события или снимка), секунды</summary>
    public const string SourceAge = "aeza.source.age";

    /// <summary>Чем посчитан основной балл поколения (label mode: rules/ml, значение 1 у использованного)</summary>
    public const string Scoring = "aeza.scoring";

    /// <summary>90 % интервал выручки под риском (label bound: low/high), евро — только когда балл считает ML</summary>
    public const string MoneyAtRiskInterval = "aeza.money.at_risk.interval.euros";
}

/// <summary>Неизменяемый срез бизнес-показателей одного поколения (то, что видит один scrape)</summary>
/// <param name="GenerationId">Поколение</param>
/// <param name="AsOf">Момент оценки</param>
/// <param name="SourceUpdatedAt">Последнее изменение источника на момент расчёта</param>
/// <param name="Summary">Сводка поколения (та же, что отдаёт API)</param>
/// <param name="ExpiryOutcomes">Исходы сроков из журнала</param>
public sealed record BusinessMetricsSnapshot(
    long GenerationId, DateTimeOffset AsOf, DateTimeOffset SourceUpdatedAt, GenerationSummary Summary, IReadOnlyDictionary<string, int> ExpiryOutcomes);

/// <summary>Текущий срез бизнес-показателей</summary>
public sealed class BusinessMetricsState
{
    private readonly AsyncLocal<BusinessMetricsSnapshot?> _pinned = new();
    private volatile BusinessMetricsSnapshot? _current;
    private readonly SemaphoreSlim _refresh = new(0, 1);

    /// <summary>Срез для текущего сбора</summary>
    public BusinessMetricsSnapshot? Read() => _pinned.Value ?? _current;

    /// <summary>Заменить срез (после чтения из БД)</summary>
    public void Set(BusinessMetricsSnapshot? snapshot) => _current = snapshot;

    /// <summary>Закрепить текущий срез на время запроса</summary>
    public BusinessMetricsSnapshot? Pin() => _pinned.Value = _current;

    /// <summary>Поколение опубликовано — обновить срез, не дожидаясь периода</summary>
    public void RequestRefresh()
    {
        if (_refresh.CurrentCount == 0) _refresh.Release();
    }

    /// <summary>Ожидать запроса обновления или истечения периода</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _refresh.WaitAsync(timeout, ct);
}

/// <summary>Бизнес-метрики (ObservableGauge)</summary>
public sealed class RiskMetrics : IDisposable
{
    private readonly Meter _meter;

    /// <summary>Регистрирует инструменты на Meter из <see cref="MetricsOptions.MeterName"/></summary>
    public RiskMetrics(IMeterFactory meterFactory, IOptions<MetricsOptions> options, BusinessMetricsState state, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(options);
        _meter = meterFactory.Create(options.Value.MeterName);

        Labeled(MetricNames.Customers, "Клиенты по уровню риска непродления (score)", "risk_level", s => s.Summary.Customers.ByLevel);
        Single(MetricNames.CustomersCount, "Клиентов в расчёте", s => s.Summary.Customers.Total);
        Labeled(MetricNames.CustomersByState, "Клиенты по состоянию оценки", "score_state", s => s.Summary.Customers.ByState);
        Labeled(MetricNames.FullChurn, "Клиенты по уровню индекса «уход целиком» (минимум score действующих услуг)", "risk_level",
            s => s.Summary.Churn?.FullChurn.ByLevel);
        Labeled(MetricNames.FullChurnByState, "Клиенты по состоянию индекса «уход целиком» (churned — все услуги удалены)", "state",
            s => s.Summary.Churn?.FullChurn.ByState);
        Labeled(MetricNames.PaymentTrend, "Клиенты по тренду выполненных платежей", "trend", s => s.Summary.Churn?.PaymentTrend.ByState);
        Single(MetricNames.ScoreSum, "Сумма score оценённых клиентов (индекс, не вероятность)", s => (double)s.Summary.Customers.ScoreSum);
        Single(MetricNames.ScoredCustomers, "Оценённых клиентов", s => s.Summary.Customers.Scored);
        Single(MetricNames.MoneyAtRisk, "Выручка под риском на горизонте: Σ продлений × score/100, евро", s => Eur(s.Summary.Money.AtRiskMinor));
        Single(MetricNames.MoneyPlanned, "Плановые продления на горизонте, евро", s => Eur(s.Summary.Money.PlannedRenewalsMinor));
        Single(MetricNames.MoneyUnfunded, "Не покрыто балансом и разрешёнными бонусами на горизонте, евро", s => Eur(s.Summary.Money.UnfundedMinor));
        Labeled(MetricNames.MoneyAtRiskByLevel, "Выручка под риском по уровню клиента, евро", "risk_level",
            s => s.Summary.Money.ByLevel.ToDictionary(k => k.Key, k => Eur(k.Value.AtRiskMinor)));
        Labeled(MetricNames.ExpiryOutcomes, "Исходы сроков из журнала: продлено / не продлено за N дней / исключено / ждёт", "outcome", s => s.ExpiryOutcomes);
        Single(MetricNames.HorizonDays, "Горизонт денег и прогноза, дни", s => s.Summary.Money.HorizonDays);
        Single(MetricNames.Generation, "Номер опубликованного поколения расчёта", s => s.GenerationId);
        Single(MetricNames.GenerationAsOf, "Момент оценки поколения (asOf), unix-время", s => s.AsOf.ToUnixTimeSeconds(), "s");
        Single(MetricNames.SourceAge, "Возраст данных источника (с последнего события или снимка)",
            s => Math.Max(0, (time.GetUtcNow() - s.SourceUpdatedAt).TotalSeconds), "s");
        Labeled(MetricNames.MoneyAtRiskInterval, "90 % интервал выручки под риском, евро (только в режиме ML: балл — вероятность)", "bound",
            s => s.Summary.Money.AtRiskInterval is { } i ? new Dictionary<string, double> { ["low"] = Eur(i.LowMinor), ["high"] = Eur(i.HighMinor) } : null);
        Labeled(MetricNames.Scoring, "Чем посчитан основной балл поколения: 1 — этот режим (rules — правила, ml — только ML-модель)", "mode",
            s => new Dictionary<string, int>
            {
                [ScoringModes.Rules] = s.Summary.Scoring?.Used == ScoringModes.Ml ? 0 : 1,
                [ScoringModes.Ml] = s.Summary.Scoring?.Used == ScoringModes.Ml ? 1 : 0,
            });

        void Single(string name, string description, Func<BusinessMetricsSnapshot, double> value, string? unit = null) =>
            _meter.CreateObservableGauge(name, () => state.Read() is { } s ? [new Measurement<double>(value(s))] : Array.Empty<Measurement<double>>(), unit, description);

        void Labeled<T>(string name, string description, string label, Func<BusinessMetricsSnapshot, IReadOnlyDictionary<string, T>?> values)
            where T : struct =>
            _meter.CreateObservableGauge(name, () =>
            {
                if (state.Read() is not { } s || values(s) is not { } map) return Array.Empty<Measurement<double>>();
                return map.OrderBy(k => k.Key, StringComparer.Ordinal)
                    .Select(k => new Measurement<double>(Convert.ToDouble(k.Value, CultureInfo.InvariantCulture), new KeyValuePair<string, object?>(label, k.Key)))
                    .ToArray();
            }, unit: null, description: description);
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static double Eur(long minor) => (double)(minor / 100m);
}

/// <summary>Держит срез бизнес-показателей в соответствии с БД</summary>
public sealed class BusinessMetricsRefresher(
    IServiceScopeFactory scopes, BusinessMetricsState state, IOptions<MetricsOptions> options, ILogger<BusinessMetricsRefresher> log) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(options.Value.RefreshSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                state.Set(await LoadAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Business metrics refresh failed; previous values kept");
            }
            try
            {
                await state.WaitAsync(period, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<BusinessMetricsSnapshot?> LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeId = await db.AppState.AsNoTracking().Select(s => s.ActiveGenerationId).SingleOrDefaultAsync(ct);
        if (activeId is not { } id) return null;
        var gen = await db.Generations.AsNoTracking().Where(g => g.Id == id)
            .Select(g => new { g.Id, g.AsOf, g.SourceUpdatedAt, g.SummaryJson, g.SourceSnapshotId }).SingleOrDefaultAsync(ct);
        if (gen is null) return null;
        var sourceAsOf = await db.SourceSnapshots.AsNoTracking().Where(s => s.Id == gen.SourceSnapshotId).Select(s => s.SourceAsOf).SingleAsync(ct);
        var outcomes = await db.ExpiryOutcomes.AsNoTracking().GroupBy(o => o.Outcome).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var byOutcome = new[] { "renewed", "not_renewed", "excluded", "pending" }
            .ToDictionary(k => k, k => outcomes.Where(o => (o.Key ?? "pending") == k).Sum(o => o.Count));
        var updated = gen.SourceUpdatedAt is { } u && u > sourceAsOf ? u : sourceAsOf;
        return new BusinessMetricsSnapshot(gen.Id, gen.AsOf, updated, AppJson.Deserialize<GenerationSummary>(gen.SummaryJson), byOutcome);
    }
}

/// <summary>Регистрация бизнес-метрик и конечной точки Prometheus</summary>
public static class MetricsEntry
{
    /// <summary>OpenTelemetry MeterProvider только с Meter бизнес-метрик (без process/runtime/HTTP-инструментирования) и экспортёр Prometheus</summary>
    public static IServiceCollection AddBusinessMetrics(this IServiceCollection services, MetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<BusinessMetricsState>();
        services.AddSingleton<RiskMetrics>();
        services.AddHostedService<BusinessMetricsRefresher>();
        if (!options.EnableOpenTelemetry) return services;

        var assembly = typeof(MetricsEntry).Assembly.GetName();
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName: "aeza-risk", serviceVersion: assembly.Version?.ToString() ?? "unknown",
                serviceInstanceId: Environment.MachineName))
            .WithMetrics(mb =>
            {
                mb.AddMeter(options.MeterName);
                // Без кэша ответа: значения и заголовок X-Risk-Generation одного scrape — из одного среза
                if (options.EnablePrometheus) mb.AddPrometheusExporter(o => o.ScrapeResponseCacheDurationMilliseconds = 0);
            });
        return services;
    }

    /// <summary>GET /metrics без авторизации (scrape Prometheus внутри сети; наружу путь закрыт nginx/ingress), не входит в Swagger</summary>
    public static IApplicationBuilder UseBusinessMetricsEndpoint(this IApplicationBuilder app, MetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.EnableOpenTelemetry || !options.EnablePrometheus) return app;
        // Инструменты создаются при первом обращении к сервису — создаём заранее, чтобы первый scrape их уже видел
        app.ApplicationServices.GetRequiredService<RiskMetrics>();
        var path = new PathString(options.MetricsEndpointPath);
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                var pinned = ctx.RequestServices.GetRequiredService<BusinessMetricsState>().Pin();
                if (pinned is not null) ctx.Response.Headers["X-Risk-Generation"] = pinned.GenerationId.ToString(CultureInfo.InvariantCulture);
            }
            await next(ctx);
        });
        app.UseOpenTelemetryPrometheusScrapingEndpoint(meterProvider: null, predicate: null, path: options.MetricsEndpointPath,
            configureBranchedPipeline: null, optionsName: null);
        return app;
    }
}
