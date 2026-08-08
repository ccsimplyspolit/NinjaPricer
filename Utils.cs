using System.Net.Http;
using System.Threading.Tasks;

namespace NinjaPricer;

public static class Utils
{
    public static async Task<string> DownloadFromUrl(string url)
    {
        using var handler = new HttpClientHandler { UseCookies = false };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36");
        return await client.GetStringAsync(url).ConfigureAwait(false);
    }
}