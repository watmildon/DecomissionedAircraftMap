using Newtonsoft.Json;

public class OsmItems
{
    public float version { get; set; }
    public string generator { get; set; } = string.Empty;
    public Element[] elements { get; set; } = [];
}

public class Element
{
    public string type { get; set; } = string.Empty;
    public long id { get; set; }
    public Tags tags { get; set; } = new();
}

public class Tags
{
    public string? wikidata { get; set; }
    public string? wikipedia { get; set; }
    public string? wikimedia_commons { get; set; }

    [JsonProperty("model:wikidata")]
    public string? modelwikidata { get; set; }

    [JsonProperty("subject:wikidata")]
    public string? subjectwikidata { get; set; }
}
