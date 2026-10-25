namespace Aeza.Risk.Core.Entities;

/// <summary>Принятый неизменяемый снимок источника (provenance + сырой файл)</summary>
public sealed class SourceSnapshotEntity
{
    public long Id { get; set; }
    /// <summary>Серверная монотонная ревизия снимка</summary>
    public long Revision { get; set; }
    public DateTimeOffset SourceAsOf { get; set; }
    public bool SourceAsOfAssumed { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string ContentHash { get; set; } = "";
    public string RawSha256 { get; set; } = "";
    public string Adapter { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string? SourceRevisionLabel { get; set; }
    public bool Synthetic { get; set; }
    public string Provenance { get; set; } = "{}";
    public string Warnings { get; set; } = "{}";
    public int AccountsCount { get; set; }
    public int ServicesCount { get; set; }
    public int TransactionsCount { get; set; }
    public long RawBytes { get; set; }
    public byte[] RawGzip { get; set; } = [];
    public string Actor { get; set; } = "";
}

public sealed class SrcAccount
{
    public long SnapshotId { get; set; }
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public long? Balance { get; set; }
    public long? WithdrawBalance { get; set; }
    public long? TotalReplenished { get; set; }
    public long? BonusBalance { get; set; }
    public string? BonusState { get; set; }
}

public sealed class SrcService
{
    public long SnapshotId { get; set; }
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string? PaymentTerm { get; set; }
    public bool? AutoProlong { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Status { get; set; }
    public long? Price { get; set; }
}

public sealed class SrcTransaction
{
    public long SnapshotId { get; set; }
    public long Id { get; set; }
    public long ServiceId { get; set; }
    public long? Amount { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? PerformedAt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? Type { get; set; }
}

/// <summary>Неизменяемая ревизия настроек модели</summary>
public sealed class ModelRevisionEntity
{
    public long Id { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public int SchemaVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string? Comment { get; set; }
    public long? BasedOn { get; set; }
}

public static class JobStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Superseded = "superseded";
    public const string Coalesced = "coalesced";
}

/// <summary>Durable задание пересчёта</summary>
public sealed class RecalcJob
{
    public long Id { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = JobStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long? SourceSnapshotId { get; set; }
    public long? ModelRevisionId { get; set; }
    public long? GenerationId { get; set; }
    public long? CoalescedInto { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? RequestedBy { get; set; }
}

/// <summary>Неизменяемое поколение результатов для кортежа входов</summary>
public sealed class GenerationEntity
{
    public long Id { get; set; }
    public long SourceSnapshotId { get; set; }
    public long SourceRevision { get; set; }
    public long ModelRevisionId { get; set; }
    public DateTimeOffset AsOf { get; set; }
    public string ClockMode { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public int DurationMs { get; set; }
    public long JobId { get; set; }
    /// <summary>Водяной знак журнала событий источника (последний применённый seq) — часть входов поколения</summary>
    public long SourceSeq { get; set; }
    /// <summary>Время последнего изменения источника (событие или снимок) на момент расчёта</summary>
    public DateTimeOffset? SourceUpdatedAt { get; set; }
    /// <summary>Во входах есть данные симулятора: снимок помечен synthetic или применены события симулятора после базы</summary>
    public bool SourceSynthetic { get; set; }
    /// <summary>Последнее доменное событие, учтённое в активности клиентов (признаки поведения) — часть входов поколения</summary>
    public long ActivitySeq { get; set; }
    /// <summary>Теневая модель поколения и счётчик её включений — часть входов</summary>
    public long? MlModelId { get; set; }
    public long MlRevision { get; set; }
    public string SummaryJson { get; set; } = "{}";
    public string ForecastJson { get; set; } = "[]";
}

/// <summary>Результат клиента в поколении: колонки для сортировки/фильтра + полная деталь (jsonb)</summary>
public sealed class CustomerResultEntity
{
    public long GenerationId { get; set; }
    public long CustomerId { get; set; }
    public string Email { get; set; } = "";
    public string EmailSort { get; set; } = "";
    public long? Balance { get; set; }
    public long? TotalReplenished { get; set; }
    public long? WithdrawBalance { get; set; }
    public long? BonusBalance { get; set; }
    public string? BonusState { get; set; }
    public decimal? Score { get; set; }
    public string Level { get; set; } = "unknown";
    public string ScoreState { get; set; } = "";
    public string PrimaryReasonCode { get; set; } = "";
    public string PrimaryReasonText { get; set; } = "";
    public decimal PrimaryReasonContribution { get; set; }
    public long? PrimaryReasonServiceId { get; set; }
    public string[] ReasonCodes { get; set; } = [];
    public string[] Flags { get; set; } = [];
    public long PlannedMinor { get; set; }
    public long UnfundedMinor { get; set; }
    public long AtRiskMinor { get; set; }
    public int ServicesTotal { get; set; }
    public int ServicesRenewable { get; set; }
    public int ServicesScored { get; set; }
    /// <summary>Индекс «уход клиента целиком» (min score действующих услуг) или null</summary>
    public decimal? FullChurnScore { get; set; }
    public string FullChurnLevel { get; set; } = "unknown";
    public string FullChurnState { get; set; } = "";
    /// <summary>Тренд платежей: decline/stable/growth/no_payments/insufficient_history</summary>
    public string PaymentTrend { get; set; } = "";
    public decimal? PaymentChangePct { get; set; }
    public long PaidCurrentMinor { get; set; }
    public long PaidPreviousMinor { get; set; }
    public string DetailJson { get; set; } = "{}";
}

public sealed class AppStateEntity
{
    public int Id { get; set; } = 1;
    public long? ActiveGenerationId { get; set; }
    public long? LatestSourceSnapshotId { get; set; }
    public long? LatestModelRevisionId { get; set; }
    /// <summary>Последний применённый seq журнала событий</summary>
    public long SourceSeq { get; set; }
    /// <summary>Время последнего изменения источника (событие или снимок)</summary>
    public DateTimeOffset? SourceUpdatedAt { get; set; }
    /// <summary>Теневая ML-модель; null — выключена</summary>
    public long? ActiveMlModelId { get; set; }
    /// <summary>Счётчик включений и выключений теневой модели и смен режима расчёта — часть входов поколения</summary>
    public long MlRevision { get; set; }
    /// <summary>Чем считается основной балл: ml (по умолчанию) | rules</summary>
    public string ScoringPrimary { get; set; } = "ml";
}

/// <summary>Журнал событий источника (только добавление): что пришло, когда и что с ним сделано</summary>
public sealed class SourceEventEntity
{
    /// <summary>Порядок приёма (монотонный)</summary>
    public long Seq { get; set; }
    public string? EventId { get; set; }
    /// <summary>Время получения = время события</summary>
    public DateTimeOffset ReceivedAt { get; set; }
    public string Kind { get; set; } = "";
    public long EntityId { get; set; }
    public long? OwnerId { get; set; }
    public string Outcome { get; set; } = "";
    public string? StaleReason { get; set; }
    public string[] Changes { get; set; } = [];
    /// <summary>Значения «от → до» без PII (jsonb)</summary>
    public string Effect { get; set; } = "{}";
    /// <summary>Полное состояние записи как пришло (jsonb, исходные имена полей)</summary>
    public string Payload { get; set; } = "{}";
    public string Origin { get; set; } = "";
    public string Actor { get; set; } = "";
    public bool Synthetic { get; set; }
    public Guid BatchId { get; set; }
    /// <summary>Время события: v2 — occurredAt источника, v1 — время получения</summary>
    public DateTimeOffset OccurredAt { get; set; }
    /// <summary>Доменный тип события v2, из state которого получено изменение; null — v1</summary>
    public string? EventType { get; set; }
    /// <summary>Доменное событие v2 (source_domain_events.seq); null — v1</summary>
    public long? DomainSeq { get; set; }
}

/// <summary>Журнал доменных событий v2 (только добавление): что произошло в источнике и когда</summary>
public sealed class SourceDomainEventEntity
{
    /// <summary>Порядок приёма</summary>
    public long Seq { get; set; }
    public string EventId { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>Тип есть в каталоге (иначе — качество данных)</summary>
    public bool KnownType { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    /// <summary>null — глобальное событие</summary>
    public long? AccountId { get; set; }
    public long? AccountSeq { get; set; }
    public long? ServiceId { get; set; }
    /// <summary>Входящий коннектор (http-push, имя из Auth:IngestConnectors, panel)</summary>
    public string Connector { get; set; } = "";
    /// <summary>Детали события (jsonb, без PII)</summary>
    public string Data { get; set; } = "{}";
    /// <summary>applied | no_change | stale | recorded</summary>
    public string Outcome { get; set; } = "";
    /// <summary>Сколько записей из state применено (изменили состояние)</summary>
    public int StateApplied { get; set; }
    public bool Synthetic { get; set; }
    public Guid BatchId { get; set; }
}

/// <summary>Последовательность событий аккаунта в источнике (accountSeq): максимум и пропуски</summary>
public sealed class SourceAccountStreamEntity
{
    public long AccountId { get; set; }
    public long MaxSeq { get; set; }
    /// <summary>Пропущенные номера ниже MaxSeq (до 1000)</summary>
    public long[] Missing { get; set; } = [];
    /// <summary>С какого момента есть незакрытый пропуск</summary>
    public DateTimeOffset? GapSince { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Входящий коннектор источника: водяной знак и последнее событие</summary>
public sealed class SourceConnectorEntity
{
    public string Name { get; set; } = "";
    /// <summary>Все события до этого момента отправлены (stream.watermark)</summary>
    public DateTimeOffset? Watermark { get; set; }
    public DateTimeOffset? LastReceivedAt { get; set; }
    public DateTimeOffset? LastOccurredAt { get; set; }
    public long Events { get; set; }
    /// <summary>Отклонено событий (ошибки полей) за всё время</summary>
    public long Rejected { get; set; }
    /// <summary>Повторов eventId за всё время</summary>
    public long Duplicates { get; set; }
    /// <summary>Последняя ошибка приёма: путь поля и код, без значений</summary>
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
}

/// <summary>Дневная активность клиента: проекция журнала доменных событий v2 по времени события</summary>
public sealed class CustomerActivityEntity
{
    public long AccountId { get; set; }
    public DateOnly Day { get; set; }
    public int Logins { get; set; }
    public int LoginsFailed { get; set; }
    public int UsageSamples { get; set; }
    public double CpuSum { get; set; }
    public double TrafficOutGb { get; set; }
    public int IncidentMinutes { get; set; }
    public int TicketsOpened { get; set; }
    public int TicketsUnsolved { get; set; }
    public int? CsatMin { get; set; }
    public int? NpsMin { get; set; }
    public int PaymentsCount { get; set; }
    public long PaymentsMinor { get; set; }
    public int PaymentsFailed { get; set; }
    public int TopUpsCount { get; set; }
    public long TopUpsMinor { get; set; }
    public int Downgrades { get; set; }
    public int Upgrades { get; set; }
    public int AutoProlongOff { get; set; }
    public int Suspends { get; set; }
    public long WithdrawalsMinor { get; set; }
    public int Backups { get; set; }
    public int Renewals { get; set; }
    public int Deletions { get; set; }
    public int BonusExpired { get; set; }
    public int BackupDownloads { get; set; }
    public int DnsExternal { get; set; }
    public int RetentionOffers { get; set; }
    public int RetentionHoldouts { get; set; }
    public int Events { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Пакетный импорт истории событий (коннектор batch-import): durable-задание по кускам</summary>
public sealed class SourceImportEntity
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Actor { get; set; } = "";
    public string Connector { get; set; } = "";
    public bool Synthetic { get; set; }
    /// <summary>pending | running | succeeded | failed</summary>
    public string Status { get; set; } = "pending";
    public long Bytes { get; set; }
    public int ChunksTotal { get; set; }
    public int ChunksDone { get; set; }
    public long EventsTotal { get; set; }
    public long Applied { get; set; }
    public long Recorded { get; set; }
    public long NoChange { get; set; }
    public long Stale { get; set; }
    public long Duplicates { get; set; }
    public long Rejected { get; set; }
    /// <summary>Первые ошибки полей (пути и коды, без значений)</summary>
    public string? Errors { get; set; }
    public string? ErrorCode { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Пересчёт после импорта (один на весь импорт)</summary>
    public long? JobId { get; set; }
}

/// <summary>Кусок импорта: до 5000 строк JSONL, gzip</summary>
public sealed class SourceImportChunkEntity
{
    public long ImportId { get; set; }
    public int No { get; set; }
    public int Lines { get; set; }
    /// <summary>Номер первой строки куска в файле (для путей ошибок)</summary>
    public long FirstLine { get; set; }
    public byte[] Data { get; set; } = [];
    public bool Done { get; set; }
}

/// <summary>Текущее состояние аккаунта (снимок ⊕ события)</summary>
public sealed class CurAccount
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public long? Balance { get; set; }
    public long? WithdrawBalance { get; set; }
    public long? TotalReplenished { get; set; }
    public long? BonusBalance { get; set; }
    public string? BonusState { get; set; }
    public long UpdatedSeq { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Текущее состояние услуги</summary>
public sealed class CurService
{
    public long Id { get; set; }
    public long OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string? PaymentTerm { get; set; }
    public bool? AutoProlong { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Status { get; set; }
    public long? Price { get; set; }
    public long UpdatedSeq { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Текущее состояние транзакции</summary>
public sealed class CurTransaction
{
    public long Id { get; set; }
    public long ServiceId { get; set; }
    public long? Amount { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? PerformedAt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? Type { get; set; }
    public long UpdatedSeq { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Исход срока услуги: продлена ли в течение N дней после expiresAt</summary>
public sealed class ExpiryOutcomeEntity
{
    public long ServiceId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long OwnerId { get; set; }
    public string? PaymentTerm { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public string? Outcome { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public bool Synthetic { get; set; }
}

/// <summary>Наблюдённое изменение услуги между двумя последовательными снимками (интервал, а не точное время)</summary>
public sealed class ObservedServiceEvent
{
    public long Id { get; set; }
    public long SourceSnapshotId { get; set; }
    public long SourceRevision { get; set; }
    public long ServiceId { get; set; }
    public long OwnerId { get; set; }
    public string Kind { get; set; } = "";
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public DateTimeOffset ObservedFrom { get; set; }
    public DateTimeOffset ObservedTo { get; set; }
}

public sealed class UserEntity
{
    public long Id { get; set; }
    public string Login { get; set; } = "";
    public string LoginNormalized { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "";
    public int FailedCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool Disabled { get; set; }
}

/// <summary>Серверная сессия: в cookie — зашифрованный тикет с sid; здесь — SHA-256(sid) для отзыва</summary>
public sealed class SessionEntity
{
    public string IdHash { get; set; } = "";
    public long UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>Неудачные входы по SHA-256(логин, IP): общая для реплик блокировка перебора</summary>
public sealed class LoginThrottleEntity
{
    public string KeyHash { get; set; } = "";
    public int Failures { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Details { get; set; } = "{}";
}

/// <summary>Загруженная ML-модель: ONNX и карточка; включённая — app_state.active_ml_model_id</summary>
public sealed class MlModelEntity
{
    public long Id { get; set; }
    public string Version { get; set; } = "";
    public string FeatureSchema { get; set; } = "";
    /// <summary>logistic | fasttree</summary>
    public string Kind { get; set; } = "";
    public bool Synthetic { get; set; }
    /// <summary>Карточка model.json (метрики, разбиение, важность признаков) как есть</summary>
    public string CardJson { get; set; } = "{}";
    public string? ReportMarkdown { get; set; }
    public byte[] Onnx { get; set; } = [];
    public string OnnxSha256 { get; set; } = "";
    /// <summary>Структура деревьев (trees.json) для точных вкладов; null — пакет без неё</summary>
    public string? TreesJson { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public string UploadedBy { get; set; } = "";
}

/// <summary>Прогноз теневой модели в точке решения E − k (первый расчёт после неё): сверяется с expiry_outcomes — проверка на реальных исходах</summary>
public sealed class MlShadowPredictionEntity
{
    public long ModelId { get; set; }
    public long ServiceId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int OffsetDays { get; set; }
    public long AccountId { get; set; }
    public DateTimeOffset PredictedAt { get; set; }
    public long GenerationId { get; set; }
    public double Probability { get; set; }
    public decimal? RulesScore { get; set; }
}

/// <summary>Событие, собранное в панели: ждёт симулятор, потом попадает в общий поток событий</summary>
public sealed class SimulationCommandEntity
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    /// <summary>Ревизия snapshot системы, к которой относится событие</summary>
    public long WorldRevision { get; set; }
    public string Type { get; set; } = "";
    public long AccountId { get; set; }
    public long? ServiceId { get; set; }
    public string DataJson { get; set; } = "{}";
    /// <summary>pending | applied | rejected</summary>
    public string Status { get; set; } = "pending";
    public string EventIdsJson { get; set; } = "[]";
    public string? Message { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Симуляция: настройки, текущий snapshot системы и последний отчёт симулятора (одна строка)</summary>
public sealed class SimulationStateEntity
{
    public int Id { get; set; } = 1;
    public string SettingsJson { get; set; } = "{}";
    public long Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public long WorldRevision { get; set; }
    public DateTimeOffset WorldStartedAt { get; set; }
    public string WorldSettingsJson { get; set; } = "{}";
    public DateTimeOffset? WorldResetAt { get; set; }
    public string? WorldResetBy { get; set; }
    public long ScenarioRequest { get; set; }
    public string? AgentStatusJson { get; set; }
    public DateTimeOffset? AgentReportedAt { get; set; }
}
