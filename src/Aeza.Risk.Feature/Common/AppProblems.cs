using Aeza.Risk.Core.Generations;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aeza.Risk.Feature;

/// <summary>ProblemDetails ответов API с машинным кодом ошибки (extensions.code)</summary>
internal static class AppProblems
{
    /// <summary>Ошибка со статусом, заголовком и кодом</summary>
    public static ProblemHttpResult Create(int status, string title, string code, IDictionary<string, object?>? extra = null, string? detail = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (extra is not null)
            foreach (var (key, value) in extra) extensions[key] = value;
        return TypedResults.Problem(statusCode: status, title: title, detail: detail, extensions: extensions);
    }

    /// <summary>422 с ошибками по путям полей</summary>
    public static ProblemHttpResult Validation(IReadOnlyDictionary<string, string[]> errors, string? title = null, string? code = null)
    {
        var problem = new HttpValidationProblemDetails(errors.ToDictionary(k => k.Key, k => k.Value))
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = title ?? "One or more validation errors occurred.",
        };
        if (code is not null) problem.Extensions["code"] = code;
        return TypedResults.Problem(problem);
    }

    /// <summary>Поколение не найдено: 503 (ещё не посчитано) или 410 (удалено по retention)</summary>
    public static ProblemHttpResult Generation(GenerationLookupError error) => error switch
    {
        GenerationLookupError.Gone => Create(StatusCodes.Status410Gone, "Поколение удалено по retention — перечитайте активное", "generation_gone"),
        GenerationLookupError.ActiveMissing => Create(StatusCodes.Status503ServiceUnavailable, "Активное поколение недоступно", "no_generation"),
        _ => Create(StatusCodes.Status503ServiceUnavailable, "Расчёт ещё не выполнен: загрузите тестовые данные или дождитесь событий источника и пересчёта", "no_generation"),
    };
}
