using Aeza.Risk.Core.Security;

using FluentValidation;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Aeza.Risk.Feature;

/// <summary>Фильтр fluent валидатора</summary>
/// <param name="validator">Валидатор модели</param>
/// <typeparam name="T">тип валидируемой модели</typeparam>
public class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var model = ctx.Arguments.OfType<T>().FirstOrDefault();

        if (model is null)
            return TypedResults.BadRequest("Request body is required.");

        var result = await validator.ValidateAsync(model, ctx.HttpContext.RequestAborted);

        return !result.IsValid
            ? TypedResults.ValidationProblem(result.ToDictionary())
            : await next(ctx);
    }
}

/// <summary>Явная проверка antiforgery для JSON minimal API (регистрации сервиса недостаточно)</summary>
public sealed class CsrfFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.Identity is { IsAuthenticated: true, AuthenticationType: AuthSchemes.Machine }) return await next(context);
        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return AppProblems.Create(StatusCodes.Status403Forbidden, "CSRF-токен отсутствует или неверен", "csrf_invalid",
                detail: "Получите токен GET /api/v1/auth/csrf и передайте его в заголовке X-CSRF-TOKEN.");
        }
        return await next(context);
    }
}
