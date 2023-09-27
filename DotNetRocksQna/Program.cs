using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssemblyAI.SemanticKernel;
using DotNetRocksQna;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Memory.Chroma;
using Microsoft.SemanticKernel.Text;

var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>()
    .AddCommandLine(args)
    .Build();

using var loggerFactory = LoggerFactory.Create(builder => { builder.SetMinimumLevel(0); });
var kernel = new KernelBuilder()
    .WithOpenAIChatCompletionService(
        "gpt-3.5-turbo",
        config["OpenAI:ApiKey"] ?? throw new Exception("OpenAI:ApiKey configuration required.")
    )
    .WithMemoryStorage(new ChromaMemoryStore("http://localhost:8000"))
    .WithOpenAITextEmbeddingGenerationService(
        "text-embedding-ada-002",
        config["OpenAI:ApiKey"] ?? throw new Exception("OpenAI:ApiKey configuration required.")
    )
    .WithLoggerFactory(loggerFactory)
    .Build();

RegisterPlugins();

var show = await PickShow();
await TranscribeShow(show);
await AskQuestions(show);

async Task<Show> PickShow()
{
    ConsoleSpinner.Start("Getting .NET Rocks! shows");

    var context = kernel.CreateNewContext();
    var getShowsFunction = kernel.Skills.GetFunction(
        DotNetRocksPlugin.PluginName,
        DotNetRocksPlugin.GetShowsFunctionName
    );
    await getShowsFunction.InvokeAsync(context);
    context.Variables["shows"] = context.Result;

    var listShowsPrompt = """
                          Be concise
                          ---
                          Here is a list of podcast shows:
                          {{$shows}}
                          ---
                          Show titles as ordered list:
                          """;
    var listShowsFunction = kernel.CreateSemanticFunction(listShowsPrompt, temperature: 0);
    var showsList = (await listShowsFunction.InvokeAsync(context)).Result;
    ConsoleSpinner.Stop();

    Console.WriteLine(showsList);
    Console.WriteLine();
    Console.Write("Pick a show: ");
    var answer = Console.ReadLine() ?? throw new Exception("You have to pick a show.");

    ConsoleSpinner.Start("Querying show");
    const string pickShowPrompt = """
                                  Return the show JSON object from the array below using the following query.
                                  List: {{$shows}}
                                  Query: {{$query}}
                                  """;
    var pickShowFunction = kernel.CreateSemanticFunction(pickShowPrompt, temperature: 0);
    context.Variables["query"] = answer;
    await pickShowFunction.InvokeAsync(context);

    var show = JsonSerializer.Deserialize<Show>(context.Result) ?? throw new Exception("Failed to deserialize show.");
    ConsoleSpinner.Stop();

    Console.WriteLine($"You picked show: \"{show.Title}\"");
    return show;
}

async Task TranscribeShow(Show show)
{
    var collectionName = $"Transcript_{show.Number}";
    var collections = await kernel.Memory.GetCollectionsAsync();
    if (collections.Contains(collectionName))
    {
        Console.WriteLine("Show already transcribed");
        return;
    }

    ConsoleSpinner.Start("Transcribing show");
    var transcribeFunction = kernel.Skills.GetFunction(
        TranscriptPlugin.PluginName, 
        TranscriptPlugin.TranscribeFunctionName
    );
    var context = kernel.CreateNewContext();
    context.Variables["audioUrl"] = show.AudioUrl;
    await transcribeFunction.InvokeAsync(context);
    var transcript = context.Result;

    var paragraphs = TextChunker.SplitPlainTextParagraphs(
        TextChunker.SplitPlainTextLines(transcript, 128),
        1024
    );
    for (var i = 0; i < paragraphs.Count; i++)
    {
        await kernel.Memory.SaveInformationAsync(collectionName, paragraphs[i], $"paragraph{i}");
    }

    ConsoleSpinner.Stop();
}

async Task AskQuestions(Show show)
{
    var collectionName = $"Transcript_{show.Number}";
    var askQuestionPrompt = """
                            Here is a transcript: {{$transcript}}
                            ---
                            {{$question}}
                            """;
    var askQuestionFunction = kernel.CreateSemanticFunction(askQuestionPrompt);

    while (true)
    {
        Console.WriteLine();
        Console.Write("Ask a question: ");
        var question = Console.ReadLine();

        ConsoleSpinner.Start("Generating answers");
        var promptBuilder = new StringBuilder();
        await foreach (var searchResult in kernel.Memory.SearchAsync(collectionName, question, limit: 3))
        {
            promptBuilder.AppendLine(searchResult.Metadata.Text);
        }

        var context = kernel.CreateNewContext();
        context.Variables["transcript"] = promptBuilder.ToString();
        context.Variables["question"] = question;

        await askQuestionFunction.InvokeAsync(context);
        ConsoleSpinner.Stop();
        Console.WriteLine(context.Result);
    }
}

void RegisterPlugins()
{
    var transcriptPlugin = kernel.ImportSkill(
        new TranscriptPlugin(config["AssemblyAI:ApiKey"]),
        TranscriptPlugin.PluginName
    );

    var dotNetRocksPlugin = kernel.ImportSkill(
        new DotNetRocksPlugin(),
        DotNetRocksPlugin.PluginName
    );
}

class Show
{
    [JsonPropertyName("show_number")] public int Number { get; set; }

    [JsonPropertyName("title")] public string Title { get; set; }

    [JsonPropertyName("audio_url")] public string AudioUrl { get; set; }
}