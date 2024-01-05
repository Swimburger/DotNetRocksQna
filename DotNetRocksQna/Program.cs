using System.Text;
using System.Text.Json;
using AssemblyAI.SemanticKernel;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Chroma;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Text;

namespace DotNetRocksQna;

internal class Program
{
    private const string CompletionModel = "gpt-3.5-turbo";
    private const string EmbeddingsModel = "text-embedding-ada-002";
    private const string ChromeDbEndpoint = "http://localhost:8000";

    private static IConfiguration config;
    private static Kernel kernel;
    private static ISemanticTextMemory memory;

    public static async Task Main(string[] args)
    {
        config = CreateConfig();
        kernel = CreateKernel();
        memory = CreateMemory();

        var show = await PickShow();
        await TranscribeShow(show);
        await AskQuestions(show);
    }

    private static IConfiguration CreateConfig() => new ConfigurationBuilder()
        .AddUserSecrets<Program>().Build();

    private static string GetOpenAiApiKey() => config["OpenAI:ApiKey"] ??
                                               throw new Exception("OpenAI:ApiKey configuration required.");

    private static string GetAssemblyAiApiKey() => config["AssemblyAI:ApiKey"] ??
                                                   throw new Exception("AssemblyAI:ApiKey configuration required.");

    private static Kernel CreateKernel()
    {
        var builder = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion(CompletionModel, GetOpenAiApiKey());

        builder.Plugins.AddFromObject(new TranscriptPlugin(GetAssemblyAiApiKey()));
        builder.Plugins.AddFromType<DotNetRocksPlugin>();

        return builder.Build();
    }

    private static ISemanticTextMemory CreateMemory()
    {
        return new MemoryBuilder()
            .WithChromaMemoryStore(ChromeDbEndpoint)
            .WithOpenAITextEmbeddingGeneration(EmbeddingsModel, GetOpenAiApiKey())
            .Build();
    }

    private static async Task<Show> PickShow()
    {
        ConsoleSpinner.Start("Getting .NET Rocks! shows\n");

        try
        {
            var shows = (await kernel.InvokeAsync(
                nameof(DotNetRocksPlugin),
                DotNetRocksPlugin.GetShowsFunctionName
            )).GetValue<string>();

            var showsList = (await kernel.InvokePromptAsync(
                """
                Be succinct.
                ---
                Here is a list of .NET Rocks! shows:
                {{$shows}}
                ---
                Show titles as ordered list and ask me to pick one.
                """,
                new KernelArguments { ["shows"] = shows }
            )).GetValue<string>();
            ConsoleSpinner.Stop();

            Console.WriteLine(showsList);
            Console.WriteLine();
            Console.Write("Pick a show: ");
            var query = Console.ReadLine() ?? throw new Exception("You have to pick a show.");

            ConsoleSpinner.Start("Querying show");
            var jsonShow = (await kernel.InvokePromptAsync(
                """
                Be succinct. Don't explain your reasoning.
                Select the JSON object from the array below using the given query.
                If the query is a number, it is likely the "index" property of the JSON objects.
                ---
                JSON array: {{$shows}}
                Query: {{$query}}
                JSON object:
                """,
                new KernelArguments
                {
                    ["shows"] = shows,
                    ["query"] = query
                }
            )).GetValue<string>();
            ConsoleSpinner.Stop();

            var show = JsonSerializer.Deserialize<Show>(jsonShow) ??
                       throw new Exception($"Failed to deserialize show.");

            Console.WriteLine($"You picked show: \"{show.Title}\"\n");
            return show;
        }
        catch (Exception)
        {
            ConsoleSpinner.Stop();
            throw;
        }
    }

    private static async Task TranscribeShow(Show show)
    {
        var collectionName = $"Transcript_{show.Number}";
        var collections = await memory.GetCollectionsAsync();
        if (collections.Contains(collectionName))
        {
            Console.WriteLine("Show already transcribed\n");
            return;
        }

        ConsoleSpinner.Start("Transcribing show");
        var transcript = (await kernel.InvokeAsync(
            nameof(TranscriptPlugin),
            TranscriptPlugin.TranscribeFunctionName,
            new KernelArguments
            {
                ["INPUT"] = show.AudioUrl
            }
        )).GetValue<string>();

        var paragraphs = TextChunker.SplitPlainTextParagraphs(
            TextChunker.SplitPlainTextLines(transcript, 128),
            1024
        );
        for (var i = 0; i < paragraphs.Count; i++)
        {
            await memory.SaveInformationAsync(collectionName, paragraphs[i], $"paragraph{i}");
        }

        ConsoleSpinner.Stop();
    }

    private static async Task AskQuestions(Show show)
    {
        var collectionName = $"Transcript_{show.Number}";
        var askQuestionFunction = kernel.CreateFunctionFromPrompt("""
        You are a Q&A assistant who will answer questions about the transcript of the given podcast show.
        ---
        Here is context from the show transcript: {{$transcript}}
        ---
        {{$question}}
        """);

        while (true)
        {
            Console.Write("Ask a question: ");
            var question = Console.ReadLine();

            ConsoleSpinner.Start("Generating answers");
            var promptBuilder = new StringBuilder();
            await foreach (var searchResult in memory.SearchAsync(collectionName, question, limit: 3))
            {
                promptBuilder.AppendLine(searchResult.Metadata.Text);
            }

            if (promptBuilder.Length == 0) Console.WriteLine("No context retrieved from transcript vector DB.\n");

            var answer = (await askQuestionFunction.InvokeAsync(kernel, new KernelArguments
            {
                ["transcript"] = promptBuilder.ToString(),
                ["question"] = question
            })).GetValue<string>();
            ConsoleSpinner.Stop();
            Console.WriteLine(answer);
        }
    }
}