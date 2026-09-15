// Checks an Overpass response against a baseline taken from the last committed
// run. Partial responses are common enough that a short result is not by itself
// evidence of real change: the strict check decides when a response is suspect
// enough to retry against another server, and the lenient check decides whether
// a short result that every server agrees on should be accepted as real churn.
public class OsmResponseValidator
{
    // A response that loses more than this fraction of the baseline is retried.
    private const double RetryShrinkTolerance = 0.01;

    // A response that loses more than this fraction is rejected outright, even
    // when every server returned it.
    private const double AcceptShrinkTolerance = 0.05;

    // Backstop for when there is no baseline to compare against, such as a first
    // run or a missing GeoJSON file.
    private const int AbsoluteMinimumElements = 1500;

    public int BaselineElementCount { get; }
    public int BaselineIdCount { get; }

    public OsmResponseValidator(int baselineElementCount, int baselineIdCount)
    {
        BaselineElementCount = baselineElementCount;
        BaselineIdCount = baselineIdCount;
    }

    // Both return null when the response is acceptable, or a description of the
    // problem when it is not.
    public string? ValidateForRetry(OsmItems items) => Validate(items, RetryShrinkTolerance);

    public string? ValidateForAccept(OsmItems items) => Validate(items, AcceptShrinkTolerance);

    private string? Validate(OsmItems items, double shrinkTolerance)
    {
        int elementCount = items.elements?.Length ?? 0;

        if (elementCount < AbsoluteMinimumElements)
        {
            return $"only {elementCount} elements returned, below the absolute minimum of {AbsoluteMinimumElements}";
        }

        if (BaselineElementCount > 0)
        {
            int floor = (int)(BaselineElementCount * (1 - shrinkTolerance));
            if (elementCount < floor)
            {
                return $"{elementCount} elements returned, below the {floor} expected from a baseline of {BaselineElementCount}";
            }
        }

        // Element count alone misses the case that actually bites: a response of
        // roughly the right size that is missing the wikidata tags the image
        // deletion pass keys off.
        if (BaselineIdCount > 0)
        {
            int idCount = CountDistinctWikidataIds(items);
            int floor = (int)(BaselineIdCount * (1 - shrinkTolerance));
            if (idCount < floor)
            {
                return $"{idCount} distinct wikidata ids returned, below the {floor} expected from a baseline of {BaselineIdCount}";
            }
        }

        return null;
    }

    public static int CountDistinctWikidataIds(OsmItems items)
    {
        var ids = new HashSet<string>();

        foreach (var element in items.elements ?? [])
        {
            foreach (var id in new[] { element.tags.wikidata, element.tags.modelwikidata, element.tags.subjectwikidata })
            {
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }

        return ids.Count;
    }
}
