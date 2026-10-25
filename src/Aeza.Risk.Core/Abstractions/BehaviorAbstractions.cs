namespace Aeza.Risk.Core.Abstractions;

/// <summary>Поведение клиентов на момент оценки и номер последнего учтённого доменного события (вход поколения)</summary>
/// <param name="Accounts">Клиенты с поведенческими данными; остальных нет — «нет данных»</param>
/// <param name="ActivitySeq">Последнее доменное событие, вошедшее в активность</param>
public sealed record BehaviorInput(IReadOnlyDictionary<long, AccountBehavior> Accounts, long ActivitySeq);

/// <summary>Доменное событие в ленте клиента</summary>
/// <param name="Seq">Номер в журнале доменных событий</param>
/// <param name="OccurredAt">Время события в источнике</param>
/// <param name="ReceivedAt">Время получения</param>
/// <param name="Type">Тип домен.действие</param>
/// <param name="Domain">account | money | service | usage | support | retention | other</param>
/// <param name="ServiceId">Услуга</param>
/// <param name="Text">Подпись из известных полей (без PII)</param>
/// <param name="KnownType">Тип из каталога</param>
/// <param name="Connector">Входящий коннектор</param>
/// <param name="Synthetic">События симулятора</param>
public sealed record DomainEventItem(
    long Seq, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, string Type, string Domain, long? ServiceId, string Text, bool KnownType, string Connector, bool Synthetic);

/// <summary>Активность клиента для карточки</summary>
/// <param name="KnownSince">Первый день поведенческих данных; null — данных нет (только v1)</param>
/// <param name="Days">Дни активности за период</param>
/// <param name="Events">Последние доменные события (новые сверху)</param>
/// <param name="NextBeforeSeq">Для следующей страницы ленты</param>
public sealed record CustomerActivityView(DateOnly? KnownSince, IReadOnlyList<ActivityDay> Days, IReadOnlyList<DomainEventItem> Events, long? NextBeforeSeq);

/// <summary>Проекция дневной активности и журнал доменных событий</summary>
public interface IBehaviorRepository
{
    /// <summary>Поведение всех клиентов на момент <paramref name="asOf"/> (дни не позже даты оценки)</summary>
    Task<BehaviorInput> LoadAsync(DateTimeOffset asOf, CancellationToken cancellationToken);

    /// <summary>Активность клиента за [from, to] и страница ленты (domain — фильтр, beforeSeq — старше)</summary>
    Task<CustomerActivityView> GetCustomerAsync(long accountId, DateOnly from, DateOnly to, string? domain, long? beforeSeq, int limit, CancellationToken cancellationToken);
}
