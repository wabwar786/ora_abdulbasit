using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Orapmshms.Services;

// Holds at most one active retry per browser session/hotel/user. Single server process only.
internal static class FbrBrowserBridge
{
    internal sealed class Attempt(string owner)
    {
        public string Owner { get; } = owner;
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddMinutes(3);
        public ConcurrentDictionary<string, Pending> Jobs { get; } = new();
        public string? ProbedUrl { get; set; }
        public int PostDispatched;
    }
    internal sealed record Job(string Id, string Kind, string Url, string? Body, DateTimeOffset Expires);
    internal sealed record Reply(string Id, bool Connected, int Status, string? Body, string? Error);
    internal sealed class Pending(Job job)
    {
        public Job Job { get; } = job;
        public int Claimed;
        public TaskCompletionSource<Reply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static readonly ConcurrentDictionary<string, Attempt> Attempts = new();
    private static readonly object Gate = new();
    public static Attempt? Begin(string id, string owner)
    {
        lock (Gate)
        {
            foreach (var x in Attempts) if (x.Value.Expires <= DateTimeOffset.UtcNow) Attempts.TryRemove(x.Key, out _);
            if (Attempts.Count >= 100 || Attempts.Values.Any(a => a.Owner == owner)) return null;
            var a = new Attempt(owner);
            return Attempts.TryAdd(id, a) ? a : null;
        }
    }
    public static Attempt? Find(string id, string owner) => !string.IsNullOrEmpty(id) && Attempts.TryGetValue(id, out var a)
        && a.Owner == owner && a.Expires > DateTimeOffset.UtcNow ? a : null;
    public static void End(string id, Attempt attempt)
    {
        lock (Gate) { if (Attempts.TryGetValue(id, out var a) && ReferenceEquals(a, attempt)) Attempts.TryRemove(id, out _); }
    }
    public static Job? Claim(Attempt a)
    {
        foreach (var p in a.Jobs.Values)
            if (p.Job.Expires > DateTimeOffset.UtcNow && Interlocked.CompareExchange(ref p.Claimed, 1, 0) == 0) return p.Job;
        return null;
    }
    public static bool Complete(Attempt a, Reply r) => a.Jobs.TryGetValue(r.Id, out var p)
        && Volatile.Read(ref p.Claimed) == 1 && p.Job.Expires > DateTimeOffset.UtcNow && p.Completion.TrySetResult(r);
    private static string LocalUrl(string configured)
    {
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(u.UserInfo))
            throw new InvalidOperationException("Invalid fiscal route.");
        return new UriBuilder(u) { Host = "127.0.0.1", Port = u.Port, Fragment = "" }.Uri.AbsoluteUri;
    }
    private static async Task<Reply> Dispatch(Attempt a, string kind, string url, string? body, CancellationToken ct)
    {
        if (a.Expires <= DateTimeOffset.UtcNow.AddSeconds(60)) throw new InvalidOperationException("Local retry expired.");
        var job = new Job(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), kind, url, body,
            DateTimeOffset.UtcNow.AddSeconds(kind == "probe" ? 20 : 55));
        var p = new Pending(job); a.Jobs[job.Id] = p;
        try { return await p.Completion.Task.WaitAsync(job.Expires - DateTimeOffset.UtcNow, ct); }
        finally { a.Jobs.TryRemove(job.Id, out _); }
    }
    public static async Task<(bool Connected, string Failure)> Probe(Attempt a, string url, CancellationToken ct)
    {
        try
        {
            var local = LocalUrl(url);
            var r = await Dispatch(a, "probe", local, null, ct);
            if (r.Connected) a.ProbedUrl = local;
            return (r.Connected, r.Error ?? "");
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return (false, "Browser-local connection check failed."); }
    }
    public sealed class LocalClientFactory(Attempt a) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(a)) { Timeout = TimeSpan.FromSeconds(65) };
    }
    private sealed class Handler(Attempt a) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri == null || request.Content == null ||
                LocalUrl(request.RequestUri.AbsoluteUri) != a.ProbedUrl || Interlocked.Exchange(ref a.PostDispatched, 1) != 0)
                throw new InvalidOperationException("Unexpected local fiscal request.");
            var body = await request.Content.ReadAsStringAsync(ct);
            if (body.Length > 1000000) throw new InvalidOperationException("Fiscal payload is too large.");
            var r = await Dispatch(a, "post", a.ProbedUrl!, body, ct);
            // Browser responses are untrusted; pilot only until independent fiscal verification exists.
            if (!r.Connected || r.Status < 100 || r.Status > 599) throw new HttpRequestException(r.Error ?? "Uncertain local fiscal response.");
            return new HttpResponseMessage((HttpStatusCode)r.Status) { Content = new StringContent(r.Body ?? "", Encoding.UTF8, "application/json") };
        }
    }
}
