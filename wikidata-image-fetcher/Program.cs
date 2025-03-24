using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Drawing;
using System.Windows.Markup;
using Newtonsoft.Json;
class Program
{
    static HttpClient httpClient = new HttpClient();
    static async Task Main(string[] args)
    {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OSMMapMakerBot");

        string featureTag = "\"historic\"=\"aircraft\"";
        string dataTag = "wikidata";

        var checkedList = await DownloadThumbnailsForOverpassQuery(featureTag, dataTag);

        dataTag = "\"model:wikidata\"";

        var checkedList2 = await DownloadThumbnailsForOverpassQuery(featureTag, dataTag);

        var neededFiles = MergeCollections(checkedList, checkedList2);

        foreach(var file in Directory.GetFiles("..\\images\\"))
        {
            FileInfo fInfo = new FileInfo(file);

            if (!neededFiles.Contains(Path.GetFileNameWithoutExtension(fInfo.Name)))
            {
                Console.WriteLine($"Deleting {file}");
                File.Delete(file);
            }
        }
    }

    private static async Task<ICollection<string>> DownloadThumbnailsForOverpassQuery(string featureTag, string dataTag)
    {
        string overpassQuery = $"[out:json][timeout:25]; nwr[{featureTag}][{dataTag}]; out tags;";

        string data = SendQuery(overpassQuery);

        var osmItems = JsonConvert.DeserializeObject<OsmItems>(data);

        if (osmItems == null)
        {
            Console.WriteLine("ERROR: bad overpass data return");
            throw new Exception("Overpass failed");
        }

        HashSet<string> checkedItems = new HashSet<string>();

        foreach (var osmItem in osmItems.elements)
        {
            var tagTocheck = osmItem.tags.wikidata;
            if (dataTag == "\"model:wikidata\"")
            {
                tagTocheck = osmItem.tags.modelwikidata;
            }
            if (checkedItems.Contains(tagTocheck))
            {
                continue;
            }
            else
            {
                checkedItems.Add(tagTocheck);

                // TODO, check return and write log file of items needing attention.
                await DownloadImageFromWikidataId(tagTocheck);
            }
        }

        return checkedItems;
    }

    public static string SendQuery(string overpassQuery)
    {
        // URL for the Overpass API endpoint
        string overpassUrl = "https://overpass-api.de/api/interpreter";

        // set up the request
        HttpRequestMessage request = new(HttpMethod.Post, overpassUrl);
        request.Content = new StringContent(overpassQuery);

        // send the query
        HttpResponseMessage response = httpClient.Send(request);

        response.EnsureSuccessStatusCode();

        var contentTask = response.Content.ReadAsStringAsync();
        contentTask.Wait();

        return contentTask.Result;
    }

    private static async Task<bool> DownloadImageFromWikidataId(string wikidataId)
    {
        if (wikidataId == null)
        {
            return false;
        }
        string fileName = $"{wikidataId}.jpg";

        if (File.Exists("..\\images\\" + fileName))
        {
            return true;
        }

        string apiUrl = $"https://www.wikidata.org/wiki/Special:EntityData/{wikidataId}.json";

        try
        {
            HttpResponseMessage response = await httpClient.GetAsync(apiUrl);
            response.EnsureSuccessStatusCode();

            string jsonData = await response.Content.ReadAsStringAsync();

            // Parse JSON and find the image property (P18)
            JObject wikidataJson = JObject.Parse(jsonData);
            string imageName = wikidataJson
                .SelectToken($"$.entities.{wikidataId}.claims.P18[0].mainsnak.datavalue.value")
                ?.ToString();

            if (imageName == null)
            {
                Console.WriteLine($"No image (P18) found for Wikidata ID: {wikidataId}");
                return false;
            }

            if (imageName.EndsWith(".svg"))
            {
                Console.WriteLine($"WARNING: {wikidataId} .svg files type is not supported");
                return false;
            }

            // Construct the URL to download the image
            string imageUrl = $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(imageName)}";

            

            // Download and save the image
            byte[] imageBytes = await httpClient.GetByteArrayAsync(imageUrl);
            // Rescale the image to a maximum width of 100 pixels while maintaining aspect ratio
            using (MemoryStream memoryStream = new MemoryStream(imageBytes))
            {
                using (Image originalImage = Image.FromStream(memoryStream))
                {
                    int newWidth = 100; // Maximum width
                    int newHeight = (int)(originalImage.Height * (newWidth / (double)originalImage.Width)); // Maintain aspect ratio

                    using (Bitmap resizedImage = new Bitmap(originalImage, new Size(newWidth, newHeight)))
                    {
                        resizedImage.Save("..\\images\\" + fileName); // Save resized image to disk
                        Console.WriteLine($"Resized image saved as {fileName}");
                    }
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

    static ICollection<T> MergeCollections<T>(ICollection<T> collection1, ICollection<T> collection2)
    {
        // Create a new list to hold the merged collections
        List<T> mergedList = new List<T>();

        // Add items from the first collection
        if (collection1 != null)
        {
            mergedList.AddRange(collection1);
        }

        // Add items from the second collection
        if (collection2 != null)
        {
            mergedList.AddRange(collection2);
        }

        return mergedList;
    }
}
