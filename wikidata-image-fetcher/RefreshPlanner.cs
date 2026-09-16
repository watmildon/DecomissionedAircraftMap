// Decides what a run should fetch, given what Wikidata currently reports and what
// we already hold locally. Kept out of Main so both the nightly path and the
// manual force refresh can be exercised directly rather than only by running the
// whole program against live Overpass.
//
// Building a plan also updates the cache: adopting an existing image and
// recording "no image" outcomes are decisions, not side effects of fetching.
public class RefreshPlanner
{
    public enum FetchReason
    {
        // No local image at all.
        New,

        // P18 points somewhere else than the file we downloaded.
        SourceChanged,

        // Same filename, but old enough that the bytes are worth checking again.
        Revalidate,

        // A manual full refresh, which re-fetches regardless.
        Forced,
    }

    public record Item(string Id, string Source, bool Replacing, FetchReason Reason);

    public class Plan
    {
        public List<Item> ToFetch { get; } = new();

        // Local images whose wikidata item no longer offers an image at all.
        public List<string> Orphaned { get; } = new();

        public int Unchanged { get; set; }
        public int Adopted { get; set; }
        public int Deferred { get; set; }
        public int Unsupported { get; set; }
        public int Unresolved { get; set; }
        public int NoImage { get; set; }

        // What the safety cap measures. Revalidations and forced refreshes are
        // left out deliberately: they are expected work, not a sign that Wikidata
        // told us something strange, so they must not trip a cap that exists to
        // catch exactly that.
        public int SourceChanged => ToFetch.Count(i => i.Reason == FetchReason.SourceChanged);

        // How many fetches overwrite a file we already hold, whatever the reason.
        // This is for reporting, not for the cap.
        public int Overwriting => ToFetch.Count(i => i.Replacing);

        public int Revalidations => ToFetch.Count(i => i.Reason == FetchReason.Revalidate);

        public int RevalidationsDeferred { get; set; }

        public int NewDownloads => ToFetch.Count(i => !i.Replacing);
    }

    public static Plan Build(
        IEnumerable<string> ids,
        IReadOnlyDictionary<string, WikidataImageLookup.Result> lookup,
        Func<string, bool> hasLocalImage,
        ImageCache cache,
        DateTime now,
        bool forceRefresh,
        int maxRevalidations = int.MaxValue)
    {
        var plan = new Plan();
        var due = new List<(string Id, string Source, DateTime? LastChecked)>();

        foreach (var id in ids)
        {
            var result = lookup.TryGetValue(id, out var found)
                ? found
                : new WikidataImageLookup.Result(WikidataImageLookup.Status.Unresolved, null);

            bool haveLocal = hasLocalImage(id);

            if (result.Status == WikidataImageLookup.Status.Unresolved)
            {
                // No trustworthy answer, so leave whatever we already have alone.
                // Deleted items and anything the API would not resolve land here.
                plan.Unresolved++;
                continue;
            }

            if (result.Status == WikidataImageLookup.Status.NoImage)
            {
                if (haveLocal)
                {
                    plan.Orphaned.Add(id);
                }

                plan.NoImage++;
                cache.RecordFailure(id, null, "no-p18", now);
                continue;
            }

            string source = result.FileName!;

            if (source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                // Schematics rather than photographs; these want cleanup on the
                // wikidata side rather than rendering here.
                plan.Unsupported++;
                cache.RecordFailure(id, source, "unsupported-format", now);
                continue;
            }

            if (forceRefresh)
            {
                // A manual refresh re-fetches the bytes whatever we think we know.
                // It is the only way to notice a Commons file that was replaced
                // under the same name, which no filename comparison can catch.
                plan.ToFetch.Add(new Item(id, source, haveLocal, FetchReason.Forced));
                continue;
            }

            if (haveLocal)
            {
                string? known = cache.DownloadedSourceFor(id);

                if (known == null)
                {
                    // Downloaded before the run started recording sources. Adopt
                    // the current P18 rather than re-fetching every existing
                    // thumbnail; the manual refresh is how you force that.
                    cache.RecordDownloaded(id, source, now);
                    plan.Adopted++;
                    continue;
                }

                if (string.Equals(known, source, StringComparison.Ordinal))
                {
                    if (cache.IsDueForRevalidation(id, now))
                    {
                        due.Add((id, source, cache.LastCheckedFor(id)));
                    }
                    else
                    {
                        plan.Unchanged++;
                    }

                    continue;
                }

                plan.ToFetch.Add(new Item(id, source, true, FetchReason.SourceChanged));
                continue;
            }

            if (cache.ShouldSkipFetch(id, source, now))
            {
                plan.Deferred++;
                continue;
            }

            plan.ToFetch.Add(new Item(id, source, false, FetchReason.New));
        }

        // Oldest first, and capped, so that a wiped or unreadable cache trickles
        // through over many nights instead of re-downloading everything at once.
        foreach (var item in due.OrderBy(d => d.LastChecked ?? DateTime.MinValue).Take(maxRevalidations))
        {
            plan.ToFetch.Add(new Item(item.Id, item.Source, true, FetchReason.Revalidate));
        }

        plan.RevalidationsDeferred = Math.Max(0, due.Count - plan.Revalidations);

        return plan;
    }
}
