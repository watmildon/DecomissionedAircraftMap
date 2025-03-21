
public class OsmItems
{
    public float version { get; set; }
    public string generator { get; set; }
    public Element[] elements { get; set; }
}

public class Element
{
    public string type { get; set; }
    public long id { get; set; }
    public Tags tags { get; set; }
}

public class Tags
{
    public string wikidata { get; set; }
    public string wikipedia { get; set; }
    public string wikimedia_commons { get; set; }
    public string aircraftmodelwikidata { get; set; }
}
