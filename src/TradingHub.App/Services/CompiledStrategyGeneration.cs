using TradingHub.Scripting;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

public sealed class CompiledStrategyGeneration
{
    internal CompiledStrategyGeneration(ScriptSource source, ScriptCompileResult compilation, Type type)
    {
        Source = source;
        Compilation = compilation;
        Type = type;
        SourceDigest = StrategyStore.HashCode(source.Text);
        Id = type.FullName ?? type.Name;
        DisplayName = Strategy.DisplayName(type);
    }

    public ScriptSource Source { get; }
    public ScriptCompileResult Compilation { get; }
    public Type Type { get; }
    public string SourceDigest { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string FileName => Path.GetFileNameWithoutExtension(Source.Path);
    public Strategy Create() => (Strategy)Activator.CreateInstance(Type)!;
}

public sealed class StrategyCompilation
{
    internal StrategyCompilation(ScriptSource source, ScriptCompileResult result, CompiledStrategyGeneration? generation)
    {
        Source = source;
        Result = result;
        Generation = generation;
    }

    public ScriptSource Source { get; }
    public ScriptCompileResult Result { get; }
    public CompiledStrategyGeneration? Generation { get; }
    public bool Success => Generation != null;
    public IReadOnlyList<ScriptDiagnostic> Diagnostics => Result.Diagnostics;
    public TimeSpan Elapsed => Result.Elapsed;
}
