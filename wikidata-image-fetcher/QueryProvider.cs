using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public enum QueryBackend
{
    Overpass,
    Postpass,
    QLever
}

public interface IQueryProvider
{
    QueryBackend Backend { get; }
    string EndpointUrl { get; }
    OsmItems? ExecuteQuery(HttpClient client);
}

public class OverpassQueryProvider : IQueryProvider
{
    public QueryBackend Backend => QueryBackend.Overpass;
    public string EndpointUrl { get; }
    public string Query { get; }

    public OverpassQueryProvider(string query, string endpointUrl = "https://overpass-api.de/api/interpreter")
    {
        Query = query;
        EndpointUrl = endpointUrl;
    }

    public OsmItems? ExecuteQuery(HttpClient client)
    {
        Console.WriteLine($"Querying Overpass API at {EndpointUrl}...");

        var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        request.Content = new StringContent(Query);

        var response = client.Send(request);
        response.EnsureSuccessStatusCode();

        var contentTask = response.Content.ReadAsStringAsync();
        contentTask.Wait();

        return JsonConvert.DeserializeObject<OsmItems>(contentTask.Result);
    }
}

public class PostpassQueryProvider : IQueryProvider
{
    public QueryBackend Backend => QueryBackend.Postpass;
    public string EndpointUrl { get; }
    public string Query { get; }

    public PostpassQueryProvider(string query, string endpointUrl = "https://postpass.trailsta.sh/sql")
    {
        Query = query;
        EndpointUrl = endpointUrl;
    }

    public OsmItems? ExecuteQuery(HttpClient client)
    {
        Console.WriteLine($"Querying Postpass at {EndpointUrl}...");

        var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        request.Content = new StringContent(Query);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        var response = client.Send(request);
        response.EnsureSuccessStatusCode();

        var contentTask = response.Content.ReadAsStringAsync();
        contentTask.Wait();

        // Postpass returns rows with osm_id and tags columns
        // Convert to OsmItems format
        return ConvertPostpassResponse(contentTask.Result);
    }

    private OsmItems? ConvertPostpassResponse(string jsonResponse)
    {
        var rows = JsonConvert.DeserializeObject<List<PostpassRow>>(jsonResponse);
        if (rows == null) return null;

        var elements = new List<Element>();
        foreach (var row in rows)
        {
            var element = new Element
            {
                id = row.osm_id,
                tags = ParseTags(row.tags)
            };
            elements.Add(element);
        }

        return new OsmItems { elements = elements.ToArray() };
    }

    private Tags ParseTags(Dictionary<string, string>? tagsDict)
    {
        var tags = new Tags();
        if (tagsDict == null) return tags;

        if (tagsDict.TryGetValue("wikidata", out var wikidata))
            tags.wikidata = wikidata;
        if (tagsDict.TryGetValue("model:wikidata", out var modelWikidata))
            tags.modelwikidata = modelWikidata;
        if (tagsDict.TryGetValue("subject:wikidata", out var subjectWikidata))
            tags.subjectwikidata = subjectWikidata;

        return tags;
    }

    private class PostpassRow
    {
        public long osm_id { get; set; }
        public Dictionary<string, string>? tags { get; set; }
    }
}

public class QLeverQueryProvider : IQueryProvider
{
    public QueryBackend Backend => QueryBackend.QLever;
    public string EndpointUrl { get; }
    public string Query { get; }

    public QLeverQueryProvider(string query, string endpointUrl = "https://qlever.cs.uni-freiburg.de/api/osm-planet")
    {
        Query = query;
        EndpointUrl = endpointUrl;
    }

    public OsmItems? ExecuteQuery(HttpClient client)
    {
        Console.WriteLine($"Querying QLever at {EndpointUrl}...");

        var encodedQuery = Uri.EscapeDataString(Query);
        var url = $"{EndpointUrl}?query={encodedQuery}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/sparql-results+json");

        var response = client.Send(request);
        response.EnsureSuccessStatusCode();

        var contentTask = response.Content.ReadAsStringAsync();
        contentTask.Wait();

        return ConvertQLeverResponse(contentTask.Result);
    }

    private OsmItems? ConvertQLeverResponse(string jsonResponse)
    {
        var sparqlResult = JObject.Parse(jsonResponse);
        var bindings = sparqlResult["results"]?["bindings"] as JArray;

        if (bindings == null) return null;

        var elements = new List<Element>();
        long idCounter = 1;

        foreach (var binding in bindings)
        {
            var tags = new Tags();

            // Extract wikidata IDs from SPARQL bindings
            var wikidataValue = binding["wikidata"]?["value"]?.ToString();
            if (!string.IsNullOrEmpty(wikidataValue))
            {
                // Extract Q-number from URI like http://www.wikidata.org/entity/Q12345
                tags.wikidata = ExtractQNumber(wikidataValue);
            }

            var modelWikidataValue = binding["model_wikidata"]?["value"]?.ToString();
            if (!string.IsNullOrEmpty(modelWikidataValue))
            {
                tags.modelwikidata = ExtractQNumber(modelWikidataValue);
            }

            var subjectWikidataValue = binding["subject_wikidata"]?["value"]?.ToString();
            if (!string.IsNullOrEmpty(subjectWikidataValue))
            {
                tags.subjectwikidata = ExtractQNumber(subjectWikidataValue);
            }

            var element = new Element
            {
                id = idCounter++,
                tags = tags
            };
            elements.Add(element);
        }

        return new OsmItems { elements = elements.ToArray() };
    }

    private string? ExtractQNumber(string value)
    {
        // Handle both full URIs and plain Q-numbers
        if (value.StartsWith("Q"))
            return value;

        var lastSlash = value.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < value.Length - 1)
            return value.Substring(lastSlash + 1);

        return value;
    }
}
