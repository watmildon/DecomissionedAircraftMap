using Newtonsoft.Json;

public interface IOsmQueryProvider
{
    string EndpointUrl { get; }
    OsmItems? ExecuteQuery(HttpClient client);
}

public class OverpassQueryProvider : IOsmQueryProvider
{
    public string EndpointUrl { get; }
    public string Query { get; }

    private const int MaxRetriesPerServer = 3;
    private const int BaseBackoffMs = 2000;

    private readonly OsmResponseValidator? validator;

    // Fallback Overpass servers to try if primary fails
    private static readonly string[] FallbackServers = new[]
    {
        "https://overpass-api.de/api/interpreter",           // German (main)
        "https://overpass.kumi.systems/api/interpreter",     // German (Kumi)
        "https://overpass.openstreetmap.ru/api/interpreter", // Russian
    };

    public OverpassQueryProvider(
        string query,
        string endpointUrl = "https://overpass-api.de/api/interpreter",
        OsmResponseValidator? validator = null)
    {
        Query = query;
        EndpointUrl = endpointUrl;
        this.validator = validator;
    }

    public OsmItems? ExecuteQuery(HttpClient client)
    {
        // Build list of servers to try: primary first, then fallbacks (excluding primary if it's in the list)
        var serversToTry = new List<string> { EndpointUrl };
        foreach (var server in FallbackServers)
        {
            if (!serversToTry.Contains(server))
                serversToTry.Add(server);
        }

        Exception? lastException = null;

        // Hold on to the largest response we see. If every server returns a short
        // one we need something to fall back on, and the largest is the closest
        // thing we have to a complete picture.
        OsmItems? bestResult = null;
        int bestElementCount = -1;

        foreach (var serverUrl in serversToTry)
        {
            for (int attempt = 1; attempt <= MaxRetriesPerServer; attempt++)
            {
                try
                {
                    Console.WriteLine($"Querying Overpass API at {serverUrl} (attempt {attempt}/{MaxRetriesPerServer})...");

                    var result = TryExecuteQuery(client, serverUrl);

                    if (result != null)
                    {
                        int elementCount = result.elements?.Length ?? 0;
                        if (elementCount > bestElementCount)
                        {
                            bestElementCount = elementCount;
                            bestResult = result;
                        }

                        var problem = validator?.ValidateForRetry(result);

                        if (problem == null)
                        {
                            Console.WriteLine($"Successfully queried {serverUrl}");
                            return result;
                        }

                        // A response that parsed but came back short is treated
                        // like a failed request: back off and try again, then try
                        // another server, rather than trusting it.
                        Console.Error.WriteLine($"Suspect response from {serverUrl}: {problem}");
                        lastException = new InvalidOperationException($"Suspect response from {serverUrl}: {problem}");
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Console.Error.WriteLine($"Attempt {attempt}/{MaxRetriesPerServer} failed for {serverUrl}: {ex.Message}");
                }

                if (attempt < MaxRetriesPerServer)
                {
                    int backoffMs = BaseBackoffMs * attempt;
                    Console.WriteLine($"Waiting {backoffMs}ms before retry...");
                    Thread.Sleep(backoffMs);
                }
            }

            Console.WriteLine($"All {MaxRetriesPerServer} attempts failed for {serverUrl}, trying next server...");
        }

        // Every server either errored or returned a short response. If several
        // independent servers agree on a smaller dataset, that is more likely to
        // be real churn than a coincidence of partial responses, so accept it as
        // long as it is within the wider tolerance.
        if (bestResult != null && validator != null)
        {
            var problem = validator.ValidateForAccept(bestResult);

            if (problem == null)
            {
                Console.WriteLine($"Accepting the best response seen ({bestElementCount} elements) after exhausting retries; the shrink is within tolerance.");
                return bestResult;
            }

            throw new InvalidOperationException(
                $"All Overpass servers returned unusable responses. Best response: {problem}",
                lastException);
        }

        // All servers failed
        throw new InvalidOperationException(
            $"All Overpass servers failed after {MaxRetriesPerServer} attempts each. Last error: {lastException?.Message}",
            lastException);
    }

    private OsmItems? TryExecuteQuery(HttpClient client, string serverUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, serverUrl);
        request.Content = new StringContent(Query);

        var response = client.Send(request);
        response.EnsureSuccessStatusCode();

        var contentTask = response.Content.ReadAsStringAsync();
        contentTask.Wait();
        var content = contentTask.Result;

        // Check if we got an error response (HTML instead of JSON)
        if (content.TrimStart().StartsWith("<"))
        {
            Console.Error.WriteLine("ERROR: Overpass API returned an error response (HTML/XML instead of JSON)");
            Console.Error.WriteLine("The server may be overloaded or the query may have timed out.");
            Console.Error.WriteLine($"Response preview: {content.Substring(0, Math.Min(500, content.Length))}");
            throw new InvalidOperationException("Overpass API returned an error response");
        }

        var result = JsonConvert.DeserializeObject<OsmItems>(content);
        Console.WriteLine($"Parsed {result?.elements?.Length ?? 0} elements from Overpass response");

        return result;
    }
}
