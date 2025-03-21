﻿using System;
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

        await DownloadThumbnailsForOverpassQuery(featureTag, dataTag);

        dataTag = "\"model:wikidata\"";

        await DownloadThumbnailsForOverpassQuery(featureTag, dataTag);
    }

    private static async Task DownloadThumbnailsForOverpassQuery(string featureTag, string dataTag)
    {
        string overpassQuery = $"[out:json][timeout:25]; nwr[{featureTag}][{dataTag}]; out tags;";

        string data = SendQuery(overpassQuery);

        var osmItems = JsonConvert.DeserializeObject<OsmItems>(data);

        if (osmItems == null)
        {
            Console.WriteLine("ERROR: bad overpass data return");
            return;
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
                checkedItems.Add(osmItem.tags.wikidata);
                await DownloadImageFromWikidataId(osmItem.tags.wikidata);
            }
        }
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

    private static async Task DownloadImageFromWikidataId(string wikidataId)
    {
        if (wikidataId == null)
        {
            return;
        }
        string fileName = $"{wikidataId}.jpg";

        if (File.Exists("..\\images\\" + fileName))
        {
            return;
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
                return;
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
        }
    }
}
