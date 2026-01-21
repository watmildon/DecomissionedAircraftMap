using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;
using System.Collections.Immutable;

class Program
{
    static HttpClient s_HttpClient = new HttpClient();
    static string s_ImagesFolder = ".." + Path.DirectorySeparatorChar + "images" + Path.DirectorySeparatorChar;
    static string s_GeoJsonPath = ".." + Path.DirectorySeparatorChar + "aircraft.geojson";
    static List<string> s_OsmItemsNeedingReview = new List<string>();
    const int RequestDelayMs = 3000; // Delay between requests to respect rate limits
    const int MaxRetries = 3;
    const int MinExpectedElements = 5000; // Safety threshold - current count is ~2200

    static async Task Main(string[] args)
    {
        s_HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OSMMapMakerBot/1.0 (https://github.com/watmildon/DecomissionedAircraftMap)");
        s_HttpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        // Parse command-line arguments for backend selection
        var backend = QueryBackend.Overpass;
        if (args.Length > 0)
        {
            backend = args[0].ToLowerInvariant() switch
            {
                "postpass" => QueryBackend.Postpass,
                "qlever" => QueryBackend.QLever,
                "overpass" => QueryBackend.Overpass,
                _ => QueryBackend.Overpass
            };
        }

        Console.WriteLine($"Using {backend} backend");

        string[] tags = { "wikidata", "model:wikidata", "subject:wikidata" };
        var queryProvider = CreateQueryProvider(backend);
        var runner = new AnalysisRunner(queryProvider, tags, s_ImagesFolder, s_HttpClient);

        runner.RunAnalysis();

        // Safety check: ensure we got a reasonable number of elements
        int elementCount = runner.OsmData?.elements?.Length ?? 0;
        if (elementCount < MinExpectedElements)
        {
            Console.Error.WriteLine($"ERROR: Only {elementCount} elements returned, expected at least {MinExpectedElements}.");
            Console.Error.WriteLine("This may indicate an Overpass timeout, partial response, or server issue.");
            Console.Error.WriteLine("Aborting to prevent data loss.");
            Environment.Exit(1);
        }
        Console.WriteLine($"Element count check passed: {elementCount} elements (minimum: {MinExpectedElements})");

        Console.WriteLine("Files to download...");

        foreach (var file in runner.ItemsNeedingDownload.ToImmutableSortedSet<string>())
        {
            // Skip semicolon-delimited entries (invalid OSM tagging)
            if (file.Contains(';'))
            {
                Console.WriteLine($"Skipping semicolon-delimited entry: {file}");
                s_OsmItemsNeedingReview.Add(file);
                continue;
            }

            await DownloadThumbnailFromWikidataId(file);
            await Task.Delay(RequestDelayMs);
        }

        Console.WriteLine();

        const int MaxDeletionsAllowed = 10;
        int filesToDeleteCount = runner.FilesToDelete.Count();

        if (filesToDeleteCount > MaxDeletionsAllowed)
        {
            Console.Error.WriteLine($"ERROR: {filesToDeleteCount} files would be deleted, which exceeds the safety limit of {MaxDeletionsAllowed}.");
            Console.Error.WriteLine("This may indicate a problem with the Overpass query or data source.");
            Console.Error.WriteLine("Files that would be deleted:");
            foreach (var file in runner.FilesToDelete)
            {
                Console.Error.WriteLine($"  {file}");
            }
            Environment.Exit(1);
        }

        if (filesToDeleteCount > 0)
        {
            Console.WriteLine($"Deleting {filesToDeleteCount} unneeded files");

            foreach (var file in runner.FilesToDelete)
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

        // Safety check: compare new feature count against existing file
        int existingFeatureCount = GetExistingGeoJsonFeatureCount();
        if (existingFeatureCount > 0)
        {
            int threshold = (int)(existingFeatureCount * 0.8); // Allow up to 20% reduction
            if (features.Count < threshold)
            {
                Console.Error.WriteLine($"ERROR: New GeoJSON would have {features.Count} features, but existing file has {existingFeatureCount}.");
                Console.Error.WriteLine($"This is more than a 20% reduction (threshold: {threshold}).");
                Console.Error.WriteLine("This may indicate a problem with the data source. Aborting to prevent data loss.");
                Environment.Exit(1);
            }
            Console.WriteLine($"GeoJSON feature count check passed: {features.Count} new vs {existingFeatureCount} existing");
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

    private static int GetExistingGeoJsonFeatureCount()
    {
        if (!File.Exists(s_GeoJsonPath))
            return 0;

        try
        {
            string existingJson = File.ReadAllText(s_GeoJsonPath);
            var existingGeoJson = JObject.Parse(existingJson);
            var featuresArray = existingGeoJson["features"] as JArray;
            return featuresArray?.Count ?? 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not read existing GeoJSON file: {ex.Message}");
            return 0;
        }
    }

    private static async Task<bool> DownloadThumbnailFromWikidataId(string wikidataId)
    {
        if (wikidataId == null)
        {
            return false;
        }
        string fileName = $"{wikidataId}.jpg";

        if (File.Exists(s_ImagesFolder + fileName))
        {
            Console.WriteLine($"File exists for: {wikidataId}");
            return true;
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
                    return false;
                }

                if (imageName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"File type svg is not supported: {wikidataId}");
                    return false;
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
                    return false;
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

                return true;
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
                return false;
            }
        }

        Console.WriteLine($"Failed after {MaxRetries} attempts: {wikidataId}");
        return false;
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

    private static IQueryProvider CreateQueryProvider(QueryBackend backend)
    {
        return backend switch
        {
            QueryBackend.Overpass => new OverpassQueryProvider(OverpassQuery, "https://maps.mail.ru/osm/tools/overpass/api/interpreter"),
            QueryBackend.Postpass => new PostpassQueryProvider(PostpassQuery),
            QueryBackend.QLever => new QLeverQueryProvider(QLeverQuery),
            _ => new OverpassQueryProvider(OverpassQuery, "https://overpass.private.coffee/api/interpreter")
        };
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

    private static readonly string PostpassQuery = """
        SELECT osm_id, tags
        FROM postpass_pointpolygon
        WHERE (
            (tags->>'historic' = 'aircraft')
            OR (tags->>'historic' = 'memorial' AND tags->>'memorial' = 'aircraft')
            OR (tags->>'historic' = 'wreck' AND tags->>'wreck:type' = 'aircraft')
            OR (tags->>'historic' = 'monument' AND tags->>'monument' = 'aircraft')
            OR (tags->>'historic' = 'aircraft_wreck')
            OR (tags->>'artwork_type' = 'aircraft')
        )
        AND (
            tags ? 'wikidata'
            OR tags ? 'model:wikidata'
            OR tags ? 'subject:wikidata'
        )
        """;

    private static readonly string QLeverQuery = """
        PREFIX osmkey: <https://www.openstreetmap.org/wiki/Key:>
        PREFIX wd: <http://www.wikidata.org/entity/>

        SELECT ?wikidata ?model_wikidata ?subject_wikidata WHERE {
          {
            ?item osmkey:historic "aircraft" .
          } UNION {
            ?item osmkey:historic "memorial" .
            ?item osmkey:memorial "aircraft" .
          } UNION {
            ?item osmkey:historic "wreck" .
            ?item osmkey:wreck:type "aircraft" .
          } UNION {
            ?item osmkey:historic "monument" .
            ?item osmkey:monument "aircraft" .
          } UNION {
            ?item osmkey:historic "aircraft_wreck" .
          } UNION {
            ?item osmkey:artwork_type "aircraft" .
          }
          OPTIONAL { ?item osmkey:wikidata ?wikidata . }
          OPTIONAL { ?item osmkey:model:wikidata ?model_wikidata . }
          OPTIONAL { ?item osmkey:subject:wikidata ?subject_wikidata . }
          FILTER(BOUND(?wikidata) || BOUND(?model_wikidata) || BOUND(?subject_wikidata))
        }
        """;
}
