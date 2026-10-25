namespace Aeza.Risk.Core.Source;

/// <summary>Правила применения события к текущему состоянию одной записи</summary>
public static class SourceChangeRules
{
    /// <summary>Услуга</summary>
    public static ChangeResult ApplyService(ServiceInput? current, ServiceInput incoming)
    {
        var owner = incoming.OwnerId;
        if (current is null)
        {
            var created = new List<string> { ChangeCodes.ServiceCreated };
            var to = SourceEnums.ParseStatus(incoming.Status);
            if (to == ServiceStatus.Deleted) created.Add(ChangeCodes.Deleted);
            return new ChangeResult(ChangeOutcome.Applied, null, created, new Dictionary<string, object?>
            {
                ["statusTo"] = incoming.Status, ["expiresTo"] = incoming.ExpiresAt, ["priceTo"] = incoming.Price,
                ["autoProlongTo"] = incoming.AutoProlong, ["termTo"] = incoming.PaymentTerm,
            }, owner);
        }

        var from = SourceEnums.ParseStatus(current.Status);
        var toStatus = SourceEnums.ParseStatus(incoming.Status);
        if (from == ServiceStatus.Deleted && toStatus != ServiceStatus.Deleted)
            return ChangeResult.StaleBy(StaleReasons.DeletedIsTerminal, current.OwnerId);
        if (current.ExpiresAt is { } ce && incoming.ExpiresAt is { } ie && ie < ce)
            return ChangeResult.StaleBy(StaleReasons.ExpiresRegressed, current.OwnerId);
        if (current == incoming) return ChangeResult.Same(owner);

        var changes = new List<string>();
        var effect = new Dictionary<string, object?>();
        if (current.Status != incoming.Status)
        {
            changes.Add(ChangeCodes.StatusChanged);
            effect["statusFrom"] = current.Status;
            effect["statusTo"] = incoming.Status;
            if (toStatus == ServiceStatus.ProlongWait) changes.Add(ChangeCodes.ToProlongWait);
            if (toStatus == ServiceStatus.Deleted) changes.Add(ChangeCodes.Deleted);
            if (toStatus == ServiceStatus.Blocked) changes.Add(ChangeCodes.Blocked);
        }
        if (current.ExpiresAt != incoming.ExpiresAt)
        {
            if (current.ExpiresAt is not null && incoming.ExpiresAt > current.ExpiresAt) changes.Add(ChangeCodes.Renewed);
            effect["expiresFrom"] = current.ExpiresAt;
            effect["expiresTo"] = incoming.ExpiresAt;
        }
        if (current.Price != incoming.Price)
        {
            changes.Add(ChangeCodes.PriceChanged);
            effect["priceFrom"] = current.Price;
            effect["priceTo"] = incoming.Price;
        }
        if (current.AutoProlong != incoming.AutoProlong)
        {
            changes.Add(ChangeCodes.AutoProlongChanged);
            effect["autoProlongFrom"] = current.AutoProlong;
            effect["autoProlongTo"] = incoming.AutoProlong;
        }
        if (current.PaymentTerm != incoming.PaymentTerm)
        {
            changes.Add(ChangeCodes.TermChanged);
            effect["termFrom"] = current.PaymentTerm;
            effect["termTo"] = incoming.PaymentTerm;
        }
        if (current.OwnerId != incoming.OwnerId)
        {
            changes.Add(ChangeCodes.OwnerChanged);
            effect["ownerFrom"] = current.OwnerId;
            effect["ownerTo"] = incoming.OwnerId;
        }
        // Имя и дата создания тоже входят в состояние, но в эффект не попадают (имя может быть пользовательским)
        return new ChangeResult(ChangeOutcome.Applied, null, changes, effect, owner);
    }

    /// <summary>Аккаунт</summary>
    public static ChangeResult ApplyAccount(AccountInput? current, AccountInput incoming)
    {
        if (current is null)
        {
            return new ChangeResult(ChangeOutcome.Applied, null, [ChangeCodes.AccountCreated], new Dictionary<string, object?>
            {
                ["balanceTo"] = incoming.Balance, ["totalReplenishedTo"] = incoming.TotalReplenished, ["bonusStateTo"] = incoming.BonusState,
            }, incoming.Id);
        }
        if (current.TotalReplenished is { } ct && incoming.TotalReplenished is { } it && it < ct)
            return ChangeResult.StaleBy(StaleReasons.TotalReplenishedRegressed, current.Id);
        if (current == incoming) return ChangeResult.Same(incoming.Id);

        var changes = new List<string>();
        var effect = new Dictionary<string, object?>();
        if (current.Balance != incoming.Balance)
        {
            changes.Add(ChangeCodes.BalanceChanged);
            effect["balanceFrom"] = current.Balance;
            effect["balanceTo"] = incoming.Balance;
        }
        if (current.TotalReplenished != incoming.TotalReplenished)
        {
            effect["totalReplenishedFrom"] = current.TotalReplenished;
            effect["totalReplenishedTo"] = incoming.TotalReplenished;
            if (current.TotalReplenished is { } a && incoming.TotalReplenished is { } b && b > a)
            {
                changes.Add(ChangeCodes.ToppedUp);
                effect["toppedUpMinor"] = b - a;
            }
        }
        if (current.BonusBalance != incoming.BonusBalance)
        {
            changes.Add(ChangeCodes.BonusChanged);
            effect["bonusFrom"] = current.BonusBalance;
            effect["bonusTo"] = incoming.BonusBalance;
        }
        if (current.BonusState != incoming.BonusState)
        {
            changes.Add(ChangeCodes.BonusStateChanged);
            effect["bonusStateFrom"] = current.BonusState;
            effect["bonusStateTo"] = incoming.BonusState;
        }
        if (current.WithdrawBalance != incoming.WithdrawBalance)
        {
            changes.Add(ChangeCodes.WithdrawChanged);
            effect["withdrawFrom"] = current.WithdrawBalance;
            effect["withdrawTo"] = incoming.WithdrawBalance;
        }
        if (!string.Equals(current.Email, incoming.Email, StringComparison.Ordinal)) changes.Add(ChangeCodes.EmailChanged); // значения не пишем
        return new ChangeResult(ChangeOutcome.Applied, null, changes, effect, incoming.Id);
    }

    /// <summary>Транзакция</summary>
    public static ChangeResult ApplyTransaction(TransactionInput? current, TransactionInput incoming, long? ownerId)
    {
        var incomingStatus = SourceEnums.ParseTransactionStatus(incoming.Status);
        var effect = new Dictionary<string, object?>
        {
            ["serviceId"] = incoming.ServiceId, ["amount"] = incoming.Amount, ["status"] = incoming.Status, ["type"] = incoming.Type,
        };
        if (current is null)
        {
            var created = new List<string> { ChangeCodes.TransactionCreated };
            if (incomingStatus == TransactionStatus.Performed) created.Add(ChangeCodes.PaymentPerformed);
            return new ChangeResult(ChangeOutcome.Applied, null, created, effect, ownerId);
        }
        var currentStatus = SourceEnums.ParseTransactionStatus(current.Status);
        if (currentStatus == TransactionStatus.Performed && incomingStatus == TransactionStatus.NotPerformed)
            return ChangeResult.StaleBy(StaleReasons.PerformedIsTerminal, ownerId);
        if (current == incoming) return ChangeResult.Same(ownerId);

        var changes = new List<string> { ChangeCodes.TransactionUpdated };
        if (currentStatus != TransactionStatus.Performed && incomingStatus == TransactionStatus.Performed) changes.Add(ChangeCodes.PaymentPerformed);
        effect["statusFrom"] = current.Status;
        effect["amountFrom"] = current.Amount;
        return new ChangeResult(ChangeOutcome.Applied, null, changes, effect, ownerId);
    }
}

/// <summary>Текущее состояние источника в памяти: снимок ⊕ события</summary>
public sealed class SourceState
{
    private readonly Dictionary<long, HashSet<long>> _servicesByOwner = new();

    /// <summary>Аккаунты по id</summary>
    public Dictionary<long, AccountInput> Accounts { get; } = new();

    /// <summary>Услуги по id</summary>
    public Dictionary<long, ServiceInput> Services { get; } = new();

    /// <summary>Транзакции по id</summary>
    public Dictionary<long, TransactionInput> Transactions { get; } = new();

    /// <summary>Состояние из снимка (или его части)</summary>
    public static SourceState From(SourceData data)
    {
        var s = new SourceState();
        foreach (var a in data.Accounts) s.Accounts[a.Id] = a;
        foreach (var x in data.Services) s.PutService(x);
        foreach (var t in data.Transactions) s.Transactions[t.Id] = t;
        return s;
    }

    /// <summary>Канонический снимок текущего состояния (по возрастанию id)</summary>
    public SourceData ToData() => new(
        Accounts.Values.OrderBy(a => a.Id).ToList(),
        Services.Values.OrderBy(s => s.Id).ToList(),
        Transactions.Values.OrderBy(t => t.Id).ToList());

    /// <summary>Применить событие; при <see cref="ChangeOutcome.Applied"/> состояние меняется</summary>
    public ChangeResult Apply(SourceChange change)
    {
        switch (change.Kind)
        {
            case SourceRecordKind.Account:
            {
                var a = change.Account!;
                Accounts.TryGetValue(a.Id, out var current);
                var r = SourceChangeRules.ApplyAccount(current, a);
                if (r.Outcome == ChangeOutcome.Applied) Accounts[a.Id] = a;
                return r;
            }
            case SourceRecordKind.Service:
            {
                var s = change.Service!;
                Services.TryGetValue(s.Id, out var current);
                var r = SourceChangeRules.ApplyService(current, s);
                if (r.Outcome != ChangeOutcome.Applied) return r;
                var hadActive = HasNonDeleted(s.OwnerId);
                if (current is not null && current.OwnerId != s.OwnerId) _servicesByOwner[current.OwnerId].Remove(s.Id);
                PutService(s);
                var changes = r.Changes.ToList();
                if (!Accounts.ContainsKey(s.OwnerId)) changes.Add(ChangeCodes.OwnerUnknown);
                if (changes.Contains(ChangeCodes.Deleted) && hadActive && !HasNonDeleted(s.OwnerId)) changes.Add(ChangeCodes.CustomerLostAllServices);
                return r with { Changes = changes };
            }
            default:
            {
                var t = change.Transaction!;
                Transactions.TryGetValue(t.Id, out var current);
                long? owner = Services.TryGetValue(t.ServiceId, out var svc) ? svc.OwnerId : null;
                var r = SourceChangeRules.ApplyTransaction(current, t, owner);
                if (r.Outcome != ChangeOutcome.Applied) return r;
                Transactions[t.Id] = t;
                return owner is null ? r with { Changes = [.. r.Changes, ChangeCodes.ServiceUnknown] } : r;
            }
        }
    }

    /// <summary>Услуги клиента</summary>
    public IEnumerable<ServiceInput> ServicesOf(long ownerId) =>
        _servicesByOwner.TryGetValue(ownerId, out var ids) ? ids.Select(id => Services[id]) : [];

    private bool HasNonDeleted(long ownerId) =>
        ServicesOf(ownerId).Any(x => SourceEnums.ParseStatus(x.Status) != ServiceStatus.Deleted);

    private void PutService(ServiceInput s)
    {
        Services[s.Id] = s;
        if (!_servicesByOwner.TryGetValue(s.OwnerId, out var set)) _servicesByOwner[s.OwnerId] = set = new HashSet<long>();
        set.Add(s.Id);
    }
}
