using System.Text.Json;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services;

/// <summary>Un mensaje guardado de una conversación con Claude.</summary>
public sealed class ChatMessage
{
    /// <summary>"user", "assistant" o "note" (avisos de la app: errores, archivos tocados).</summary>
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public double? ReportedCostUsd { get; set; }
    public StrategyTokenUsage? TokenUsage { get; set; }
}

/// <summary>Conversación con Claude: los mensajes y el id de sesión para seguirla donde quedó (--resume).</summary>
public sealed class ChatConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    // Missing engine in legacy JSON is Claude; unknown values are never interpreted as Claude.
    public string Engine { get; set; } = "claude";
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? SessionId { get; set; }
    public bool Lab { get; set; }

    /// <summary>Archivo de estrategia abierto cuando empezó la charla.</summary>
    public string? Strategy { get; set; }

    public List<ChatMessage> Messages { get; set; } = new();
}

/// <summary>Historial de conversaciones con Claude (un JSON por conversación en la carpeta de datos).</summary>
public static class ChatHistory
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Root => Path.Combine(AppPaths.Root, "Chats");

    public static IReadOnlyList<ChatConversation> List()
    {
        var res = new List<ChatConversation>();
        if (!Directory.Exists(Root)) return res;
        foreach (var f in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ChatConversation>(File.ReadAllText(f), Json) is { } c) res.Add(c);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }
        }

        return res.OrderByDescending(c => c.UpdatedUtc).ToList();
    }

    public static void Save(ChatConversation c)
    {
        if (c.Messages.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Root);
            c.UpdatedUtc = DateTime.UtcNow;
            File.WriteAllText(Path.Combine(Root, c.Id + ".json"), JsonSerializer.Serialize(c, Json));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    public static void Delete(string id)
    {
        try
        {
            File.Delete(Path.Combine(Root, id + ".json"));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}
