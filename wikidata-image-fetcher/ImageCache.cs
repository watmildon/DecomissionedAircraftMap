using System.Globalization;
using Newtonsoft.Json;

// Records what the nightly run knows about each wikidata item's image: the
// Commons file a local thumbnail was built from, or the reason there is no
// thumbnail. Storing the source filename is what lets the batched sweep notice
// that P18 changed without re-downloading anything to compare against.
public class ImageCache
{
    // Fetch failures are retried on a delay so a Commons file we cannot use does
    // not cost a request every night. Items with no P18 at all are deliberately
    // not on this clock: the batched sweep re-checks those for free.
    private const int BaseRetryDays = 7;
    private const int MaxJitterDays = 7;
    private const string DateFormat = "yyyy-MM-dd";

    private readonly string path;
    private readonly SortedDictionary<string, Entry> entries;

    public class Entry
    {
        // The Commons filename this entry describes, where there is one.
        public string? Source { get; set; }

        // Null when the thumbnail downloaded successfully.
        public string? Reason { get; set; }

        // Stored as a plain date so the committed file produces small, stable
        // diffs instead of churning on every run.
        public string LastChecked { get; set; } = string.Empty;
    }

    private ImageCache(string path, SortedDictionary<string, Entry> entries)
    {
        this.path = path;
        this.entries = entries;
    }

    public int Count => entries.Count;

    public int DownloadedCount => entries.Values.Count(e => e.Reason == null);

    public static ImageCache Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ImageCache(path, new SortedDictionary<string, Entry>(StringComparer.Ordinal));
        }

        try
        {
            var loaded = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(path));
            return new ImageCache(
                path,
                new SortedDictionary<string, Entry>(loaded ?? new Dictionary<string, Entry>(), StringComparer.Ordinal));
        }
        catch (Exception ex)
        {
            // A corrupt cache is not worth failing the run over; the worst case is
            // one slow night while it is rebuilt.
            Console.WriteLine($"Warning: could not read image cache, starting empty: {ex.Message}");
            return new ImageCache(path, new SortedDictionary<string, Entry>(StringComparer.Ordinal));
        }
    }

    // The Commons file the local thumbnail for this item was built from, or null
    // if we have no record of a successful download.
    public string? DownloadedSourceFor(string id)
        => entries.TryGetValue(id, out var entry) && entry.Reason == null ? entry.Source : null;

    public bool ShouldSkipFetch(string id, string source, DateTime now)
    {
        if (!entries.TryGetValue(id, out var entry) || entry.Reason == null)
        {
            return false;
        }

        // A different P18 than the one that failed deserves an immediate attempt.
        if (!string.Equals(entry.Source, source, StringComparison.Ordinal))
        {
            return false;
        }

        if (!DateTime.TryParseExact(entry.LastChecked, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastChecked))
        {
            return false;
        }

        return now < lastChecked.AddDays(RetryDaysFor(id));
    }

    public void RecordDownloaded(string id, string source, DateTime now)
    {
        entries[id] = new Entry
        {
            Source = source,
            Reason = null,
            LastChecked = now.ToString(DateFormat, CultureInfo.InvariantCulture),
        };
    }

    public void RecordFailure(string id, string? source, string reason, DateTime now)
    {
        entries[id] = new Entry
        {
            Source = source,
            Reason = reason,
            LastChecked = now.ToString(DateFormat, CultureInfo.InvariantCulture),
        };
    }

    public void Forget(string id) => entries.Remove(id);

    // Drops entries for items the query no longer asks about, so the file does not
    // accumulate ids that have left OSM.
    public int PruneTo(IEnumerable<string> liveIds)
    {
        var live = new HashSet<string>(liveIds, StringComparer.Ordinal);
        var stale = entries.Keys.Where(id => !live.Contains(id)).ToList();

        foreach (var id in stale)
        {
            entries.Remove(id);
        }

        return stale.Count;
    }

    public void Save()
    {
        File.WriteAllText(path, JsonConvert.SerializeObject(entries, Formatting.Indented));
    }

    private static int RetryDaysFor(string id) => BaseRetryDays + (StableHash(id) % (MaxJitterDays + 1));

    // string.GetHashCode is randomised per process, so hash the id here to keep
    // each item's retry date stable from one run to the next, while still spreading
    // retries across nights rather than bunching them onto one.
    private static int StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;

            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }
}
