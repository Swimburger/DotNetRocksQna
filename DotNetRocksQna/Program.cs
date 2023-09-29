using System.Text;
using System.Text.Json;
using AssemblyAI.SemanticKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Memory.Chroma;
using Microsoft.SemanticKernel.Text;

namespace DotNetRocksQna;

internal class Program
{
    private const string CompletionModel = "gpt-3.5-turbo";
    private const string EmbeddingsModel = "text-embedding-ada-002";
    private const string ChromeDbEndpoint = "http://localhost:8000";

    private static IConfiguration config;
    private static IKernel kernel;

    public static async Task Main(string[] args)
    {
        config = CreateConfig();
        kernel = CreateKernel();
        RegisterPlugins();

        var show = await PickShow();
        await TranscribeShow(show);
        await AskQuestions(show);
    }

    private static IConfiguration CreateConfig() => new ConfigurationBuilder()
        .AddUserSecrets<Program>().Build();

    private static IKernel CreateKernel()
    {
        var openAiApiKey = config["OpenAI:ApiKey"] ?? throw new Exception("OpenAI:ApiKey configuration required.");
        var loggerFactory = LoggerFactory.Create(builder => { builder.SetMinimumLevel(0); });
        return new KernelBuilder()
            .WithOpenAIChatCompletionService(CompletionModel, openAiApiKey)
            .WithMemoryStorage(new ChromaMemoryStore(ChromeDbEndpoint))
            .WithOpenAITextEmbeddingGenerationService(EmbeddingsModel, openAiApiKey)
            .WithLoggerFactory(loggerFactory)
            .Build();
    }

    private static void RegisterPlugins()
    {
        var assemblyAiApiKey = config["AssemblyAI:ApiKey"] ??
                               throw new Exception("AssemblyAI:ApiKey configuration required.");
        kernel.ImportSkill(
            new TranscriptPlugin(assemblyAiApiKey),
            TranscriptPlugin.PluginName
        );

        kernel.ImportSkill(
            new DotNetRocksPlugin(),
            DotNetRocksPlugin.PluginName
        );
    }

    private static async Task<Show> PickShow()
    {
        ConsoleSpinner.Start("Getting .NET Rocks! shows\n");

        var context = kernel.CreateNewContext();
        try
        {
            var getShowsFunction = kernel.Skills.GetFunction(
                DotNetRocksPlugin.PluginName,
                DotNetRocksPlugin.GetShowsFunctionName
            );
            await getShowsFunction.InvokeAsync(context);
            context.Variables["shows"] = context.Result;

            const string listShowsPrompt = """
                                           Be succinct.
                                           ---
                                           Here is a list of .NET Rocks! shows:
                                           {{$shows}}
                                           ---
                                           Show titles as ordered list and ask me to pick one.
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
                                          Be succinct. Don't explain your reasoning.
                                          Select the JSON object from the array below using the given query.
                                          If the query is a number, it is likely the "index" property of the JSON objects.
                                          ---
                                          JSON array: {{$shows}}
                                          Query: {{$query}}
                                          JSON object:
                                          """;
            var pickShowFunction = kernel.CreateSemanticFunction(pickShowPrompt, temperature: 0);
            context.Variables["query"] = answer;
            await pickShowFunction.InvokeAsync(context);
            ConsoleSpinner.Stop();
            
            var show = JsonSerializer.Deserialize<Show>(context.Result) ??
                       throw new Exception($"Failed to deserialize show.");

            Console.WriteLine($"You picked show: \"{show.Title}\"\n");
            return show;
        }
        catch (Exception)
        {    
            ConsoleSpinner.Stop();
            Console.WriteLine("Something went wrong. Context result: {0}", context.Result);
            throw;
        }
    }

    private static async Task TranscribeShow(Show show)
    {
        var collectionName = $"Transcript_{show.Number}";
        var collections = await kernel.Memory.GetCollectionsAsync();
        if (collections.Contains(collectionName))
        {
            Console.WriteLine("Show already transcribed\n");
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

    private static async Task AskQuestions(Show show)
    {
        var collectionName = $"Transcript_{show.Number}";
        const string askQuestionPrompt = """
                                         You are a Q&A assistant who will answer questions about the transcript of the given podcast show.
                                         ---
                                         Here is context from the show transcript: {{$transcript}}
                                         ---
                                         {{$question}}
                                         """;
        var askQuestionFunction = kernel.CreateSemanticFunction(askQuestionPrompt);

        while (true)
        {
            Console.Write("Ask a question: ");
            var question = Console.ReadLine();

            ConsoleSpinner.Start("Generating answers");
            var promptBuilder = new StringBuilder();
            await foreach (var searchResult in kernel.Memory.SearchAsync(collectionName, question, limit: 3))
            {
                promptBuilder.AppendLine(searchResult.Metadata.Text);
            }

            if (promptBuilder.Length == 0) Console.WriteLine("No context retrieved from transcript vector DB.\n");

            var context = kernel.CreateNewContext();
            context.Variables["transcript"] = promptBuilder.ToString();
            context.Variables["question"] = question;

            await askQuestionFunction.InvokeAsync(context);
            ConsoleSpinner.Stop();
            Console.WriteLine(context.Result);
        }
    }
}