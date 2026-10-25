using System.Globalization;
using System.Text;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Generations;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Feature.Customers.List;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Export.Customers;

/// <summary>Ручная выгрузка кандидатов на возврат: CSV всех строк активного поколения под фильтрами рейтинга</summary>
[UsedImplicitly]
public class ExportCustomers
{
    /// <summary>Строк в одной выгрузке не больше</summary>
    public const int MaxRows = 200_000;

    private static readonly string[] Columns =
    [
        "generation_id", "as_of", "customer_id", "email", "risk_score", "risk_level", "score_state", "primary_reason",
        "at_risk_minor", "planned_minor", "unfunded_minor", "full_churn_score", "full_churn_level", "full_churn_state",
        "payment_trend", "payment_change_pct", "reason_codes",
    ];

    private static async Task<IResult> Handle(
        [AsParameters] CustomerListQuery query, HttpContext http, IGenerationReader generations, ICustomerResultsRepository results,
        IAuditLog audit, CancellationToken cancellationToken)
    {
        var lookup = await generations.ResolveAsync(query.GenerationId, cancellationToken);
        if (lookup.Context is not { } ctx) return AppProblems.Generation(lookup.Error);

        var criteria = query.ToCriteria() with { Page = 1, PageSize = MaxRows };
        var page = await results.ListAsync(ctx.Generation.Id, criteria, cancellationToken);
        var items = page.Rows.Select(r => r.ToListItem()).ToList();
        var actor = http.User.Identity?.Name ?? "unknown";
        await audit.WriteAsync(actor, "customers_exported", new
        {
            rows = items.Count, generationId = ctx.Generation.Id, criteria.SortBy, criteria.Ascending, criteria.RiskLevels,
            criteria.ScoreState, criteria.FullChurnLevels, criteria.FullChurnState, criteria.PaymentTrends, search = criteria.Search is not null,
        }, cancellationToken);
        http.Response.Headers["X-Risk-Generation"] = ctx.Generation.Id.ToString(CultureInfo.InvariantCulture);
        return TypedResults.File(ToCsv(ctx.Info, items), "text/csv; charset=utf-8", $"aeza-customers-g{ctx.Generation.Id}.csv");
    }

    /// <summary>CSV (RFC 4180, UTF-8 с BOM для Excel); деньги — целые еврокопейки; текстовые поля защищены от formula injection</summary>
    internal static byte[] ToCsv(GenerationInfo generation, IReadOnlyList<CustomerListItem> items)
    {
        var sb = new StringBuilder(128 + items.Count * 160);
        sb.AppendJoin(',', Columns).Append("\r\n");
        var gen = generation.Id.ToString(CultureInfo.InvariantCulture);
        var asOf = generation.AsOf.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        foreach (var c in items)
        {
            string[] cells =
            [
                gen, asOf, Num(c.Id), Text(c.Email), Num(c.RiskScore), c.RiskLevel, c.ScoreState, Text(c.PrimaryReason.Text),
                Num(c.Money.AtRiskMinor), Num(c.Money.PlannedRenewalsMinor), Num(c.Money.UnfundedMinor),
                Num(c.Churn.FullChurn.Score), c.Churn.FullChurn.Level, c.Churn.FullChurn.State,
                c.Churn.PaymentTrend.State, Num(c.Churn.PaymentTrend.ChangePct), Text(string.Join(';', c.ReasonCodes)),
            ];
            sb.AppendJoin(',', cells).Append("\r\n");
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Num(decimal? v) => v is { } d ? d.ToString("0.######", CultureInfo.InvariantCulture) : "";

    /// <summary>Кавычки по RFC 4180; значения, начинающиеся с = + - @ (или tab/CR), — с апострофом (OWASP CSV injection)</summary>
    internal static string Text(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 || v[0] == '\'' ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(ExportCustomers);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("export/customers", Handle)
                .RequireAuthorization(Policies.Reader)
                .AddEndpointFilter<ValidationFilter<CustomerListQuery>>()
                .Produces(StatusCodes.Status200OK, contentType: "text/csv")
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
                .WithTags("Export")
                .WithSummary("Ручная выгрузка кандидатов на возврат в CSV (для сервиса возврата пользователей)")
                .WithDescription("Все клиенты активного поколения под фильтрами и сортировкой рейтинга (без пагинации, до 200 000 строк), UTF-8 с BOM." +
                                 "Деньги — целые еврокопейки. Заголовок X-Risk-Generation. Доступ: сессия admin/analyst (кнопка «Экспорт CSV» в панели)." +
                                 "Каждая выгрузка пишется в аудит.");
    }
}
