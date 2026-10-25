using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Aeza.Risk.Ml;

/// <summary>Отчёт «модель против правил» по карточке model.json — для людей, без идентификаторов клиентов</summary>
public static class Report
{
    public static string Markdown(JsonObject card)
    {
        var sb = new StringBuilder();
        var split = card["split"]!.AsObject();
        var metrics = card["metrics"]!.AsObject();
        sb.AppendLine($"# Модель {card["version"]}: сравнение с правилами");
        sb.AppendLine();
        sb.AppendLine(card["synthetic"]?.GetValue<bool>() == true
            ? "> Набор построен по журналу симулятора."
            : $"> {card["note"]}").AppendLine();
        sb.AppendLine($"- Цель: {card["target"]}");
        sb.AppendLine($"- Решения: {card["decisions"]}");
        sb.AppendLine($"- Набор: {card["dataset"]!["rows"]} строк, «не продлено» {card["dataset"]!["positives"]}; период {card["dataset"]!["dataStart"]} … {card["dataset"]!["dataEnd"]}; источник {card["dataset"]!["source"]}");
        sb.AppendLine($"- Проверка по времени: решения с {split["testFrom"]} ({split["testRows"]} строк, {split["testPositives"]} «не продлено»); обучение — сроки, чей исход созрел до начала проверки ({split["trainRows"]} строк, последнее решение {split["lastTrainDecision"]}): у каждой строки обучения решение раньше проверки не меньше чем на k + N дней");
        sb.AppendLine($"- Ёмкость команды возврата: top {F(split["capacityShare"]!.GetValue<double>() * 100, "0")}% решений (K = {metrics["rules"]!["k"]})");
        sb.AppendLine($"- Признаков {card["features"]!.AsArray().Count} (схема {card["featureSchema"]}), seed {card["seed"]}, выбрана: **{card["model"]}**");
        sb.AppendLine();
        sb.AppendLine("## Отложенный период");
        sb.AppendLine();
        sb.AppendLine("| | PR-AUC | 95% ДИ | ROC-AUC | P@K | R@K | Brier |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var (key, title) in new[] { ("rules", "Правила (по умолчанию, окно срочности 7 дн.)"), ("rulesWide", "Правила (окно срочности 30 дн.)") })
            if (metrics[key] is JsonObject rules)
                sb.AppendLine($"| {title} | {rules["prAuc"]} | | {rules["rocAuc"]} | {rules["precisionAtK"]} | {rules["recallAtK"]} | — |");
        foreach (var name in new[] { "logistic", "fasttree" })
        {
            var m = metrics[name]!.AsObject();
            var t = m["time"]!.AsObject();
            sb.AppendLine($"| {Title(name)} | {t["prAuc"]} | {Ci(m["prAucCi95"])} | {t["rocAuc"]} | {t["precisionAtK"]} | {t["recallAtK"]} | {t["brier"]} |");
        }
        sb.AppendLine();
        sb.AppendLine($"Доля «не продлено» в проверке (PR-AUC случайного ранжира): {card["leakage"]!["baseRate"]}.");
        sb.AppendLine();
        sb.AppendLine($"Разница с лучшими правилами — {(card["baseline"]?.GetValue<string>() == "rulesWide" ? "окно срочности 30 дн." : "по умолчанию")} (bootstrap по клиентам, 200 повторов, 95% интервал):");
        sb.AppendLine();
        foreach (var name in new[] { "logistic", "fasttree" })
        {
            var m = metrics[name]!.AsObject();
            sb.AppendLine($"- {Title(name)}: PR-AUC {Ci(m["prAucMinusRulesCi95"])}, P@K {Ci(m["precisionAtKMinusRulesCi95"])}{Verdict(m["prAucMinusRulesCi95"])}");
        }
        sb.AppendLine();
        sb.AppendLine("## Клиенты вне обучения");
        sb.AppendLine();
        sb.AppendLine("20% клиентов (по хешу id) исключены из обучения, модель проверена только на них в том же отложенном периоде.");
        sb.AppendLine();
        foreach (var name in new[] { "logistic", "fasttree" })
            if (metrics[name]!["customersOutsideTraining"] is JsonObject g)
                sb.AppendLine($"- {Title(name)}: PR-AUC {g["model"]!["prAuc"]} против правил {g["rules"]!["prAuc"]} ({g["model"]!["rows"]} строк, {g["model"]!["positives"]} «не продлено»)");
        sb.AppendLine();
        sb.AppendLine($"## Калибровка ({Title(card["model"]!.GetValue<string>())})");
        sb.AppendLine();
        sb.AppendLine("| Вероятность | Строк | Средняя предсказанная | Наблюдённая доля |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in metrics[card["model"]!.GetValue<string>()]!["calibration"]!.AsArray())
            sb.AppendLine($"| {c!["from"]}–{c["to"]} | {c["rows"]} | {c["predicted"]} | {c["observed"]} |");
        sb.AppendLine();
        sb.AppendLine("## Важность признаков");
        sb.AppendLine();
        sb.AppendLine("Перестановочная: на сколько падает PR-AUC на отложенном периоде, если перемешать признак между строками. Это вклад в ранжир, а не причинный эффект.");
        sb.AppendLine();
        sb.AppendLine("| Признак | Падение PR-AUC |");
        sb.AppendLine("|---|---|");
        foreach (var f in card["topFeatures"]!.AsArray()) sb.AppendLine($"| {f!["name"]} | {f["prAucDrop"]} |");
        sb.AppendLine();
        if (card["causes"] is JsonObject causes)
        {
            sb.AppendLine("## Заложенные причины симулятора");
            sb.AppendLine();
            sb.AppendLine("Для «не продлено» в проверке — причина ухода клиента из truth.jsonl (partial — частичный отказ без ухода). Полнота top-K по причинам и пять признаков с наибольшей стандартизованной разницей против продлённых.");
            sb.AppendLine();
            sb.AppendLine("| Причина | Случаев | R@K модель | R@K правила | Сильнейшие отличия | Совпадает с ожидаемым |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var (cause, node) in causes)
            {
                var c = node!.AsObject();
                var signals = string.Join(", ", c["signals"]!.AsArray().Select(s => $"{s!["name"]} ({F(s["smd"]!.GetValue<double>(), "+0.00;-0.00")})"));
                var match = c["signalsMatchExpected"] is { } v ? (v.GetValue<bool>() ? "да" : "нет") : "—";
                sb.AppendLine($"| {cause} | {c["positives"]} | {c["recallAtKModel"]} | {c["recallAtKRules"]} | {signals} | {match} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine("## Проверка на утечки");
        sb.AppendLine();
        var leak = card["leakage"]!.AsObject();
        sb.AppendLine($"- Признаки считаются на момент решения из состояния после событий с occurredAt ≤ t (переигрывание журнала); идентификаторы и даты в признаки не входят.");
        sb.AppendLine("- Обучение только на сроках, чей исход созрел до начала проверки (E + N раньше её начала): метки обучения не заходят в проверку.");
        sb.AppendLine($"- Модель на перемешанных метках: PR-AUC {leak["shuffledLabelsPrAuc"]}, ROC-AUC {leak["shuffledLabelsRocAuc"]} при доле положительных {leak["baseRate"]} — {(leak["shuffledLabelsRocAuc"]!.GetValue<double>() < 0.6 ? "не лучше случайной, утечки через конвейер не видно" : "ВЫШЕ случайной: проверить утечку")}.");
        sb.AppendLine();
        sb.AppendLine("## Ограничения");
        sb.AppendLine();
        sb.AppendLine("- Модель — вероятность «не продлено» по журналу событий; основным баллом или тенью её делает переключатель на странице «Модель».");
        sb.AppendLine("- Правила — индекс приоритета, не вероятность: сравнение идёт по ранжированию (PR-AUC, top-K), Brier только у модели.");
        sb.AppendLine("- Продвижение — после проверки на реальных исходах организаторов и согласования бизнес-цели и ёмкости.");
        sb.AppendLine();
        sb.AppendLine($"ONNX sha256 `{card["onnxSha256"]}`, вход `{card["input"]}` [N × {card["features"]!.AsArray().Count}], выход `{card["output"]}`; расхождение ONNX с ML.NET {F(card["parityMaxAbsDiff"]!.GetValue<double>(), "E1")}.");
        return sb.ToString();
    }

    private static string Title(string name) => name == "logistic" ? "Логистическая регрессия" : "FastTree (бустинг)";

    private static string Ci(JsonNode? ci) => ci is JsonArray a ? $"[{a[0]}; {a[1]}]" : "";

    private static string Verdict(JsonNode? ci) => ci is JsonArray a
        ? a[0]!.GetValue<double>() > 0 ? " — лучше правил" : a[1]!.GetValue<double>() < 0 ? " — хуже правил" : " — разница в пределах неопределённости"
        : "";

    private static string F(double x, string format) => x.ToString(format, CultureInfo.InvariantCulture);
}
