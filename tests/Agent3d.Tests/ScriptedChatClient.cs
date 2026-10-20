using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent3d.Tests;

internal sealed class ScriptedChatClient(
    params Func<IList<ChatMessage>, ChatOptions?, ChatResponse>[] steps) : IChatClient
{
    private readonly Queue<Func<IList<ChatMessage>, ChatOptions?, ChatResponse>> _steps = new(steps);
    public int Requests { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests++;
        return Task.FromResult(_steps.Dequeue()(messages.ToList(), options));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    public static ChatResponse Call(string name, Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments)])
        { MessageId = Guid.NewGuid().ToString("N") })
        { ResponseId = Guid.NewGuid().ToString("N"), FinishReason = ChatFinishReason.ToolCalls };

    public static ChatResponse Text(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text) { MessageId = Guid.NewGuid().ToString("N") })
        { ResponseId = Guid.NewGuid().ToString("N"), FinishReason = ChatFinishReason.Stop };
}
