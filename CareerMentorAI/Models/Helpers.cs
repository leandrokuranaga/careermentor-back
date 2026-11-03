
public static class TokenEstimator
{
    public static int EstimateTokens(string s) => Math.Max(1, s.Length / 4);

    public static IEnumerable<string> SlidingChunks(string text, int approxTokenLimit, int approxOverlapTokens)
    {
        int maxChars = approxTokenLimit * 4;
        int overlapChars = approxOverlapTokens * 4;
        if (maxChars <= 0) maxChars = 20000;
        if (overlapChars < 0) overlapChars = 0;

        int i = 0;
        while (i < text.Length)
        {
            int len = Math.Min(maxChars, text.Length - i);
            yield return text.Substring(i, len);
            if (i + len >= text.Length) break;
            i += Math.Max(1, len - overlapChars);
        }
    }
}

sealed class TokenRateLimiter
{
    private readonly int _tpmLimit;
    private readonly object _lock = new();
    private readonly Queue<(DateTime ts, int tokens)> _window = new();
    private int _windowSum = 0;

    public TokenRateLimiter(int tpmLimit) => _tpmLimit = Math.Max(1000, tpmLimit);

    public async Task AwaitCapacityAsync(int costTokens, CancellationToken ct = default)
    {
        while (true)
        {
            var delay = TimeSpan.Zero;

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                while (_window.Count > 0 && (now - _window.Peek().ts).TotalSeconds >= 60)
                {
                    _windowSum -= _window.Dequeue().tokens;
                }

                if (_windowSum + costTokens <= _tpmLimit)
                {
                    _window.Enqueue((now, costTokens));
                    _windowSum += costTokens;
                    return;
                }

                if (_window.Count > 0)
                {
                    var oldest = _window.Peek().ts;
                    var elapsed = now - oldest;
                    var waitMs = Math.Max(50, (int)(60000 - elapsed.TotalMilliseconds));
                    delay = TimeSpan.FromMilliseconds(waitMs);
                }
                else
                {
                    delay = TimeSpan.FromMilliseconds(200);
                }
            }

            await Task.Delay(delay, ct);
        }
    }
}

static class JsonUtils
{
    public static string SafeTrimJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "{}";
        var json = content.Trim();
        if (json.StartsWith("```"))
        {
            var i = json.IndexOf('{'); var j = json.LastIndexOf('}');
            json = (i >= 0 && j >= i) ? json.Substring(i, j - i + 1) : "{}";
        }
        return json;
    }
}