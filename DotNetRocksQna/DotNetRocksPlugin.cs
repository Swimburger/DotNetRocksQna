using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using System.Xml;
using Microsoft.SemanticKernel;

namespace DotNetRocksQna;

public class DotNetRocksPlugin
{
    private const string RssFeedUrl = "https://pwop.com/feed.aspx?show=dotnetrocks";

    public const string GetShowsFunctionName = nameof(GetShows);

    [KernelFunction, Description("Get shows of the .NET Rocks! podcast.")]
    public async Task<string> GetShows()
    {
        using var httpClient = new HttpClient();
        var rssFeedStream = await httpClient.GetStreamAsync(RssFeedUrl);

        var doc = new XmlDocument();
        doc.Load(rssFeedStream);

        var nodes = doc.DocumentElement.SelectNodes("//item");
        var shows = new List<Show>();
        for (var showIndex = 0; showIndex < nodes.Count; showIndex++)
        {
            var node = nodes[showIndex];
            var link = node["link"].InnerText;
            var queryString = HttpUtility.ParseQueryString(new Uri(link).Query);
            var showNumber = int.Parse(queryString["ShowNum"]);
            var title = node["title"].InnerText;
            var audioUrl = node["enclosure"].Attributes["url"].Value;

            shows.Add(new Show
            {
                Index = showIndex + 1,
                Number = showNumber,
                Title = title,
                AudioUrl = audioUrl
            });
        }

        return JsonSerializer.Serialize(shows);
    }
}

public class Show
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("show_number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; }
    [JsonPropertyName("audio_url")] public string AudioUrl { get; set; }
}