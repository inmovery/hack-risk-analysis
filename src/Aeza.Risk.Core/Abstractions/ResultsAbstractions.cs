using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Generations;

namespace Aeza.Risk.Core.Abstractions;

/// <summary>Выбор поколения результатов: активное или явно запрошенное</summary>
public interface IGenerationReader
{
    /// <summary>Поколение и состояние очереди; все ответы одного запроса строятся из него</summary>
    Task<GenerationLookup> ResolveAsync(long? generationId, CancellationToken cancellationToken);
}

/// <summary>Критерии списка клиентов (уже проверенные)</summary>
/// <param name="SortBy">id | email | balance | totalReplenished | riskScore | atRisk | fullChurnRisk | paymentChange</param>
/// <param name="Ascending">Направление сортировки</param>
/// <param name="RiskLevels">Фильтр уровней</param>
/// <param name="ScoreState">Фильтр состояния оценки</param>
/// <param name="Flag">Фильтр флага качества</param>
/// <param name="Search">ID или часть email</param>
/// <param name="Page">Номер страницы (с 1)</param>
/// <param name="PageSize">Размер страницы</param>
/// <param name="FullChurnLevels">Фильтр уровней индекса «уход целиком»</param>
/// <param name="FullChurnState">Фильтр состояния индекса «уход целиком» (churned — уже ушли)</param>
/// <param name="PaymentTrends">Фильтр тренда платежей</param>
public sealed record CustomerListCriteria(
    string SortBy, bool Ascending, IReadOnlyList<string>? RiskLevels, string? ScoreState, string? Flag, string? Search, int Page, int PageSize,
    IReadOnlyList<string>? FullChurnLevels = null, string? FullChurnState = null, IReadOnlyList<string>? PaymentTrends = null);

/// <summary>Страница списка клиентов</summary>
public sealed record CustomerListPage(int Total, IReadOnlyList<CustomerResultEntity> Rows);

/// <summary>Оценка клиента в поколении (для сравнения в предпросмотре)</summary>
public sealed record CustomerScoreRow(long CustomerId, string Email, decimal? Score, string Level, long AtRiskMinor);

/// <summary>Результаты клиентов в поколении</summary>
public interface ICustomerResultsRepository
{
    /// <summary>Фильтрация, сортировка по всему набору и страница</summary>
    Task<CustomerListPage> ListAsync(long generationId, CustomerListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>Сохранённая деталь клиента (jsonb) или null</summary>
    Task<string?> GetDetailJsonAsync(long generationId, long customerId, CancellationToken cancellationToken);

    /// <summary>Оценки всех клиентов поколения</summary>
    Task<IReadOnlyDictionary<long, CustomerScoreRow>> GetScoresAsync(long generationId, CancellationToken cancellationToken);
}

/// <summary>Наблюдённая история изменений между загрузками тестовых данных</summary>
/// <param name="Snapshots">Загрузок тестовых данных до ревизии включительно</param>
/// <param name="EventsTotal">Событий всего</param>
/// <param name="DeletedByObservedDay">Удаления по дню наблюдения (yyyy-MM-dd)</param>
/// <param name="Recent">Последние события</param>
public sealed record ObservedHistory(int Snapshots, int EventsTotal, IReadOnlyDictionary<string, int> DeletedByObservedDay, IReadOnlyList<ObservedEventDto> Recent);

/// <summary>Счётчики наблюдений для статуса ML</summary>
public sealed record ObservationCounts(int Snapshots, int StatusChanges, int Deletions);

/// <summary>Аналитика наблюдений (история между загрузками тестовых данных)</summary>
public interface IAnalyticsRepository
{
    /// <summary>История до ревизии источника включительно</summary>
    Task<ObservedHistory> GetHistoryAsync(long upToSourceRevision, CancellationToken cancellationToken);

    /// <summary>Счётчики наблюдений</summary>
    Task<ObservationCounts> GetObservationCountsAsync(CancellationToken cancellationToken);
}

/// <summary>Задания пересчёта</summary>
public interface IRecalcJobRepository
{
    /// <summary>Задание по идентификатору</summary>
    Task<RecalcJob?> GetAsync(long id, CancellationToken cancellationToken);
}
