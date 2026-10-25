using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aeza.Risk.Core.Source;

public sealed record ParsedSnapshot(
    SourceData Data,
    DateTimeOffset? SourceAsOf,
    string? SourceRevisionLabel,
    bool Synthetic,
    int SchemaVersion,
    bool Envelope,
    IReadOnlyDictionary<string, int> Warnings,
    IReadOnlyDictionary<string, string[]> Errors,
    IReadOnlyDictionary<string, int> ErrorCounts)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Адаптер <c>aeza-json-v1</c>: принимает envelope { schemaVersion, sourceAsOf, accounts, services, transactions } или «голый» data.json организаторов</summary>
public static class SnapshotParser
{
    public const string AdapterName = "aeza-json-v1";
    public const int MaxRecordsPerCollection = 1_000_000;
    public const long MaxMoneyAbs = 100_000_000_000; // €1 млрд в еврокопейках: исключает переполнение агрегатов
    private const int MaxErrorsListed = 200;

    public static ParsedSnapshot Parse(ReadOnlySpan<byte> utf8)
    {
        var errors = new Dictionary<string, List<string>>();
        var errorCounts = new Dictionary<string, int>();
        var warnings = new Dictionary<string, int>();
        void Err(string path, string code, string message)
        {
            errorCounts[code] = errorCounts.GetValueOrDefault(code) + 1;
            if (errors.Count >= MaxErrorsListed && !errors.ContainsKey(path)) return;
            if (!errors.TryGetValue(path, out var l)) errors[path] = l = new List<string>();
            l.Add(message);
        }
        void Warn(string code) => warnings[code] = warnings.GetValueOrDefault(code) + 1;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            Err("$", "invalid_json", "Тело не является корректным JSON.");
            return Empty(errors, errorCounts, warnings);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Err("$", "invalid_root", "Корень должен быть объектом с массивами accounts, services, transactions.");
                return Empty(errors, errorCounts, warnings);
            }

            var envelope = root.TryGetProperty("schemaVersion", out _) || root.TryGetProperty("sourceAsOf", out _);
            var schemaVersion = 1;
            if (root.TryGetProperty("schemaVersion", out var sv))
            {
                if (sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out schemaVersion) || schemaVersion != 1)
                    Err("schemaVersion", "unsupported_schema", "Поддерживается только schemaVersion = 1.");
            }
            DateTimeOffset? asOf = null;
            if (root.TryGetProperty("sourceAsOf", out var sa) && sa.ValueKind != JsonValueKind.Null)
                asOf = ReadDate(sa, "sourceAsOf", Err);
            string? revisionLabel = null;
            if (root.TryGetProperty("sourceRevision", out var sr) && sr.ValueKind != JsonValueKind.Null)
            {
                revisionLabel = sr.ValueKind switch
                {
                    JsonValueKind.String => sr.GetString(),
                    JsonValueKind.Number => sr.GetRawText(),
                    _ => null,
                };
                if (revisionLabel is null || revisionLabel.Length > 128) Err("sourceRevision", "invalid_type", "sourceRevision — строка или число до 128 символов.");
            }
            var synthetic = root.TryGetProperty("synthetic", out var syn) && syn.ValueKind == JsonValueKind.True;
            foreach (var p in root.EnumerateObject())
                if (p.Name is not ("schemaVersion" or "sourceAsOf" or "sourceRevision" or "synthetic" or "accounts" or "services" or "transactions" or "provenance"))
                    Warn("unknown_root_field");

            var accounts = ReadArray(root, "accounts", Err);
            var services = ReadArray(root, "services", Err);
            var transactions = ReadArray(root, "transactions", Err);

            var accs = new List<AccountInput>(accounts.Count);
            var accIds = new HashSet<long>();
            var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < accounts.Count; i++)
            {
                var p = $"accounts[{i}]";
                if (ReadAccount(accounts[i], p, Err, Warn) is not { } acc) continue;
                if (acc.Id > 0 && !accIds.Add(acc.Id)) Err($"{p}.id", "duplicate_id", "Повторяющийся id аккаунта.");
                if (acc.Email.Length > 0 && !emails.Add(acc.Email)) Warn("duplicate_email_case_insensitive");
                accs.Add(acc);
            }

            var svcs = new List<ServiceInput>(services.Count);
            var svcIds = new HashSet<long>();
            for (var i = 0; i < services.Count; i++)
            {
                var p = $"services[{i}]";
                if (ReadService(services[i], p, Err, Warn) is not { } svc) continue;
                if (svc.Id > 0 && !svcIds.Add(svc.Id)) Err($"{p}.id", "duplicate_id", "Повторяющийся id услуги.");
                if (svc.OwnerId > 0 && !accIds.Contains(svc.OwnerId)) Err($"{p}.ownerId", "unknown_reference", "ownerId ссылается на отсутствующий аккаунт.");
                svcs.Add(svc);
            }

            var txs = new List<TransactionInput>(transactions.Count);
            var txIds = new HashSet<long>();
            for (var i = 0; i < transactions.Count; i++)
            {
                var p = $"transactions[{i}]";
                if (ReadTransaction(transactions[i], p, Err, Warn) is not { } tx) continue;
                if (tx.Id > 0 && !txIds.Add(tx.Id)) Err($"{p}.id", "duplicate_id", "Повторяющийся id транзакции.");
                if (tx.ServiceId > 0 && !svcIds.Contains(tx.ServiceId)) Err($"{p}.serviceId", "unknown_reference", "serviceId ссылается на отсутствующую услугу.");
                txs.Add(tx);
            }

            if (accs.Count == 0) Warn("empty_accounts");
            return new ParsedSnapshot(new SourceData(accs, svcs, txs), asOf, revisionLabel, synthetic, schemaVersion, envelope,
                warnings, errors.ToDictionary(k => k.Key, k => k.Value.ToArray()), errorCounts);
        }
    }

    /// <summary>Аккаунт из JSON-объекта (ошибки — по путям, без значений). null — запись не объект</summary>
    public static AccountInput? ReadAccount(JsonElement e, string p, Action<string, string, string> err, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(err);
        ArgumentNullException.ThrowIfNull(warn);
        if (e.ValueKind != JsonValueKind.Object) { err(p, "invalid_type", "Запись должна быть объектом."); return null; }
        var id = ReadId(e, "id", p, err);
        var email = ReadString(e, "email", p, err, required: true, maxLength: 320);
        var balance = ReadMoney(e, "balance", p, err, warn, allowNegative: true);
        if (balance < 0) warn("negative_balance");
        var acc = new AccountInput(id ?? 0, email ?? "",
            balance,
            ReadMoney(e, "withdrawBalance", p, err, warn, allowNegative: false),
            ReadMoney(e, "totalReplenished", p, err, warn, allowNegative: false),
            ReadMoney(e, "bonusBalance", p, err, warn, allowNegative: false),
            ReadString(e, "bonusState", p, err, required: false, maxLength: 64));
        if (acc.BonusState is not null && SourceEnums.ParseBonusState(acc.BonusState) == BonusState.Unknown) warn("unknown_bonus_state");
        if (acc.BonusState is null) warn("missing_bonus_state");
        WarnUnknownFields(e, warn, "id", "email", "balance", "withdrawBalance", "totalReplenished", "bonusBalance", "bonusState");
        return acc;
    }

    /// <summary>Услуга из JSON-объекта (ссылочная целостность проверяется вызывающим кодом). null — запись не объект</summary>
    public static ServiceInput? ReadService(JsonElement e, string p, Action<string, string, string> err, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(err);
        ArgumentNullException.ThrowIfNull(warn);
        if (e.ValueKind != JsonValueKind.Object) { err(p, "invalid_type", "Запись должна быть объектом."); return null; }
        var id = ReadId(e, "id", p, err);
        var owner = ReadId(e, "ownerId", p, err);
        var created = ReadOptionalDate(e, "createdAt", p, err);
        var expires = ReadOptionalDate(e, "expiresAt", p, err);
        if (created is { } c && expires is { } x && x < c) warn("expires_before_created");
        var status = ReadString(e, "status", p, err, required: false, maxLength: 64);
        var term = ReadString(e, "paymentTerm", p, err, required: false, maxLength: 64);
        if (status is null) warn("missing_status"); else if (SourceEnums.ParseStatus(status) == ServiceStatus.Unknown) warn("unknown_status");
        if (term is null) warn("missing_payment_term"); else if (SourceEnums.ParseTerm(term) == PaymentTerm.Unknown) warn("unknown_payment_term");
        var price = ReadMoney(e, "price", p, err, warn, allowNegative: false);
        var auto = ReadOptionalBool(e, "autoProlong", p, err);
        if (auto is null) warn("missing_autoprolong");
        WarnUnknownFields(e, warn, "id", "name", "ownerId", "paymentTerm", "autoProlong", "createdAt", "expiresAt", "status", "price");
        return new ServiceInput(id ?? 0, owner ?? 0, ReadString(e, "name", p, err, required: false, maxLength: 256) ?? "", term, auto, created, expires, status, price);
    }

    /// <summary>Транзакция из JSON-объекта. null — запись не объект</summary>
    public static TransactionInput? ReadTransaction(JsonElement e, string p, Action<string, string, string> err, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(err);
        ArgumentNullException.ThrowIfNull(warn);
        if (e.ValueKind != JsonValueKind.Object) { err(p, "invalid_type", "Запись должна быть объектом."); return null; }
        var id = ReadId(e, "id", p, err);
        var serviceId = ReadId(e, "serviceId", p, err);
        var status = ReadString(e, "status", p, err, required: false, maxLength: 64);
        var performedAt = ReadOptionalDate(e, "performedAt", p, err);
        if (SourceEnums.ParseTransactionStatus(status) == TransactionStatus.NotPerformed && performedAt is not null) warn("not_performed_with_performed_at");
        WarnUnknownFields(e, warn, "id", "amount", "status", "performedAt", "createdAt", "type", "serviceId");
        return new TransactionInput(id ?? 0, serviceId ?? 0, ReadMoney(e, "amount", p, err, warn, allowNegative: true), status, performedAt,
            ReadOptionalDate(e, "createdAt", p, err), ReadString(e, "type", p, err, required: false, maxLength: 64));
    }

    /// <summary>SHA-256 нормализованного содержимого (порядок записей и форматирование не влияют) + sourceAsOf</summary>
    public static string ContentHash(SourceData data, DateTimeOffset sourceAsOf)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("sourceAsOf", sourceAsOf.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            w.WriteStartArray("accounts");
            foreach (var a in data.Accounts.OrderBy(a => a.Id)) JsonSerializer.Serialize(w, a);
            w.WriteEndArray();
            w.WriteStartArray("services");
            foreach (var s in data.Services.OrderBy(s => s.Id)) JsonSerializer.Serialize(w, s with { CreatedAt = s.CreatedAt?.ToUniversalTime(), ExpiresAt = s.ExpiresAt?.ToUniversalTime() });
            w.WriteEndArray();
            w.WriteStartArray("transactions");
            foreach (var t in data.Transactions.OrderBy(t => t.Id)) JsonSerializer.Serialize(w, t with { CreatedAt = t.CreatedAt?.ToUniversalTime(), PerformedAt = t.PerformedAt?.ToUniversalTime() });
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
    }

    private static ParsedSnapshot Empty(Dictionary<string, List<string>> errors, Dictionary<string, int> counts, Dictionary<string, int> warnings) =>
        new(new SourceData([], [], []), null, null, false, 1, false, warnings, errors.ToDictionary(k => k.Key, k => k.Value.ToArray()), counts);

    private static List<JsonElement> ReadArray(JsonElement root, string name, Action<string, string, string> err)
    {
        if (!root.TryGetProperty(name, out var arr)) { err(name, "missing_field", $"Отсутствует массив {name}."); return []; }
        if (arr.ValueKind != JsonValueKind.Array) { err(name, "invalid_type", $"{name} должен быть массивом."); return []; }
        var len = arr.GetArrayLength();
        if (len > MaxRecordsPerCollection) { err(name, "too_many_records", $"Не более {MaxRecordsPerCollection} записей в {name}."); return []; }
        return arr.EnumerateArray().ToList();
    }

    private static long? ReadId(JsonElement e, string field, string path, Action<string, string, string> err)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) { err($"{path}.{field}", "missing_field", $"Обязательное поле {field}."); return null; }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var id) || id <= 0) { err($"{path}.{field}", "invalid_id", $"{field} — положительное целое число."); return null; }
        return id;
    }

    private static string? ReadString(JsonElement e, string field, string path, Action<string, string, string> err, bool required, int maxLength)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            if (required) err($"{path}.{field}", "missing_field", $"Обязательное поле {field}.");
            return null;
        }
        if (v.ValueKind != JsonValueKind.String) { err($"{path}.{field}", "invalid_type", $"{field} — строка."); return null; }
        var s = v.GetString()!;
        if (s.Length > maxLength) { err($"{path}.{field}", "too_long", $"{field} длиннее {maxLength} символов."); return null; }
        if (required && s.Length == 0) { err($"{path}.{field}", "missing_field", $"{field} не может быть пустым."); return null; }
        return s;
    }

    private static long? ReadMoney(JsonElement e, string field, string path, Action<string, string, string> err, Action<string> warn, bool allowNegative)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) { warn($"missing_{ToSnake(field)}"); return null; }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var m))
        {
            err($"{path}.{field}", "invalid_money", $"{field} — целое число еврокопеек (100 = €1), без дробной части.");
            return null;
        }
        if (Math.Abs(m) > MaxMoneyAbs) { err($"{path}.{field}", "money_out_of_range", $"{field} вне допустимого диапазона."); return null; }
        if (!allowNegative && m < 0) { err($"{path}.{field}", "negative_money", $"{field} не может быть отрицательным."); return null; }
        return m;
    }

    private static bool? ReadOptionalBool(JsonElement e, string field, string path, Action<string, string, string> err)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        err($"{path}.{field}", "invalid_type", $"{field} — true/false.");
        return null;
    }

    private static DateTimeOffset? ReadOptionalDate(JsonElement e, string field, string path, Action<string, string, string> err)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return ReadDate(v, $"{path}.{field}", err);
    }

    /// <summary>Дата ISO 8601 с явным часовым поясом → UTC (общая для тестовых данных и событий)</summary>
    internal static DateTimeOffset? ReadDate(JsonElement v, string path, Action<string, string, string> err)
    {
        if (v.ValueKind != JsonValueKind.String) { err(path, "invalid_date", "Дата — строка ISO 8601 с часовым поясом."); return null; }
        var s = v.GetString()!;
        var hasZone = s.EndsWith('Z') || s.EndsWith('z') || System.Text.RegularExpressions.Regex.IsMatch(s, @"[+-]\d{2}:?\d{2}$");
        if (!hasZone || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            err(path, "invalid_date", "Дата — ISO 8601 с явным часовым поясом (например 2026-10-19T15:00:00Z).");
            return null;
        }
        return d.ToUniversalTime();
    }

    private static void WarnUnknownFields(JsonElement e, Action<string> warn, params string[] known)
    {
        foreach (var p in e.EnumerateObject())
            if (Array.IndexOf(known, p.Name) < 0) warn("unknown_field");
    }

    private static string ToSnake(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) { if (char.IsUpper(c)) sb.Append('_'); sb.Append(char.ToLowerInvariant(c)); }
        return sb.ToString();
    }
}
