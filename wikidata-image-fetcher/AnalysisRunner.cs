using System.Dynamic;
using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json;

public class AnalysisRunner
{
    private readonly HttpClient client;
    private IEnumerable<string> itemsNeedingDownload = [];
    private IEnumerable<string> filesToDelete = [];

    public AnalysisRunner(string overpassQuery, string[] imageTagsPreference, string imageDirectory, HttpClient client)
    {
        ArgumentException.ThrowIfNullOrEmpty(overpassQuery);
        ArgumentException.ThrowIfNullOrEmpty(imageDirectory);
        OverpassQuery = overpassQuery;
        ImageTagsPreference = imageTagsPreference;
        ImageDirectory = imageDirectory;
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public IEnumerable<string> ItemsNeedingDownload => itemsNeedingDownload;
    public string OverpassQuery { get; }
    public string[] ImageTagsPreference { get; }
    public string ImageDirectory { get; }

    public IEnumerable<string> FilesToDelete => filesToDelete;

    private OsmItems? osmObjects = null;
    public void RunAnalysis()
    {
        if (osmObjects == null)
        {
            osmObjects = JsonConvert.DeserializeObject<OsmItems>(SendOverpassQuery(OverpassQuery));
        }

        // find download targets based on preference order
        var neededFiles = FindNeededFiles(osmObjects);

        // get info about existing files
        var files = Directory.GetFiles(ImageDirectory);

        // find files needing download
        var _needToDl = new List<string>();

        foreach (var neededFile in neededFiles)
        {
            if (!files.Contains(ImageDirectory + neededFile + ".jpg"))
            {
                _needToDl.Add(neededFile);
            }
        }

        itemsNeedingDownload = _needToDl;

        // find files needing to be removed
        var _needToDelete = new List<string>();

        foreach (var file in files)
        {
            FileInfo fInfo = new FileInfo(file);

            if (!neededFiles.Contains(Path.GetFileNameWithoutExtension(fInfo.Name)))
            {
               _needToDelete.Add(file);
            }
        }

        filesToDelete = _needToDelete;
    }

    private IEnumerable<string> FindNeededFiles(OsmItems? osmObjects)
    {
        var needed = new HashSet<string>();

        if (osmObjects == null)
            return needed;

        foreach(var obj in osmObjects.elements)
        {
            foreach(var tag in ImageTagsPreference)
            {
                // todo, switch this to a more dynamic lookup. this is annoying
                if (tag == "wikidata")
                {
                    if (obj.tags.wikidata != null)
                    {
                        if (!needed.Contains(obj.tags.wikidata))
                            needed.Add(obj.tags.wikidata);
                    }
                }
                else if (tag == "model:wikidata")
                {
                    if (obj.tags.modelwikidata != null)
                    {
                        if (!needed.Contains(obj.tags.modelwikidata))
                            needed.Add(obj.tags.modelwikidata);
                    }
                }
            }
        }

        return needed;
    }

    private string SendOverpassQuery(string overpassQuery)
    {
        // URL for the Overpass API endpoint
        string overpassUrl = "https://overpass.private.coffee/api/interpreter";

        // set up the request
        HttpRequestMessage request = new(HttpMethod.Post, overpassUrl);
        request.Content = new StringContent(overpassQuery);

        // send the query
        HttpResponseMessage response = client.Send(request);

        response.EnsureSuccessStatusCode();

        var contentTask = response.Content.ReadAsStringAsync();
        contentTask.Wait();

        return contentTask.Result;
    }

    private ICollection<T> MergeCollections<T>(ICollection<T> collection1, ICollection<T> collection2)
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