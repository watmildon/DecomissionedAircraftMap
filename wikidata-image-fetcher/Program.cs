using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Collections.Immutable;

// What a thumbnail fetch attempt concluded. The distinction that matters is
// between a definite "there is no image here", which is worth remembering, and a
// transient failure, which is not.
enum FetchOutcome
{
    Downloaded,
    AlreadyPresent,
    NoImage,
    UnsupportedFormat,
    NoThumbnail,
    TransientError,
}

class Program
{
    static HttpClient s_HttpClient = new HttpClient();
    static string s_ImagesFolder = ".." + Path.DirectorySeparatorChar + "images" + Path.DirectorySeparatorChar;
    static string s_GeoJsonPath = ".." + Path.DirectorySeparatorChar + "aircraft.geojson";
    static string s_ImageCachePath = ".." + Path.DirectorySeparatorChar + "wikidataImageCache.json";
    static List<string> s_OsmItemsNeedingReview = new List<string>();
    const int RequestDelayMs = 3000; // Delay between requests to respect rate limits
    const int MaxRetries = 3;

    // Backstop on deletions. The response validator rejects most bad data before
    // we reach this point, so this only has to be loose enough for ordinary OSM
    // churn and tight enough to catch a response that slipped through.
    const int MinDeletionsAllowed = 10;
    const double MaxDeletionFraction = 0.02;

    // How much smaller the new GeoJSON may be than the committed one.
    const double MaxGeoJsonShrinkFraction = 0.2;

    static int s_BaselineFeatureCount;

    static async Task Main()
    {
        s_HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OSMMapMakerBot/1.0 (https://github.com/watmildon/DecomissionedAircraftMap)");
        s_HttpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        string[] tags = { "wikidata", "model:wikidata", "subject:wikidata" };

        // Base the safety thresholds on the last committed run rather than on
        // hardcoded counts, so they keep tracking the dataset as it grows instead
        // of needing to be retuned by hand.
        var (baselineFeatures, baselineIds) = ReadGeoJsonBaseline();
        s_BaselineFeatureCount = baselineFeatures;
        Console.WriteLine($"Baseline from committed GeoJSON: {baselineFeatures} features, {baselineIds} distinct wikidata ids");

        var validator = new OsmResponseValidator(baselineFeatures, baselineIds);
        var queryProvider = new OverpassQueryProvider(OverpassQuery, validator: validator);
        var runner = new AnalysisRunner(queryProvider, tags, s_ImagesFolder, s_HttpClient);

        runner.RunAnalysis();

        int elementCount = runner.OsmData?.elements?.Length ?? 0;
        Console.WriteLine($"Query returned {elementCount} elements");

        // Decide about deletions before downloading anything. FilesToDelete is
        // settled by the analysis above and downloading only ever adds files that
        // are needed, so there is no reason to spend fifteen minutes fetching
        // images before discovering the run has to be thrown away.
        var filesToDelete = runner.FilesToDelete.ToList();
        int imagesOnDisk = Directory.Exists(s_ImagesFolder) ? Directory.GetFiles(s_ImagesFolder).Length : 0;
        int maxDeletionsAllowed = Math.Max(MinDeletionsAllowed, (int)(imagesOnDisk * MaxDeletionFraction));

        if (filesToDelete.Count > maxDeletionsAllowed)
        {
            Console.Error.WriteLine($"ERROR: {filesToDelete.Count} files would be deleted, which exceeds the safety limit of {maxDeletionsAllowed}.");
            Console.Error.WriteLine("This may indicate a problem with the Overpass query or data source.");
            Console.Error.WriteLine("Files that would be deleted:");
            foreach (var file in filesToDelete)
            {
                Console.Error.WriteLine($"  {file}");
            }
            Environment.Exit(1);
        }

        var cache = NegativeCache.Load(s_ImageCachePath);
        var itemsNeedingDownload = runner.ItemsNeedingDownload.ToImmutableSortedSet<string>();
        var now = DateTime.UtcNow;

        Console.WriteLine($"{itemsNeedingDownload.Count} items have no local thumbnail; {cache.Count} known-negative lookups cached");

        int downloaded = 0;
        int skipped = 0;

        foreach (var file in itemsNeedingDownload)
        {
            // Skip semicolon-delimited entries (invalid OSM tagging)
            if (file.Contains(';'))
            {
                Console.WriteLine($"Skipping semicolon-delimited entry: {file}");
                s_OsmItemsNeedingReview.Add(file);
                continue;
            }

            // The bulk of this list is items Wikidata has already told us have no
            // image. Re-asking every night costs a rate-limit delay each and
            // almost never changes the answer.
            if (cache.ShouldSkip(file, now))
            {
                skipped++;
                continue;
            }

            var outcome = await DownloadThumbnailFromWikidataId(file);

            switch (outcome)
            {
                case FetchOutcome.Downloaded:
                    downloaded++;
                    cache.Forget(file);
                    break;
                case FetchOutcome.AlreadyPresent:
                    cache.Forget(file);
                    break;
                case FetchOutcome.NoImage:
                    cache.RecordNegative(file, "no-p18", now);
                    break;
                case FetchOutcome.UnsupportedFormat:
                    cache.RecordNegative(file, "unsupported-format", now);
                    break;
                case FetchOutcome.NoThumbnail:
                    cache.RecordNegative(file, "no-thumbnail", now);
                    break;
                case FetchOutcome.TransientError:
                    // Deliberately not cached, so the next run retries it.
                    break;
            }

            if (outcome != FetchOutcome.AlreadyPresent)
            {
                await Task.Delay(RequestDelayMs);
            }
        }

        int pruned = cache.PruneTo(itemsNeedingDownload);
        cache.Save();

        Console.WriteLine($"Downloaded {downloaded}, skipped {skipped} cached negatives, pruned {pruned} stale cache entries");
        Console.WriteLine();

        if (filesToDelete.Count > 0)
        {
            Console.WriteLine($"Deleting {filesToDelete.Count} unneeded files");

            foreach (var file in filesToDelete)
            {
                Console.WriteLine($"Deleting {file}");
                File.Delete(file);
            }
        }
        else
        {
            Console.WriteLine("No files to delete");
        }

        runner.RunAnalysis();

        Console.WriteLine("Writing wikidataItemsNeedingReview file");

        using (var sr = new StreamWriter("../wikidataItemsNeedingReview.txt"))
        {
            foreach (var id in runner.ItemsNeedingDownload.ToImmutableSortedSet<string>())
            {
                sr.WriteLine(id);
            }
        }

        if (s_OsmItemsNeedingReview.Count > 0)
        {
            Console.WriteLine("Writing osmItemsNeedingReview file");

            using (var sr = new StreamWriter("../osmItemsNeedingReview.txt"))
            {
                foreach (var id in s_OsmItemsNeedingReview.Order())
                {
                    sr.WriteLine(id);
                }
            }
        }

        // Write GeoJSON file for the Ultra map
        WriteGeoJsonFile(runner.OsmData);
    }

    private static void WriteGeoJsonFile(OsmItems? osmData)
    {
        if (osmData == null || osmData.elements == null || osmData.elements.Length == 0)
        {
            Console.WriteLine("No OSM data to write to GeoJSON");
            return;
        }

        Console.WriteLine("Preparing aircraft.geojson file");

        var features = new List<object>();

        foreach (var element in osmData.elements)
        {
            // Get coordinates - nodes have lat/lon directly, ways/relations have center
            double? lat = element.lat ?? element.center?.lat;
            double? lon = element.lon ?? element.center?.lon;

            if (!lat.HasValue || !lon.HasValue)
                continue;

            // Build properties from tags
            var properties = new Dictionary<string, object?>();

            // Add explicitly defined tags
            if (element.tags.wikidata != null)
                properties["wikidata"] = element.tags.wikidata;
            if (element.tags.modelwikidata != null)
                properties["model:wikidata"] = element.tags.modelwikidata;
            if (element.tags.subjectwikidata != null)
                properties["subject:wikidata"] = element.tags.subjectwikidata;
            if (element.tags.wikipedia != null)
                properties["wikipedia"] = element.tags.wikipedia;

            // Add all additional tags from the Overpass response
            if (element.tags.AdditionalTags != null)
            {
                foreach (var kvp in element.tags.AdditionalTags)
                {
                    // Convert JToken to string value
                    properties[kvp.Key] = kvp.Value?.ToString();
                }
            }

            properties["@id"] = $"{element.type}/{element.id}";

            var feature = new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Point",
                    coordinates = new[] { lon.Value, lat.Value }
                },
                properties
            };

            features.Add(feature);
        }

        // Final backstop: compare against the file we are about to overwrite.
        if (s_BaselineFeatureCount > 0)
        {
            int threshold = (int)(s_BaselineFeatureCount * (1 - MaxGeoJsonShrinkFraction));
            if (features.Count < threshold)
            {
                Console.Error.WriteLine($"ERROR: New GeoJSON would have {features.Count} features, but existing file has {s_BaselineFeatureCount}.");
                Console.Error.WriteLine($"This is more than a {MaxGeoJsonShrinkFraction:P0} reduction (threshold: {threshold}).");
                Console.Error.WriteLine("This may indicate a problem with the data source. Aborting to prevent data loss.");
                Environment.Exit(1);
            }
            Console.WriteLine($"GeoJSON feature count check passed: {features.Count} new vs {s_BaselineFeatureCount} existing");
        }

        var geojson = new
        {
            type = "FeatureCollection",
            features
        };

        var json = JsonConvert.SerializeObject(geojson, Formatting.Indented);

        // Write to temp file first, then atomically rename
        string tempPath = s_GeoJsonPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, s_GeoJsonPath, overwrite: true);

        Console.WriteLine($"Wrote {features.Count} features to aircraft.geojson");
    }

    // Reads the committed GeoJSON so this run can be measured against the last
    // good one. Returns zeroes when there is nothing to compare against, which
    // leaves the validator on its absolute floor.
    private static (int FeatureCount, int WikidataIdCount) ReadGeoJsonBaseline()
    {
        if (!File.Exists(s_GeoJsonPath))
            return (0, 0);

        try
        {
            var existingGeoJson = JObject.Parse(File.ReadAllText(s_GeoJsonPath));
            var featuresArray = existingGeoJson["features"] as JArray;

            if (featuresArray == null)
                return (0, 0);

            var ids = new HashSet<string>();

            foreach (var feature in featuresArray)
            {
                var properties = feature["properties"];
                if (properties == null)
                    continue;

                foreach (var key in new[] { "wikidata", "model:wikidata", "subject:wikidata" })
                {
                    var value = properties[key]?.ToString();
                    if (!string.IsNullOrEmpty(value))
                        ids.Add(value);
                }
            }

            return (featuresArray.Count, ids.Count);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not read existing GeoJSON file: {ex.Message}");
            return (0, 0);
        }
    }

    private static async Task<FetchOutcome> DownloadThumbnailFromWikidataId(string wikidataId)
    {
        string fileName = $"{wikidataId}.jpg";

        if (File.Exists(s_ImagesFolder + fileName))
        {
            Console.WriteLine($"File exists for: {wikidataId}");
            return FetchOutcome.AlreadyPresent;
        }

        string apiUrl = $"https://www.wikidata.org/wiki/Special:EntityData/{wikidataId}.json";

        // Retry loop with exponential backoff for rate limiting
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                HttpResponseMessage response = await s_HttpClient.GetAsync(apiUrl);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    int backoffMs = attempt * 2000; // 2s, 4s, 6s
                    Console.WriteLine($"Rate limited for {wikidataId}, waiting {backoffMs}ms (attempt {attempt}/{MaxRetries})");
                    await Task.Delay(backoffMs);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                string jsonData = await response.Content.ReadAsStringAsync();

                // Parse JSON and find the image property (P18)
                JObject wikidataJson = JObject.Parse(jsonData);
                string? imageName = wikidataJson
                    .SelectToken($"$.entities.{wikidataId}.claims.P18[0].mainsnak.datavalue.value")
                    ?.ToString();

                if (string.IsNullOrEmpty(imageName))
                {
                    Console.WriteLine($"No image (P18) found: {wikidataId}");
                    return FetchOutcome.NoImage;
                }

                if (imageName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"File type svg is not supported: {wikidataId}");
                    return FetchOutcome.UnsupportedFormat;
                }

                // Use Wikimedia API to get thumbnail URL (avoids 403 errors from direct file access)
                string commonsFileName = "File:" + imageName.Replace(' ', '_');
                string commonsApiUrl = $"https://commons.wikimedia.org/w/api.php?action=query&titles={Uri.EscapeDataString(commonsFileName)}&prop=imageinfo&iiprop=url&iiurlwidth=100&format=json";

                response = await s_HttpClient.GetAsync(commonsApiUrl);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    int backoffMs = attempt * 2000;
                    Console.WriteLine($"Rate limited fetching image info for {wikidataId}, waiting {backoffMs}ms (attempt {attempt}/{MaxRetries})");
                    await Task.Delay(backoffMs);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                string commonsJson = await response.Content.ReadAsStringAsync();
                JObject commonsData = JObject.Parse(commonsJson);

                // Navigate to the thumbnail URL in the response
                string? imageUrl = commonsData
                    .SelectToken("$.query.pages.*.imageinfo[0].thumburl")
                    ?.ToString();

                if (string.IsNullOrEmpty(imageUrl))
                {
                    Console.WriteLine($"Could not get thumbnail URL for {wikidataId}");
                    return FetchOutcome.NoThumbnail;
                }

                response = await s_HttpClient.GetAsync(imageUrl);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    int backoffMs = attempt * 2000;
                    Console.WriteLine($"Rate limited downloading image for {wikidataId}, waiting {backoffMs}ms (attempt {attempt}/{MaxRetries})");
                    await Task.Delay(backoffMs);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                await using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                {
                    // Load the image directly from the memory stream
                    using (Image image = Image.Load(contentStream))
                    {
                        ScaleAndSaveImage(wikidataId, image, 100);
                        Console.WriteLine($"Image saved: {wikidataId}");
                    }
                }

                return FetchOutcome.Downloaded;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                int backoffMs = attempt * 2000;
                Console.WriteLine($"Error for {wikidataId}: {ex.Message}, retrying in {backoffMs}ms (attempt {attempt}/{MaxRetries})");
                await Task.Delay(backoffMs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return FetchOutcome.TransientError;
            }
        }

        Console.WriteLine($"Failed after {MaxRetries} attempts: {wikidataId}");
        return FetchOutcome.TransientError;
    }

    private static void ScaleAndSaveImage(string imageName, Image image, int newWidth)
    {
        // Calculate the new height to maintain aspect ratio
        int newHeight = (int)(image.Height * (newWidth / (float)image.Width));

        // Resize the image while maintaining quality
        image.Mutate(x => x.Resize(newWidth, newHeight));

        using (var memoryStream = new MemoryStream())
        {
            image.Save($"{s_ImagesFolder}{imageName}.jpg");
        }
    }

    private static readonly string OverpassQuery = """
        [out:json][timeout:90];
        (
          nwr["historic"="aircraft"][wikidata];
          nwr["historic"="aircraft"]["model:wikidata"];
          nwr["historic"="aircraft"]["subject:wikidata"];
          nwr["historic"="memorial"]["memorial"="aircraft"][wikidata];
          nwr["historic"="memorial"]["memorial"="aircraft"]["model:wikidata"];
          nwr["historic"="memorial"]["memorial"="aircraft"]["subject:wikidata"];
          nwr["historic"="wreck"]["wreck:type"="aircraft"][wikidata];
          nwr["historic"="wreck"]["wreck:type"="aircraft"]["model:wikidata"];
          nwr["historic"="wreck"]["wreck:type"="aircraft"]["subject:wikidata"];
          nwr[historic=monument][monument=aircraft][wikidata];
          nwr[historic=monument][monument=aircraft]["model:wikidata"];
          nwr[historic=monument][monument=aircraft]["subject:wikidata"];
          nwr[historic=aircraft_wreck][wikidata];
          nwr[historic=aircraft_wreck]["model:wikidata"];
          nwr[historic=aircraft_wreck]["subject:wikidata"];
          nwr["artwork_type"=aircraft][wikidata];
          nwr["artwork_type"=aircraft]["model:wikidata"];
          nwr["artwork_type"=aircraft]["subject:wikidata"];
        );
        out center;
        """;
}
