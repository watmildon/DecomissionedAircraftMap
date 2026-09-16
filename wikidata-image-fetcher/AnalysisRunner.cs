
public class AnalysisRunner
{
    private readonly HttpClient client;
    private readonly IOsmQueryProvider queryProvider;
    private IEnumerable<string> itemsNeedingDownload = [];
    private IEnumerable<string> neededIds = [];
    private IEnumerable<string> filesToDelete = [];

    public AnalysisRunner(IOsmQueryProvider queryProvider, string[] imageTagsPreference, string imageDirectory, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(queryProvider);
        ArgumentException.ThrowIfNullOrEmpty(imageDirectory);
        this.queryProvider = queryProvider;
        ImageTagsPreference = imageTagsPreference;
        ImageDirectory = imageDirectory;
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public IEnumerable<string> ItemsNeedingDownload => itemsNeedingDownload;

    // Every wikidata id referenced by the OSM data, whether or not we hold an
    // image for it.
    public IEnumerable<string> NeededIds => neededIds;
    public string[] ImageTagsPreference { get; }
    public string ImageDirectory { get; }

    public IEnumerable<string> FilesToDelete => filesToDelete;

    public OsmItems? OsmData => osmObjects;

    private OsmItems? osmObjects = null;
    public void RunAnalysis()
    {
        if (osmObjects == null)
        {
            osmObjects = queryProvider.ExecuteQuery(client);
        }

        // find download targets based on preference order
        var neededFiles = FindNeededFiles(osmObjects);
        neededIds = neededFiles;

        // ensure the image directory exists
        Directory.CreateDirectory(ImageDirectory);

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
                else if (tag == "subject:wikidata")
                {
                    if (obj.tags.subjectwikidata != null)
                    {
                        if (!needed.Contains(obj.tags.subjectwikidata))
                            needed.Add(obj.tags.subjectwikidata);
                    }
                }
            }
        }

        return needed;
    }

}