using System.Text.Json;
using Aeza.Risk.Core;
using Xunit;

namespace Aeza.Risk.UnitTests;

/// <summary>Минимальный загрузчик data.json организаторов для тестов ядра (API использует полноценный валидирующий адаптер)</summary>
internal static class OrganizerData
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    public static SourceData Load(string path)
    {
        var raw = JsonSerializer.Deserialize<RawDto>(File.ReadAllText(path), Options)!;
        return new SourceData(
            raw.accounts.Select(a => new AccountInput(a.id, a.email, a.balance, a.withdrawBalance, a.totalReplenished, a.bonusBalance, a.bonusState)).ToList(),
            raw.services.Select(s => new ServiceInput(s.id, s.ownerId, s.name, s.paymentTerm, s.autoProlong, s.createdAt, s.expiresAt, s.status, s.price)).ToList(),
            raw.transactions.Select(t => new TransactionInput(t.id, t.serviceId, t.amount, t.status, t.performedAt, t.createdAt, t.type)).ToList());
    }

#pragma warning disable IDE1006
    private sealed record RawDto(List<A> accounts, List<S> services, List<T> transactions);
    private sealed record A(long id, string email, long balance, long withdrawBalance, long totalReplenished, long bonusBalance, string bonusState);
    private sealed record S(long id, string name, long ownerId, string paymentTerm, bool autoProlong, DateTimeOffset createdAt, DateTimeOffset expiresAt, string status, long price);
    private sealed record T(long id, long amount, string status, DateTimeOffset? performedAt, DateTimeOffset createdAt, string type, long serviceId);
#pragma warning restore IDE1006
}

public sealed class OrganizerDataTests
{
    private static readonly DateTimeOffset SourceAsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OrganizerSnapshot_EvaluatesEveryCustomer_WithHonestUnknowns()
    {
        var data = OrganizerData.Load(Path.Combine(AppContext.BaseDirectory, "organizer", "data.json"));
        var r = new RiskEngine().Evaluate(data, ModelSettings.Default(FeatureCatalog.All), SourceAsOf);

        Assert.Equal(10, r.Customers.Count);
        // Аудит данных: у аккаунтов 2, 3, 7, 8 только blocked/deleted услуги → нет оценки, а не «низкий риск».
        Assert.Equal(new long[] { 2, 3, 7, 8 }, r.Customers.Where(c => c.State == ScoreState.NoRenewableServices).Select(c => c.Account.Id).ToArray());
        Assert.All(r.Customers.Where(c => c.State == ScoreState.NoRenewableServices), c => Assert.Null(c.Score));
        // Аккаунты 5 и 10 — только prolong_wait: высокий риск, главная причина — ожидание оплаты.
        foreach (var id in new long[] { 5, 10 })
        {
            var c = r.Customers.Single(x => x.Account.Id == id);
            Assert.Equal(RiskLevel.High, c.Level);
            Assert.Equal("waiting_payment", c.PrimaryReason.Code);
            Assert.Contains(QualityFlags.ProlongWaitBeyondGrace, c.Flags);
        }
        Assert.True(r.Summary.AtRiskMinor > 0);
        Assert.True(r.Summary.AtRiskMinor <= r.Summary.PlannedRenewalsMinor);
    }
}
