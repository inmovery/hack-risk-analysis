using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Customers.Activity;

/// <summary>Активность клиента за день (проекция событий v2 по времени события)</summary>
/// <param name="Date">День</param>
/// <param name="Logins">Успешных входов</param>
/// <param name="CpuAvgPct">Средняя загрузка CPU по услугам; null — телеметрии за день нет</param>
/// <param name="UsageSamples">Услуго-дней телеметрии</param>
/// <param name="TrafficOutGb">Исходящий трафик</param>
/// <param name="IncidentMinutes">Минуты аварий</param>
/// <param name="TicketsOpened">Обращений открыто</param>
/// <param name="TicketsUnsolved">Обращений закрыто без решения</param>
/// <param name="PaymentsCount">Списаний</param>
/// <param name="PaymentsMinor">Сумма списаний, еврокопейки</param>
/// <param name="PaymentsFailed">Неудачных списаний</param>
/// <param name="TopUpsMinor">Пополнения, еврокопейки</param>
/// <param name="Events">Доменных событий за день</param>
[PublicAPI]
public sealed record ActivityDayDto(
    DateOnly Date, int Logins, double? CpuAvgPct, int UsageSamples, double TrafficOutGb, int IncidentMinutes, int TicketsOpened, int TicketsUnsolved,
    int PaymentsCount, long PaymentsMinor, int PaymentsFailed, long TopUpsMinor, int Events);

/// <summary>Поведение клиента для карточки</summary>
/// <param name="KnownSince">Первый день поведенческих данных; null — источник присылает только состояние записей (v1)</param>
/// <param name="From">Начало периода</param>
/// <param name="To">Конец периода</param>
/// <param name="Days">Дни с событиями (без пустых дней)</param>
/// <param name="Events">Лента доменных событий, новые сверху</param>
/// <param name="NextBeforeSeq">beforeSeq следующей страницы ленты</param>
[PublicAPI]
public sealed record CustomerActivityResponse(
    DateOnly? KnownSince, DateOnly From, DateOnly To, IReadOnlyList<ActivityDayDto> Days, IReadOnlyList<DomainEventItem> Events, long? NextBeforeSeq);

/// <summary>Поведение клиента: дневная активность (входы, нагрузка, обращения, платежи) и лента доменных событий с фильтром по доменам</summary>
[UsedImplicitly]
public class GetCustomerActivity
{
    /// <param name="id">Клиент</param>
    /// <param name="days">Период, дней (7–180, по умолчанию 90)</param>
    /// <param name="domain">Фильтр ленты: account | money | service | usage | support | retention</param>
    /// <param name="beforeSeq">Страница ленты старше этого номера</param>
    /// <param name="limit">Событий на странице (1–200, по умолчанию 50)</param>
    /// <param name="behavior">Проекция активности</param>
    /// <param name="time">Часы</param>
    /// <param name="cancellationToken">Отмена</param>
    private static async Task<Results<Ok<CustomerActivityResponse>, ProblemHttpResult>> Handle(
        long id, int? days, string? domain, long? beforeSeq, int? limit, IBehaviorRepository behavior, TimeProvider time, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (days is < 7 or > 180) errors["days"] = ["days от 7 до 180."];
        if (limit is < 1 or > 200) errors["limit"] = ["limit от 1 до 200."];
        if (domain is not null && !DomainEventText.Domains.Contains(domain)) errors["domain"] = [$"Допустимо: {string.Join(", ", DomainEventText.Domains)}."];
        if (errors.Count > 0) return AppProblems.Validation(errors);
        var to = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var from = to.AddDays(-((days ?? 90) - 1));
        var view = await behavior.GetCustomerAsync(id, from, to, domain, beforeSeq, limit ?? 50, cancellationToken);
        var items = view.Days.Select(d => new ActivityDayDto(d.Day, d.Totals.Logins,
            d.Totals.UsageSamples > 0 ? Math.Round(d.Totals.CpuSum / d.Totals.UsageSamples, 1) : null, d.Totals.UsageSamples, Math.Round(d.Totals.TrafficOutGb, 2),
            d.Totals.IncidentMinutes, d.Totals.TicketsOpened, d.Totals.TicketsUnsolved, d.Totals.PaymentsCount, d.Totals.PaymentsMinor, d.Totals.PaymentsFailed,
            d.Totals.TopUpsMinor, d.Totals.Events)).ToList();
        return TypedResults.Ok(new CustomerActivityResponse(view.KnownSince, from, to, items, view.Events, view.NextBeforeSeq));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetCustomerActivity);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("customers/{id:long}/activity", Handle)
                .RequireAuthorization(Policies.Reader)
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Customers")
                .WithSummary("Поведение клиента по событиям v2: дневная активность и лента доменных событий (подписи без PII)");
    }
}
