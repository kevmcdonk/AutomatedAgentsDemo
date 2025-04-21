// Copyright (c) Microsoft. All rights reserved.

using System;
using System.ClientModel.Primitives;
using Azure.Identity;
// using AutomatedAgentsDemo.ApiService.Config;
using AutomatedAgentsDemo.ApiService.Resources;
using Microsoft.SemanticKernel.Functions;
using AutomatedAgentsDemo.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.OpenAI;
using Microsoft.SemanticKernel.Data;
using Microsoft.SemanticKernel.PromptTemplates.Handlebars;
using AutomatedAgentsDemo.ApiService.Config;
using Microsoft.Extensions.FileProviders.Embedded;
using System.Reflection;
using Microsoft.SemanticKernel.Agents.Chat;
using System.Linq;
using Microsoft.SemanticKernel.ChatCompletion;
using Azure.AI.OpenAI;
using OpenAI.Assistants;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using System.Linq.Expressions;
using OpenAI.Responses;
#pragma warning disable SKEXP0001
#pragma warning disable SKEXP0010

namespace AutomatedAgentsDemo.ApiService;

/// <summary>
/// Defines the Program class containing the application's entry point.
/// </summary>
public static class Program
{
    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Enable diagnostics.
        AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics", true);

        // Uncomment the following line to enable diagnostics with sensitive data: prompts, completions, function calls, and more.
        //AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);

        // Enable SK traces using OpenTelemetry.Extensions.Hosting extensions.
        // An alternative approach to enabling traces can be found here: https://learn.microsoft.com/en-us/semantic-kernel/concepts/enterprise-readiness/observability/telemetry-with-aspire-dashboard?tabs=Powershell&pivots=programming-language-csharp 
        builder.Services.AddOpenTelemetry().WithTracing(b => b.AddSource("Microsoft.SemanticKernel*"));

        // Enable SK metrics using OpenTelemetry.Extensions.Hosting extensions.
        // An alternative approach to enabling metrics can be found here: https://learn.microsoft.com/en-us/semantic-kernel/concepts/enterprise-readiness/observability/telemetry-with-aspire-dashboard?tabs=Powershell&pivots=programming-language-csharp
        builder.Services.AddOpenTelemetry().WithMetrics(b => b.AddMeter("Microsoft.SemanticKernel*"));

        // Enable SK logs.
        // Log source and log level for SK is configured in appsettings.json.
        // An alternative approach to enabling logs can be found here: https://learn.microsoft.com/en-us/semantic-kernel/concepts/enterprise-readiness/observability/telemetry-with-aspire-dashboard?tabs=Powershell&pivots=programming-language-csharp

        // Add service defaults & Aspire client integrations.
        builder.AddServiceDefaults();

        builder.Services.AddControllers();

        // Add services to the container.
        builder.Services.AddProblemDetails();

        // Load the service configuration.
        var config = new ServiceConfig(builder.Configuration);

        // Add Kernel
        builder.Services.AddKernel();

        // Add AI services.
        AddAIServices(builder, config.Host);

        // Add Vector Store.
        //AddVectorStore(builder, config.Host);

        // Add Agent.
        AddAgents(builder, config.Host);

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        app.UseExceptionHandler();

        app.MapDefaultEndpoints();

        app.MapControllers();

        app.Run();
    }

    /// <summary>
    /// Adds AI services for chat completion and text embedding generation.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="config">Service configuration.</param>
    /// <exception cref="NotSupportedException"></exception>
    private static void AddAIServices(WebApplicationBuilder builder, HostConfig config)
    {
        // Add AzureOpenAI client.
        if (config.AIChatService == AzureOpenAIChatConfig.ConfigSectionName || config.Rag.AIEmbeddingService == AzureOpenAIEmbeddingsConfig.ConfigSectionName)
        {
            builder.AddAzureOpenAIClient(
                connectionName: HostConfig.AzureOpenAIConnectionStringName,
                configureSettings: (settings) => settings.Credential = builder.Environment.IsProduction()
                    ? new DefaultAzureCredential()
                    : new AzureCliCredential(),
                configureClientBuilder: clientBuilder =>
                {
                    clientBuilder.ConfigureOptions((options) =>
                    {
                        options.RetryPolicy = new ClientRetryPolicy(maxRetries: 3);
                    });
                });
        }

        /*
                // Add OpenAI client.
                if (config.AIChatService == AzureOpenAIChatConfig.ConfigSectionName || config.Rag.AIEmbeddingService == OpenAIEmbeddingsConfig.ConfigSectionName)
                {
                    builder.AddOpenAIClient(HostConfig.OpenAIConnectionStringName);
                }
        */
        // Add chat completion services.
        switch (config.AIChatService)
        {
            case AzureOpenAIChatConfig.ConfigSectionName:
                {
                    builder.Services.AddAzureOpenAIChatCompletion(config.AzureOpenAIChat.DeploymentName, modelId: config.AzureOpenAIChat.ModelName);
                    break;
                }
            /*case OpenAIChatConfig.ConfigSectionName:
                {
                    builder.Services.AddOpenAIChatCompletion(config.OpenAIChat.ModelName);
                    break;
                }*/
            default:
                throw new NotSupportedException($"AI chat service '{config.AIChatService}' is not supported.");
        }

        // Add text embedding generation services.
        switch (config.Rag.AIEmbeddingService)
        {
            case AzureOpenAIEmbeddingsConfig.ConfigSectionName:
                {
                    builder.Services.AddAzureOpenAITextEmbeddingGeneration(config.AzureOpenAIEmbeddings.DeploymentName, modelId: config.AzureOpenAIEmbeddings.ModelName);
                    break;
                }
            /*
        case OpenAIEmbeddingsConfig.ConfigSectionName:
            {
                builder.Services.AddOpenAITextEmbeddingGeneration(config.OpenAIEmbeddings.ModelName);
                break;
            }*/
            default:
                throw new NotSupportedException($"AI embeddings service '{config.Rag.AIEmbeddingService}' is not supported.");
        }
    }

    /// <summary>
    /// Adds the vector store to the service collection.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="config">The host configuration.</param>
    private static void AddVectorStore(WebApplicationBuilder builder, HostConfig config)
    {
        // Don't add vector store if no collection name is provided. Allows for a basic experience where no data has been uploaded to the vector store yet.
        if (string.IsNullOrWhiteSpace(config.Rag.CollectionName))
        {
            return;
        }

        /*
                // Add Vector Store
                switch (config.Rag.VectorStoreType)
                {
                    case AzureAISearchConfig.ConfigSectionName:
                    {

                        builder.AddAzureSearchClient(
                            connectionName: AzureAISearchConfig.ConnectionStringName,
                            configureSettings: (settings) => settings.Credential = builder.Environment.IsProduction()
                                ? new DefaultAzureCredential()
                                : new AzureCliCredential()
                        );
                        builder.Services.AddAzureAISearchVectorStoreRecordCollection<TextSnippet<string>>(config.Rag.CollectionName);
                        builder.Services.AddVectorStoreTextSearch<TextSnippet<string>>();
                        break;
                    }
                    default:
                        throw new NotSupportedException($"Vector store type '{config.Rag.VectorStoreType}' is not supported.");
                }
                */
    }

    /// <summary>
    /// Adds the chat completion agent to the service collection.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="config">The host configuration.</param>
    private static void AddAgents(WebApplicationBuilder builder, HostConfig config)
    {
        // Register agent without RAG if no collection name is provided. Allows for a basic experience where no data has been uploaded to the vector store yet.
        /*if (string.IsNullOrEmpty(config.Rag.CollectionName))
        {*/
        var agents = GetAgentTemplates();
        List<OpenAIAssistantAgent> agentList = new List<OpenAIAssistantAgent>();

#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable OPENAI001

        builder.Services.AddTransient<AgentGroupChat>((sp) =>
            {
                try
                {
                    Kernel kernel = sp.GetRequiredService<Kernel>();

                    // AzureOpenAIClient client = OpenAIAssistantAgent.CreateAzureOpenAIClient(new AzureCredential(), new Uri(config.AzureOpenAIEndpoint));
                    AzureOpenAIClient client = OpenAIAssistantAgent.CreateAzureOpenAIClient(new InteractiveBrowserCredential(), new Uri(config.AzureOpenAIEndpoint));
                    AssistantClient assistantClient = client.GetAssistantClient();

                    //foreach (var agent in agents)
                    //{
                        //PromptTemplateConfig templateConfig = Microsoft.SemanticKernel.KernelFunctionYaml.ToPromptTemplateConfig(agent.Value);
                        //Assistant assistant = assistantClient.CreateAssistantFromTemplateAsync(config.AzureOpenAIChat.ModelName, templateConfig).GetAwaiter().GetResult();
                        Assistant assistant = assistantClient.CreateAssistantAsync(
                            config.AzureOpenAIChat.ModelName,
                            "AutomationCrewLeader",
                            instructions: 
                            """
                            You are a friendly and helpful agent that will connect to the right agent to automatically resolve the task that the employee is looking to do.
                            You will also provide a list of other agents that can be used to automate the process.
                            You will say the name of the agent to use for the next step in the task.
                            If it cannot determine an agent to use, you will aim to answer the question from your own knowledge.
                            You should not ask a question of the user.
                            """,
                            enableCodeInterpreter: true,
                            temperature:0
                            //codeInterpreterFileIds: [fileDataCountryList.Id, fileDataCountryDetail.Id]            
                        ).GetAwaiter().GetResult();

                        OpenAIAssistantAgent openAIAgent = new(assistant, assistantClient);
                        agentList.Add(openAIAgent);

                        assistant  = assistantClient.CreateAssistantAsync(
                            config.AzureOpenAIChat.ModelName,
                            "SignatureReviewer",
                            instructions: 
                            """
                            A productive agent that checks the supplied image to confirm whether a signature has been applied.
                            If no image is supplied, it will ask for one.
                            If a document is supplied, it will check for a signature and return the result.
                            If a image is supplied but it is not a valid image, it will ask for a valid image.
                            """,
                            enableCodeInterpreter: true,
                            temperature:0
                            //codeInterpreterFileIds: [fileDataCountryList.Id, fileDataCountryDetail.Id]            
                        ).GetAwaiter().GetResult();
                        
                        openAIAgent = new(assistant, assistantClient);
                        agentList.Add(openAIAgent);

                        assistant  = assistantClient.CreateAssistantAsync(
                            config.AzureOpenAIChat.ModelName,
                            "LeaveReviewer",
                            instructions: 
                            """
                            A strict agent that reviews requests for leave and looks at whether it fits one of the types of leave below.
                            - Annual leave - used for most leave requests and used for holidays and standard time off.
                            - Sick leave - used when an employee is unable to work due to illness or injury.
                            - Family leave - used when an employee needs to take time off to care for a family member.
                            - Bereavement leave - used when an employee needs to take time off due to the death of a loved one.
                            - Personal leave - used when an employee needs to take time off for personal reasons that do not fall under other categories.
                            - Unpaid leave - used when an employee needs to take time off without pay.
                            - Maternity leave - used when an employee is taking time off for the birth or adoption of a child.
                            - Paternity leave - used when an employee is taking time off for the birth or adoption of a child.
                            - Parental leave - used when an employee is taking time off to care for a child.
                            - Military leave - used when an employee is called to active duty or training in the military.
                            - Jury duty leave - used when an employee is called to serve on a jury or as a witness in a legal proceeding.
                            - Voting leave - used when an employee needs time off to vote in an election.
                            - Leave of absence - used when an employee needs to take time off for an extended period of time.
                            - Compensatory leave - used when an employee is given time off in lieu of overtime worked.
                            - Study leave - used when an employee is taking time off to study or attend classes.
                            - Sabbatical leave - used when an employee is taking an extended period of time off for personal or professional development.
                            """,
                            enableCodeInterpreter: true,
                            temperature:0
                            //codeInterpreterFileIds: [fileDataCountryList.Id, fileDataCountryDetail.Id]            
                        ).GetAwaiter().GetResult();
                        
                        openAIAgent = new(assistant, assistantClient);
                        agentList.Add(openAIAgent);
                    //}

                    string selectionStrategyText = EmbeddedResource.Read("PromptFunction-SelectionStrategy.yaml");
                    KernelFunction selectionFunction = AgentGroupChat.CreatePromptFunctionForStrategy(selectionStrategyText, safeParameterNames: "history");

                    // Define the selection strategy
                    KernelFunctionSelectionStrategy selectionStrategy = new(selectionFunction, kernel)
                    {
                        // Always start with the writer agent.
                        InitialAgent = agentList[0],
                        // Parse the function response.
                        ResultParser = (result) => result.GetValue<string>() ?? agentList[0].Name,
                        // The prompt variable name for the history argument.
                        HistoryVariableName = "history",
                        // Save tokens by not including the entire history in the prompt
                        HistoryReducer = new ChatHistoryTruncationReducer(3),
                    };

                    string terminationStrategyText = EmbeddedResource.Read("PromptFunction-TerminationStrategy.yaml");
                    KernelFunction terminationFunction = AgentGroupChat.CreatePromptFunctionForStrategy(terminationStrategyText, safeParameterNames: "history");

                    // Define the termination strategy
                    KernelFunctionTerminationStrategy terminationStrategy =
                      new(terminationFunction, kernel)
                      {
                          // Only the reviewer may give approval.
                          Agents = agentList,
                          // Parse the function response.
                          ResultParser = (result) =>
                            result.GetValue<string>()?.Contains("true", StringComparison.OrdinalIgnoreCase) ?? false,
                          // The prompt variable name for the history argument.
                          HistoryVariableName = "history",
                          // Save tokens by not including the entire history in the prompt
                          HistoryReducer = new ChatHistoryTruncationReducer(4),
                          // Limit total number of turns no matter what
                          MaximumIterations = 5,
                      };

                    AgentGroupChat chat = new(agentList.ToArray())
                    {
                        ExecutionSettings =
                            new()
                            {
                                TerminationStrategy = terminationStrategy
                            }
                    };
                    return chat;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error creating agent: {ex.Message}");
                    throw;
                }
            });

        /*}
        else
        {
            // Register agent with RAG.
            PromptTemplateConfig templateConfig = KernelFunctionYaml.ToPromptTemplateConfig(EmbeddedResource.Read("AgentWithRagDefinition.yaml"));

            switch (config.Rag.VectorStoreType)
            {
                case AzureAISearchConfig.ConfigSectionName:
                {
                    AddAgentWithRag<string>(builder, templateConfig);
                    break;
                }
                default:
                    throw new NotSupportedException($"Vector store type '{config.Rag.VectorStoreType}' is not supported.");
            }
        }*/

        /*
            static void AddAgentWithRag<TKey>(WebApplicationBuilder builder, PromptTemplateConfig templateConfig)
            {
                builder.Services.AddTransient<ChatCompletionAgent>((sp) =>
                {
                    Kernel kernel = sp.GetRequiredService<Kernel>();
                    VectorStoreTextSearch<TextSnippet<TKey>> vectorStoreTextSearch = sp.GetRequiredService<VectorStoreTextSearch<TextSnippet<TKey>>>();

                    // Add a search plugin to the kernel which we will use in the agent template
                    // to do a vector search for related information to the user query.
                    kernel.Plugins.Add(vectorStoreTextSearch.CreateWithGetTextSearchResults("SearchPlugin"));

                    return new ChatCompletionAgent(templateConfig, new HandlebarsPromptTemplateFactory())
                    {
                        Kernel = kernel,
                    };
                });
            }
            */
    }

    private static Dictionary<string, string> GetAgentTemplates()
    {
        // Get the current assembly
        var assembly = Assembly.GetExecutingAssembly();
        Dictionary<string, string> agents = new Dictionary<string, string>();

        // Get all embedded resource names
        var resourceNames = assembly.GetManifestResourceNames();

        // Iterate through each resource
        foreach (var resourceName in resourceNames)
        {
            Console.WriteLine($"Resource Name: {resourceName}");
            // Only add resources that have the Agent prefix
            if (resourceName.StartsWith("AutomatedAgentsDemo.ApiService.Resources.Agent"))
            {
                // Read the resource stream
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            var content = reader.ReadToEnd();
                            Console.WriteLine($"Content of {resourceName}:\n{content}");
                            agents.Add(resourceName, content);
                        }
                    }
                }
            }
        }
        return agents;
    }
}