namespace TradingHub.Market;

/// <summary>
/// Contrato vigente de un futuro en cada fecha, para armar la historia continua (como el contrato continuo de ATAS).
/// Índices de la CME (ES, NQ, YM, RTY y sus micro): vencen el tercer viernes de marzo, junio, septiembre y diciembre.
/// El volumen pasa al siguiente el lunes de la semana del vencimiento: en los datos de Rithmic de septiembre 2026 el
/// ESZ6 operó 127 mil contratos el viernes 11 y 845 mil el lunes 14 (y el NQ igual).
/// </summary>
public static class FuturesRolls
{
    private static readonly HashSet<string> Quarterly = new(StringComparer.OrdinalIgnoreCase) { "ES", "MES", "NQ", "MNQ", "YM", "MYM", "RTY", "M2K", "EMD" };
    private const string Codes = "FGHJKMNQUVXZ";

    public static bool Supports(string root) => Quarterly.Contains(root);

    /// <summary>Vencimiento: tercer viernes del mes.</summary>
    public static DateTime Expiry(int year, int month)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        int toFriday = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(toFriday + 14);
    }

    /// <summary>Día en que el volumen pasa al contrato siguiente (el lunes de la semana del vencimiento).</summary>
    public static DateTime Roll(int year, int month) => Expiry(year, month).AddDays(-4);

    /// <summary>El mismo contrato con el año en dos cifras (NQU6 → NQU26), por si así lo nombra Rithmic ya vencido.</summary>
    public static string? TwoDigitYear(string symbol, DateTime around)
    {
        if (symbol.Length < 3 || !char.IsDigit(symbol[^1]) || !Codes.Contains(symbol[^2])) return null;
        int digit = symbol[^1] - '0';
        int year = around.Year - around.Year % 10 + digit;
        if (year > around.Year + 1) year -= 10;
        return symbol[..^1] + (year % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Quarterly month selected by the existing calendar boundary, without price adjustment.</summary>
    public static (int Year, int Month) ContractMonth(DateTime utc)
    {
        int year = utc.Year, month = ((utc.Month - 1) / 3 + 1) * 3;
        if (utc >= Roll(year, month)) { month += 3; if (month > 12) { month = 3; year++; } }
        return (year, month);
    }

    /// <summary>Símbolo de Rithmic/CME: raíz + mes + último dígito del año (ES, sep 2026 → ESU6).</summary>
    public static string Symbol(string root, int year, int month) => $"{root.ToUpperInvariant()}{Codes[month - 1]}{year % 10}";

    /// <summary>Contrato vigente en un instante, y desde/hasta cuándo lo es.</summary>
    public static (string Symbol, DateTime From, DateTime To) On(string root, DateTime utc)
    {
        int y = utc.Year, m = ((utc.Month - 1) / 3 + 1) * 3; // trimestral de este trimestre: 3, 6, 9 o 12
        if (utc >= Roll(y, m))
        {
            m += 3;
            if (m > 12)
            {
                m -= 12;
                y++;
            }
        }

        int py = y, pm = m - 3;
        if (pm < 1)
        {
            pm += 12;
            py--;
        }

        return (Symbol(root, y, m), Roll(py, pm), Roll(y, m));
    }

    /// <summary>Tramos de un rango, cada uno con su contrato.</summary>
    public static IEnumerable<(string Symbol, DateTime From, DateTime To)> Segments(string root, DateTime fromUtc, DateTime toUtc)
    {
        var t = fromUtc;
        while (t < toUtc)
        {
            var c = On(root, t);
            var end = c.To < toUtc ? c.To : toUtc;
            yield return (c.Symbol, t, end);
            t = end;
        }
    }
}
