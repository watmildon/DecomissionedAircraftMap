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
    static async Task Main(string[] args)
    {
        s_HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OSMMapMakerBot");

        string overpassQuery = """
        [out:json][timeout:25];
        ( 
          nwr[historic=aircraft][wikidata];
          nwr[historic=aircraft]["model:wikidata"];
          nwr[historic=memorial][memorial=aircraft][wikidata];
          nwr[historic=memorial][memorial=aircraft]["model:wikidata"];
          nwr[historic=wreck]["wreck:type"=aircraft][wikidata];
          nwr[historic=wreck]["wreck:type"=aircraft]["model:wikidata"];
          nwr[historic=monument][monument=aircraft][wikidata];
          nwr[historic=monument][monument=aircraft]["model:wikidata"];
          nwr[historic=aircraft_wreck][wikidata];
          nwr[historic=aircraft_wreck]["model:wikidata"];
        );
        out tags;
        """;
        string[] tags = { "wikidata", "model:wikidata" };

        var runner = new AnalysisRunner(overpassQuery, tags, s_ImagesFolder, s_HttpClient);

        runner.RunAnalysis();

        Console.WriteLine("Files to download...");

        foreach (var file in runner.ItemsNeedingDownload.ToImmutableSortedSet<string>())
        {
            await DownloadThumbnailFromWikidataId(file);
        }

        Console.WriteLine();

        if (runner.FilesToDelete.Count() > 0)
        {
            Console.WriteLine("Deleting unneeded files");

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

        try
        {
            HttpResponseMessage response = await s_HttpClient.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();

            string jsonData = await response.Content.ReadAsStringAsync();

            // Parse JSON and find the image property (P18)
            JObject wikidataJson = JObject.Parse(jsonData);
            string imageName = wikidataJson
                .SelectToken($"$.entities.{wikidataId}.claims.P18[0].mainsnak.datavalue.value")
                ?.ToString();

            if (imageName == null)
            {
                Console.WriteLine($"No image (P18) found: {wikidataId}");
                return false;
            }

            if (imageName.EndsWith(".svg"))
            {
                Console.WriteLine($"File type svg is not supported: {wikidataId}");
                return false;
            }

            string imageUrl = $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(imageName)}";

            response = await s_HttpClient.GetAsync(imageUrl);
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

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return false;
        }

        return true;
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
}
