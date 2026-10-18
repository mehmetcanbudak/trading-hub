using System.Collections.Concurrent;

namespace TradingHub.Localization
{
    public static partial class Strings
    {
        private static readonly ConcurrentDictionary<string, string> Overrides = new();

        /// <summary>Permite reemplazar textos en tiempo de ejecución (traducciones del usuario).</summary>
        public static void Set(string key, string value) => Overrides[key] = value;

        public static string Get(string key, string fallback) => Overrides.TryGetValue(key, out var v) ? v : EnglishDefaults.GetValueOrDefault(key, fallback);
    }
}
