using System.Collections.Concurrent;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var state = new MockState();

app.MapGet("/", () => "Telegram Mock is running");

// Catch-all Telegram Bot API methods (sendMessage, sendPhoto, sendDocument, setWebhook, getFile, etc.)
app.MapPost("/bot{token}/{method}", async (string token, string method, HttpRequest request) =>
{
    var delay = state.SimulatedDelayMs;
    if (delay > 0)
    {
        await Task.Delay(delay);
    }

    string? chatId = null;
    string? text = null;

    if (request.HasJsonContentType())
    {
        try
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("chat_id", out var chatProp))
                {
                    chatId = chatProp.ToString();
                }
                if (root.TryGetProperty("text", out var textProp))
                {
                    text = textProp.GetString();
                }
                else if (root.TryGetProperty("caption", out var capProp))
                {
                    text = capProp.GetString();
                }
            }
        }
        catch
        {
            // Ignore parse errors, proceed with mock response
        }
    }
    else if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync();
        chatId = form["chat_id"].ToString();
        text = form["text"].ToString();
    }

    if (string.IsNullOrEmpty(chatId) && request.Query.TryGetValue("chat_id", out var queryChatId))
    {
        chatId = queryChatId.ToString();
    }

    var messageId = state.RecordMessage(method, chatId, text);

    long parsedChatId = 0;
    _ = long.TryParse(chatId, out parsedChatId);

    return Results.Ok(new
    {
        ok = true,
        result = new
        {
            message_id = messageId,
            date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            chat = new
            {
                id = parsedChatId != 0 ? (object)parsedChatId : chatId ?? "0",
                type = "private"
            },
            text = text ?? string.Empty
        }
    });
});

// Stats and monitoring
app.MapGet("/mock/stats", () => Results.Ok(state.GetStats()));

// Verification endpoint: wait until expectedCount or timeout
app.MapPost("/mock/verify", async (VerifyRequest req) =>
{
    var deadline = DateTime.UtcNow.AddSeconds(req.TimeoutSeconds > 0 ? req.TimeoutSeconds : 10);
    while (DateTime.UtcNow < deadline)
    {
        if (state.TotalReceived >= req.ExpectedCount)
            break;
        await Task.Delay(200);
    }

    var isSuccess = state.TotalReceived >= req.ExpectedCount;
    return Results.Ok(new
    {
        success = isSuccess,
        totalReceived = state.TotalReceived,
        expectedCount = req.ExpectedCount,
        uniqueChats = state.UniqueChatsCount,
        byMethod = state.ByMethod
    });
});

// Recent messages
app.MapGet("/mock/messages", (string? chatId, int? limit) =>
{
    var count = limit.GetValueOrDefault(50);
    return Results.Ok(state.GetMessages(chatId, count));
});

// Reset counters
app.MapDelete("/mock/reset", () =>
{
    state.Reset();
    return Results.Ok(new { message = "Mock state reset" });
});

// Configure simulated network delay
app.MapPost("/mock/delay", (SetDelayRequest req) =>
{
    state.SimulatedDelayMs = Math.Clamp(req.DelayMs, 0, 10000);
    return Results.Ok(new { simulatedDelayMs = state.SimulatedDelayMs });
});

app.Run();

public record VerifyRequest(int ExpectedCount, int TimeoutSeconds = 15);
public record SetDelayRequest(int DelayMs);

public sealed class MockState
{
    private long _messageIdCounter = 1000;
    private long _totalReceived = 0;
    private readonly ConcurrentDictionary<string, long> _byMethod = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _receivedChats = new();
    private readonly ConcurrentQueue<MockMessageRecord> _recentMessages = new();

    public int SimulatedDelayMs { get; set; } = 0;
    public long TotalReceived => Interlocked.Read(ref _totalReceived);
    public int UniqueChatsCount => _receivedChats.Count;
    public IReadOnlyDictionary<string, long> ByMethod => _byMethod;

    public long RecordMessage(string method, string? chatId, string? text)
    {
        var msgId = Interlocked.Increment(ref _messageIdCounter);
        Interlocked.Increment(ref _totalReceived);
        _byMethod.AddOrUpdate(method, 1, (_, count) => count + 1);

        var key = chatId ?? "unknown";
        _receivedChats.AddOrUpdate(key, 1, (_, count) => count + 1);

        _recentMessages.Enqueue(new MockMessageRecord(msgId, method, chatId, text, DateTime.UtcNow));
        while (_recentMessages.Count > 1000 && _recentMessages.TryDequeue(out _)) { }

        return msgId;
    }

    public object GetStats() => new
    {
        totalReceived = TotalReceived,
        uniqueChats = UniqueChatsCount,
        byMethod = _byMethod,
        simulatedDelayMs = SimulatedDelayMs
    };

    public IEnumerable<MockMessageRecord> GetMessages(string? chatId, int limit)
    {
        var query = _recentMessages.AsEnumerable();
        if (!string.IsNullOrEmpty(chatId))
        {
            query = query.Where(m => m.ChatId == chatId);
        }
        return query.Reverse().Take(limit);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _totalReceived, 0);
        _byMethod.Clear();
        _receivedChats.Clear();
        while (_recentMessages.TryDequeue(out _)) { }
    }
}

public record MockMessageRecord(long MessageId, string Method, string? ChatId, string? Text, DateTime ReceivedAtUtc);
