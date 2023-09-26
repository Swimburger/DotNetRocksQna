using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using System.Xml;
using Microsoft.SemanticKernel.SkillDefinition;

namespace DotNetRocksQna;

public class DotNetRocksPlugin
{
    public const string PluginName = "DotNetRocksPlugin";
    private const string RssFeedUrl = "https://pwop.com/feed.aspx?show=dotnetrocks";

    public const string GetShowsFunctionName = nameof(GetShows);

    [SKFunction, Description("Get shows of the .NET Rocks! podcast.")]
    public async Task<string> GetShows()
    {
        using var httpClient = new HttpClient();
        var rssFeedStream = await httpClient.GetStreamAsync(RssFeedUrl);

        var doc = new XmlDocument();
        doc.Load(rssFeedStream);

        var nodes = doc.DocumentElement.SelectNodes("//item");
        var jsonArray = new JsonArray();
        foreach (XmlNode node in nodes)
        {
            var link = node["link"].InnerText;
            var queryString = HttpUtility.ParseQueryString(new Uri(link).Query);
            var showNumber = int.Parse(queryString["ShowNum"]);
            var title = node["title"].InnerText;
            var audioUrl = node["enclosure"].Attributes["url"].Value;

            var jsonObject = new JsonObject
            {
                ["show_number"] = showNumber,
                ["title"] = title,
                ["show_link"] = link,
                ["audio_url"] = audioUrl
            };
            jsonArray.Add(jsonObject);
        }

        return jsonArray.ToString();
    }
}