namespace Aeza.Risk.Core.Source;

/// <summary>Исходы сроков в памяти — те же правила, что у таблицы expiry_outcomes (ExpiryStore)</summary>
public sealed class ExpiryLedger
{
    private readonly Dictionary<(long ServiceId, DateTimeOffset ExpiresAt), ExpiryTerm> _terms = new();
    private readonly Dictionary<long, List<ExpiryTerm>> _open = new();

    public IReadOnlyCollection<ExpiryTerm> Terms => _terms.Values;

    /// <summary>Применить действия правил; возвращает впервые зарегистрированные сроки</summary>
    public IReadOnlyList<ExpiryTerm> Apply(IEnumerable<ExpiryAction> actions, int graceDays)
    {
        var added = new List<ExpiryTerm>();
        var grace = TimeSpan.FromDays(graceDays);
        foreach (var action in actions)
        {
            switch (action)
            {
                case RegisterExpiry r:
                    if (_terms.ContainsKey((r.ServiceId, r.ExpiresAt))) break;
                    var term = new ExpiryTerm(r.ServiceId, r.OwnerId, r.ExpiresAt, r.ObservedAt);
                    _terms[(r.ServiceId, r.ExpiresAt)] = term;
                    if (!_open.TryGetValue(r.ServiceId, out var list)) _open[r.ServiceId] = list = [];
                    list.Add(term);
                    added.Add(term);
                    break;
                case ResolveExpiry x:
                    if (!_open.TryGetValue(x.ServiceId, out var open)) break;
                    foreach (var t in open.Where(t => x.ExpiresBefore is not { } before || t.ExpiresAt < before).ToList())
                    {
                        var late = x.LateOutcome is not null && x.At > t.ExpiresAt + grace;
                        t.Resolve(late ? x.LateOutcome! : x.Outcome, late ? ExpiryOutcomes.ResolutionRenewedLate : x.Resolution, x.At);
                        open.Remove(t);
                    }
                    break;
            }
        }
        return added;
    }

    /// <summary>Созревшие сроки при полном источнике (до <paramref name="completeUpTo"/>): продлено или не продлено</summary>
    public int ResolveDue(DateTimeOffset now, DateTimeOffset? completeUpTo, int graceDays, Func<long, ServiceInput?> current)
    {
        var resolved = 0;
        foreach (var (serviceId, open) in _open)
        {
            foreach (var t in open.ToList())
            {
                var outcome = ExpiryOutcomeRules.ResolveDue(t.ExpiresAt, current(serviceId), completeUpTo, now, graceDays);
                if (outcome is null) continue;
                t.Resolve(outcome, outcome == ExpiryOutcomes.Renewed ? ExpiryOutcomes.ResolutionExtended : ExpiryOutcomes.ResolutionExpired, now);
                open.Remove(t);
                resolved++;
            }
        }
        return resolved;
    }

    public ExpiryTerm? Find(long serviceId, DateTimeOffset expiresAt) => _terms.GetValueOrDefault((serviceId, expiresAt));
}

/// <summary>Срок услуги и его исход (null — ещё не известен)</summary>
public sealed class ExpiryTerm(long serviceId, long ownerId, DateTimeOffset expiresAt, DateTimeOffset registeredAt)
{
    public long ServiceId { get; } = serviceId;
    public long OwnerId { get; } = ownerId;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public DateTimeOffset RegisteredAt { get; } = registeredAt;
    public string? Outcome { get; private set; }
    public string? Resolution { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    internal void Resolve(string outcome, string resolution, DateTimeOffset at)
    {
        Outcome = outcome;
        Resolution = resolution;
        ResolvedAt = at;
    }
}
