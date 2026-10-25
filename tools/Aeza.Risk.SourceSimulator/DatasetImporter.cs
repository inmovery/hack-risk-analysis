using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Отправка истории набора пакетным импортом анализатора (коннектор batch-import)</summary>
public static class DatasetImporter
{
    public const string StateFile = "import.json";

    public static async Task<JsonObject> ImportAsync(HttpClient http, string dir, string runId, DateTimeOffset end, ILogger log, TimeSpan poll, CancellationToken ct)
    {
        var events = Path.Combine(dir, Dataset.EventsFile);
        var gz = events + ".gz";
        if (!File.Exists(gz) || File.GetLastWriteTimeUtc(gz) < File.GetLastWriteTimeUtc(events))
        {
            log.LogInformation("Сжимаю {File} для импорта", events);
            var tmp = gz + ".tmp";
            await using (var input = File.OpenRead(events))
            await using (var output = File.Create(tmp))
            await using (var z = new GZipStream(output, CompressionLevel.Fastest))
                await input.CopyToAsync(z, ct);
            File.Move(tmp, gz, overwrite: true);
        }

        var statePath = Path.Combine(dir, StateFile);
        long? id = File.Exists(statePath) ? JsonNode.Parse(await File.ReadAllTextAsync(statePath, ct))?["id"]?.GetValue<long>() : null;
        JsonObject? view = id is { } known ? await GetAsync(http, known, ct) : null;
        if (view is null || view["status"]?.GetValue<string>() == "failed")
        {
            log.LogInformation("Загружаю историю: {Bytes} байт gzip", new FileInfo(gz).Length);
            await using var file = File.OpenRead(gz);
            using var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            using var resp = await http.PostAsync("/api/v1/source/events/import?synthetic=true", content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode != HttpStatusCode.Accepted)
                throw new InvalidOperationException($"Импорт не принят: {(int)resp.StatusCode} {(text.Length > 300 ? text[..300] : text)}");
            view = JsonNode.Parse(text)!.AsObject();
            id = view["id"]!.GetValue<long>();
            await File.WriteAllTextAsync(statePath, new JsonObject { ["id"] = id, ["runId"] = runId }.ToJsonString(), ct);
            log.LogInformation("Импорт №{Id}: {Events} событий в {Chunks} кусках", id, view["eventsTotal"], view["chunksTotal"]);
        }

        var lastDone = -1;
        while (view!["status"]!.GetValue<string>() is "uploading" or "pending" or "running")
        {
            await Task.Delay(poll, ct);
            view = await GetAsync(http, id!.Value, ct) ?? throw new InvalidOperationException($"Импорт №{id} пропал из анализатора");
            var done = view["chunksDone"]!.GetValue<int>();
            if (done != lastDone && (done % 20 == 0 || done == view["chunksTotal"]!.GetValue<int>()))
                log.LogInformation("Импорт №{Id}: {Done}/{Total} кусков, применено {Applied}, записано {Recorded}, повторов {Duplicates}, отклонено {Rejected}",
                    id, done, view["chunksTotal"], view["applied"], view["recorded"], view["duplicates"], view["rejected"]);
            lastDone = done;
        }
        if (view["status"]!.GetValue<string>() != "succeeded")
            throw new InvalidOperationException($"Импорт №{id} не выполнен: {view["errorCode"]}");

        // Источник подтверждает полноту истории: «не продлено» по срокам до конца истории можно решать
        var watermark = new JsonObject
        {
            ["schemaVersion"] = 2, ["eventId"] = $"{runId}-wm-{end.UtcTicks}", ["type"] = "stream.watermark", ["occurredAt"] = Iso.Format(end),
            ["data"] = new JsonObject { ["upTo"] = Iso.Format(end) },
        };
        using var body = new StringContent("{\"synthetic\":true,\"events\":[" + watermark.ToJsonString() + "]}", Encoding.UTF8, "application/json");
        using var wm = await http.PostAsync("/api/v1/source/events", body, ct);
        log.LogInformation("Водяной знак до {End}: {Status}", Iso.Format(end), (int)wm.StatusCode);
        return view;
    }

    private static async Task<JsonObject?> GetAsync(HttpClient http, long id, CancellationToken ct)
    {
        using var r = await http.GetAsync($"/api/v1/source/imports/{id}", ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))?.AsObject();
    }
}
