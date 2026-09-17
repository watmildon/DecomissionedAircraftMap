using Newtonsoft.Json;

public interface IOsmQueryProvider
{
    OsmItems? ExecuteQuery(HttpClient client);
}

public class OverpassQueryProvider : IOsmQueryProvider
{
    public string Query { get; }

    private const int MaxRetriesPerServer = 3;
    private const int BaseBackoffMs = 2000;

    // Environment variable holding a private Overpass instance URL (self-hosted,
    // or a public one that needs an API key in the URL). When set it is tried
    // before the public servers, which stay on as fallback so an unset secret --
    // in a fork, or in a local run -- still works.
    public const string PrimaryEndpointEnvVar = "OVERPASS_PRIMARY_URL";

    // Logged in place of the private endpoint's URL. That URL is a secret, and a
    // failed nightly run pastes its own output into a public issue, so the URL
    // must never reach the console. Not even the host is logged: these URLs often
    // carry the API key in the path, which makes the host enough to guess.
    private const string PrivateEndpointLabel = "<private Overpass instance>";

    private readonly OsmResponseValidator? validator;

    // Public Overpass servers, tried in order after the private instance.
    private static readonly string[] PublicServers = new[]
    {
        "https://overpass-api.de/api/interpreter",         // German (main)
        "https://overpass.kumi.systems/api/interpreter",   // German (Kumi)
        "https://overpass.private.coffee/api/interpreter", // Austrian
    };

    // The private endpoint, or null when none is configured. Used only to decide
    // what has to be redacted before logging -- never logged itself.
    private static string? s_privateEndpoint;

    private static readonly string[] Servers = BuildServerList();

    public OverpassQueryProvider(
        string query,
        OsmResponseValidator? validator = null)
    {
        Query = query;
        this.validator = validator;
    }

    private static string[] BuildServerList()
    {
        var primary = Environment.GetEnvironmentVariable(PrimaryEndpointEnvVar)?.Trim();

        if (string.IsNullOrEmpty(primary))
        {
            Console.WriteLine($"{PrimaryEndpointEnvVar} not set, using public Overpass servers only");
            return PublicServers;
        }

        if (!Uri.TryCreate(primary, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // Deliberately does not echo the offending value: it is a malformed
            // secret, but it is still a secret.
            Console.Error.WriteLine($"WARNING: {PrimaryEndpointEnvVar} is not a valid http(s) URL, ignoring it and using public Overpass servers only");
            return PublicServers;
        }

        s_privateEndpoint = primary;
        Console.WriteLine($"Using primary Overpass endpoint from {PrimaryEndpointEnvVar}, with public servers as fallback");
        return [primary, .. PublicServers];
    }

    // A log-safe name for a server. Always use this instead of interpolating a
    // server URL into output.
    private static string SafeName(string server) =>
        server == s_privateEndpoint ? PrivateEndpointLabel : server;

    // Strips the private endpoint URL, and its host, out of arbitrary text such as
    // exception messages, which routinely embed the host they failed to reach.
    private static string Redact(string text)
    {
        if (string.IsNullOrEmpty(s_privateEndpoint) || string.IsNullOrEmpty(text))
            return text;

        text = text.Replace(s_privateEndpoint, PrivateEndpointLabel, StringComparison.OrdinalIgnoreCase);

        if (Uri.TryCreate(s_privateEndpoint, UriKind.Absolute, out var uri))
            text = text.Replace(uri.Host, PrivateEndpointLabel, StringComparison.OrdinalIgnoreCase);

        return text;
    }

    public OsmItems? ExecuteQuery(HttpClient client)
    {
        Exception? lastException = null;

        // Hold on to the largest response we see. If every server returns a short
        // one we need something to fall back on, and the largest is the closest
        // thing we have to a complete picture.
        OsmItems? bestResult = null;
        int bestElementCount = -1;

        foreach (var serverUrl in Servers)
        {
            for (int attempt = 1; attempt <= MaxRetriesPerServer; attempt++)
            {
                try
                {
                    Console.WriteLine($"Querying Overpass API at {SafeName(serverUrl)} (attempt {attempt}/{MaxRetriesPerServer})...");

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
                            Console.WriteLine($"Successfully queried {SafeName(serverUrl)}");
                            return result;
                        }

                        // A response that parsed but came back short is treated
                        // like a failed request: back off and try again, then try
                        // another server, rather than trusting it.
                        Console.Error.WriteLine($"Suspect response from {SafeName(serverUrl)}: {problem}");
                        lastException = new InvalidOperationException($"Suspect response from {SafeName(serverUrl)}: {problem}");
                    }
                }
                catch (Exception ex)
                {
                    // The message may name the host we could not reach, so it is
                    // redacted before it is logged or kept for the final error.
                    var message = Redact(ex.Message);
                    lastException = new InvalidOperationException(message);
                    Console.Error.WriteLine($"Attempt {attempt}/{MaxRetriesPerServer} failed for {SafeName(serverUrl)}: {message}");
                }

                if (attempt < MaxRetriesPerServer)
                {
                    int backoffMs = BaseBackoffMs * attempt;
                    Console.WriteLine($"Waiting {backoffMs}ms before retry...");
                    Thread.Sleep(backoffMs);
                }
            }

            Console.WriteLine($"All {MaxRetriesPerServer} attempts failed for {SafeName(serverUrl)}, trying next server...");
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
            Console.Error.WriteLine($"Response preview: {Redact(content.Substring(0, Math.Min(500, content.Length)))}");
            throw new InvalidOperationException("Overpass API returned an error response");
        }

        var result = JsonConvert.DeserializeObject<OsmItems>(content);
        Console.WriteLine($"Parsed {result?.elements?.Length ?? 0} elements from Overpass response");

        return result;
    }
}
