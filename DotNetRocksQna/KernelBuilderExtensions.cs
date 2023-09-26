using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;

namespace DotNetRocksQna;

internal static class KernelBuilderExtensions
{
    internal static KernelBuilder WithCompletionService(this KernelBuilder kernelBuilder, IConfiguration config)
    {
        switch (config["LlmService"]!)
        {
            case "OpenAI":
                switch (config["OpenAI:ModelType"]!)
                {
                    case "text-completion":
                        kernelBuilder.WithOpenAITextCompletionService(
                            modelId: config["OpenAI:TextCompletionModelId"]!,
                            apiKey: config["OpenAI:ApiKey"]!,
                            orgId: config["OpenAI:OrgId"]
                        );
                        break;
                    case "chat-completion":
                        kernelBuilder.WithOpenAIChatCompletionService(
                            modelId: config["OpenAI:ChatCompletionModelId"]!,
                            apiKey: config["OpenAI:ApiKey"]!,
                            orgId: config["OpenAI:OrgId"]
                        );
                        break;
                }

                break;

            default:
                throw new ArgumentException($"Invalid service type value: {config["OpenAI:ModelType"]}");
        }

        return kernelBuilder;
    }
}