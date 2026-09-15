using System.Globalization;
using Newtonsoft.Json;

// Remembers which wikidata items turned out to have no usable image, so the
// nightly run can stop re-asking about the same few hundred items every night.
// Entries expire so that an image added later is still picked up eventually, and
// the expiry is jittered per item so re-checks trickle through a few at a time
// rather than the whole backlog landing on one night.
public class NegativeCache
{
    private const int BaseTtlDays = 7;
    private const int MaxJitterDays = 7;
    private const string DateFormat = "yyyy-MM-dd";

    private readonly string path;
    private readonly SortedDictionary<string, Entry> entries;

    public class Entry
    {
        public string Reason { get; set; } = string.Empty;

        // Stored as a plain date so the committed file produces small, stable
        // diffs instead of churning on every run.
        public string LastChecked { get; set; } = string.Empty;
    }

    private NegativeCache(string path, SortedDictionary<string, Entry> entries)
    {
        this.path = path;
        this.entries = entries;
    }

    public int Count => entries.Count;

    public static NegativeCache Load(string path)
    {
        if (!File.Exists(path))
        {
            return new NegativeCache(path, new SortedDictionary<string, Entry>(StringComparer.Ordinal));
        }

        try
        {
            var loaded = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(path));
            return new NegativeCache(
                path,
                new SortedDictionary<string, Entry>(loaded ?? new Dictionary<string, Entry>(), StringComparer.Ordinal));
        }
        catch (Exception ex)
        {
            // A corrupt cache is not worth failing the run over; the worst case
            // is one slow night while it is rebuilt.
            Console.WriteLine($"Warning: could not read image cache, starting empty: {ex.Message}");
            return new NegativeCache(path, new SortedDictionary<string, Entry>(StringComparer.Ordinal));
        }
    }

    public bool ShouldSkip(string id, DateTime now)
    {
        if (!entries.TryGetValue(id, out var entry))
            return false;

        if (!DateTime.TryParseExact(entry.LastChecked, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastChecked))
            return false;

        return now < lastChecked.AddDays(TtlDaysFor(id));
    }

    public void RecordNegative(string id, string reason, DateTime now)
    {
        entries[id] = new Entry
        {
            Reason = reason,
            LastChecked = now.ToString(DateFormat, CultureInfo.InvariantCulture),
        };
    }

    public void Forget(string id) => entries.Remove(id);

    // Drops entries for items the query no longer asks about, so the file does
    // not accumulate ids that have left OSM.
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

    private static int TtlDaysFor(string id) => BaseTtlDays + (StableHash(id) % (MaxJitterDays + 1));

    // string.GetHashCode is randomised per process, so hash the id here to keep
    // each item's re-check date stable from one run to the next.
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
