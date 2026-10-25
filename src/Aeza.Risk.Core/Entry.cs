using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Source;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aeza.Risk.Core;

/// <summary>Регистрация зависимостей уровня бизнес-логики</summary>
public static class Entry
{
    /// <summary>Часы, движок риска и опции с проверкой при старте</summary>
    public static IServiceCollection AddDomain(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RiskEngine>();
        services.AddOptionsWithValidateOnStart<AppOptions>()
            .BindConfiguration(AppOptions.Section)
            .ValidateDataAnnotations();
        services.AddOptionsWithValidateOnStart<AuthOptions>()
            .BindConfiguration(AuthOptions.Section)
            .ValidateDataAnnotations()
            .Validate(o => o.IngestConnectors.All(c => ConnectorNames.IsValidCustom(c.Key) && c.Value.Length >= 16),
                "Auth:IngestConnectors — имя из строчных латинских букв, цифр и дефиса (кроме http-push и panel), токен не короче 16 символов");
        services.AddOptionsWithValidateOnStart<SeedOptions>()
            .BindConfiguration(SeedOptions.Section)
            .ValidateDataAnnotations();
        services.AddOptionsWithValidateOnStart<RecalcOptions>()
            .BindConfiguration(RecalcOptions.Section)
            .ValidateDataAnnotations();
        services.AddOptionsWithValidateOnStart<StreamOptions>()
            .BindConfiguration(StreamOptions.Section)
            .ValidateDataAnnotations();
        services.AddOptionsWithValidateOnStart<MetricsOptions>()
            .BindConfiguration(MetricsOptions.Section)
            .ValidateDataAnnotations();
        return services;
    }
}
