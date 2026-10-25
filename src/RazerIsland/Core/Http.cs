using System.Net.Http;

namespace RazerIsland.Core;

/// <summary>Спільний HttpClient застосунку.</summary>
public static class Http
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"RazerIsland/{AppInfo.Version}");
        return c;
    }
}
