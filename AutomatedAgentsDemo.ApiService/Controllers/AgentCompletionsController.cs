// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.Chat;
using Microsoft.SemanticKernel.ChatCompletion;
#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace AutomatedAgentsDemo.ApiService;

/// <summary>
/// Controller for agent completions.
/// </summary>
[ApiController]
[Route("agent/completions")]
public sealed class AgentCompletionsController : ControllerBase
{
    private readonly AgentGroupChat _groupChat;
    private readonly ILogger<AgentCompletionsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentCompletionsController"/> class.
    /// </summary>
    /// <param name="agent">The agent.</param>
    /// <param name="logger">The logger.</param>
    public AgentCompletionsController(AgentGroupChat groupChat, ILogger<AgentCompletionsController> logger)
    {
        this._groupChat = groupChat;
        this._logger = logger;
    }

    /// <summary>
    /// Completes the agent request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [HttpPost]
    public async Task<IActionResult> CompleteAsync([FromBody] AgentCompletionRequest request, CancellationToken cancellationToken)
    {
        ValidateChatHistory(request.ChatHistory);

        // Add the "question" argument used in the agent template.
        var arguments = new KernelArguments
        {
            ["question"] = request.Prompt
        };

        request.ChatHistory.AddUserMessage(request.Prompt);
        _groupChat.AddChatMessage(new ChatMessageContent(AuthorRole.User, request.Prompt));

        if (request.IsStreaming)
        {
            return this.Ok(this.CompleteStreamingAsync(request.ChatHistory, arguments, cancellationToken));
        }

        return this.Ok(this.CompleteAsync(request.ChatHistory, arguments, cancellationToken));
    }

    /// <summary>
    /// Completes the agent request.
    /// </summary>
    /// <param name="chatHistory">The chat history.</param>
    /// <param name="arguments">The kernel arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completion result.</returns>
    private async IAsyncEnumerable<ChatMessageContent> CompleteAsync(ChatHistory chatHistory, KernelArguments arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var thread = new ChatHistoryAgentThread(chatHistory);

        await foreach (ChatMessageContent response in _groupChat.InvokeAsync())
        {
            yield return response;
        }
    }

    /// <summary>
    /// Completes the agent request with streaming.
    /// </summary>
    /// <param name="chatHistory">The chat history.</param>
    /// <param name="arguments">The kernel arguments.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completion result.</returns>
    private async IAsyncEnumerable<StreamingChatMessageContent> CompleteStreamingAsync(ChatHistory chatHistory, KernelArguments arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var thread = new ChatHistoryAgentThread(chatHistory);

        IAsyncEnumerable<StreamingChatMessageContent> content = _groupChat.InvokeStreamingAsync(cancellationToken: cancellationToken);
        //thread, options: new() { KernelArguments = arguments }, 
        await foreach (StreamingChatMessageContent item in content.ConfigureAwait(false))
        {
            yield return item;
        }
        
    }

    /// <summary>
    /// Validates the chat history.
    /// </summary>
    /// <param name="chatHistory">The chat history to validate.</param>
    private static void ValidateChatHistory(ChatHistory chatHistory)
    {
        foreach (ChatMessageContent content in chatHistory)
        {
            if (content.Role == AuthorRole.System)
            {
                throw new ArgumentException("A system message is provided by the agent and should not be included in the chat history.");
            }
        }
    }

#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    private sealed class ApprovalTerminationStrategy : TerminationStrategy
#pragma warning restore SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    {
        // Terminate when the final message contains the term "approve"
        protected override Task<bool> ShouldAgentTerminateAsync(Agent agent, IReadOnlyList<ChatMessageContent> history, CancellationToken cancellationToken)
            => Task.FromResult(history[history.Count - 1].Content?.Contains("approve", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}