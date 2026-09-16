using Newtonsoft.Json.Linq;

// Asks Wikidata for the P18 (image) claim of many items at once. The nightly run
// needs the current image for every item it knows about, not just the ones with
// no local file, and one request per item makes that unaffordable. wbgetentities
// answers fifty at a time, which is what makes checking everything nightly cheap.
//
// The query service would be cheaper still, but its index lags behind Wikidata by
// enough that recently edited items come back looking like they have no image,
// which would make this run delete or replace good thumbnails.
public class WikidataImageLookup
{
    public enum Status
    {
        HasImage,
        NoImage,

        // The API gave us no trustworthy answer. Callers must not take any
        // destructive action on these.
        Unresolved,
    }

    public record Result(Status Status, string? FileName);

    private const int BatchSize = 50; // API limit for anonymous callers
    private const int BatchDelayMs = 1000;
    private const int MaxDropsPerBatch = 5;

    private readonly HttpClient client;

    public WikidataImageLookup(HttpClient client)
    {
        this.client = client;
    }

    public async Task<Dictionary<string, Result>> FetchAsync(IReadOnlyList<string> ids)
    {
        var results = new Dictionary<string, Result>(StringComparer.Ordinal);
        int batches = 0;

        for (int offset = 0; offset < ids.Count; offset += BatchSize)
        {
            var batch = ids.Skip(offset).Take(BatchSize).ToList();
            await FetchBatchAsync(batch, results);
            batches++;

            if (offset + BatchSize < ids.Count)
            {
                await Task.Delay(BatchDelayMs);
            }
        }

        int withImage = results.Values.Count(r => r.Status == Status.HasImage);
        int without = results.Values.Count(r => r.Status == Status.NoImage);
        int unresolved = results.Values.Count(r => r.Status == Status.Unresolved);
        Console.WriteLine($"Looked up {ids.Count} items in {batches} requests: {withImage} with P18, {without} without, {unresolved} unresolved");

        return results;
    }

    private async Task FetchBatchAsync(List<string> batch, Dictionary<string, Result> results)
    {
        var pending = new List<string>(batch);

        for (int drop = 0; drop <= MaxDropsPerBatch && pending.Count > 0; drop++)
        {
            string url = "https://www.wikidata.org/w/api.php?action=wbgetentities"
                       + $"&ids={string.Join("|", pending)}&props=claims&format=json";

            JObject payload;

            try
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                payload = JObject.Parse(await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Batch lookup failed: {ex.Message}");
                MarkUnresolved(pending, results);
                return;
            }

            var error = payload["error"];

            if (error != null)
            {
                // A single id Wikidata does not recognise fails the entire batch
                // rather than returning the other forty-nine. The error names the
                // offender, so drop it and ask again for the rest.
                string? culprit = error["id"]?.ToString();

                if (error["code"]?.ToString() == "no-such-entity" && !string.IsNullOrEmpty(culprit))
                {
                    Console.WriteLine($"Wikidata does not recognise {culprit}; dropping it from the batch");
                    results[culprit] = new Result(Status.Unresolved, null);
                    pending.Remove(culprit);
                    continue;
                }

                Console.Error.WriteLine($"Batch lookup error: {error["info"]}");
                MarkUnresolved(pending, results);
                return;
            }

            if (payload["entities"] is not JObject entities)
            {
                MarkUnresolved(pending, results);
                return;
            }

            foreach (var id in pending)
            {
                var entity = entities[id];

                if (entity == null || entity["missing"] != null)
                {
                    results[id] = new Result(Status.Unresolved, null);
                    continue;
                }

                string? fileName = SelectPreferred(entity["claims"]?["P18"] as JArray);

                results[id] = fileName == null
                    ? new Result(Status.NoImage, null)
                    : new Result(Status.HasImage, fileName);
            }

            return;
        }

        MarkUnresolved(pending, results);
    }

    private static void MarkUnresolved(IEnumerable<string> ids, Dictionary<string, Result> results)
    {
        foreach (var id in ids)
        {
            if (!results.ContainsKey(id))
            {
                results[id] = new Result(Status.Unresolved, null);
            }
        }
    }

    // Wikidata's "truthy" semantics: a preferred statement wins outright, normal
    // statements apply only when nothing is preferred, and deprecated ones never
    // apply. Roughly one item in twenty-five here carries more than one P18, and
    // the previous code simply took whichever came back first.
    public static string? SelectPreferred(JArray? claims)
    {
        if (claims == null)
        {
            return null;
        }

        foreach (var rank in new[] { "preferred", "normal" })
        {
            foreach (var claim in claims)
            {
                if (claim["rank"]?.ToString() != rank)
                {
                    continue;
                }

                var snak = claim["mainsnak"];

                // "novalue" and "somevalue" snaks carry no filename.
                if (snak?["snaktype"]?.ToString() != "value")
                {
                    continue;
                }

                var value = snak["datavalue"]?["value"]?.ToString();

                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}
