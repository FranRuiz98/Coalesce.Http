namespace Coalesce.Http.Coalesce.Http.Extensions;

internal static class HttpResponseMessageExtensions
{
    public static async Task<HttpResponseMessage> CloneAsync(this HttpResponseMessage original)
    {
        HttpResponseMessage clone = new(original.StatusCode)
        {
            Version = original.Version,
            ReasonPhrase = original.ReasonPhrase,
            RequestMessage = original.RequestMessage,
        };

        // Copy headers
        foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
        {
            _ = clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content if it exists
        if (original.Content != null)
        {
            // Copy content headers BEFORE reading the content to avoid issues with concurrent access
            var contentHeaders = new List<KeyValuePair<string, IEnumerable<string>>>();
            foreach (KeyValuePair<string, IEnumerable<string>> header in original.Content.Headers)
            {
                contentHeaders.Add(header);
            }

            // Now read the content
            ByteArrayContent contentClone = new(await original.Content.ReadAsByteArrayAsync()
                                                                      .ConfigureAwait(false));

            // Apply the headers we captured earlier
            foreach (var header in contentHeaders)
            {
                _ = contentClone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = contentClone;
        }

        return clone;
    }
}
