using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using TradingHub.Trading;

namespace TradingHub.Indicators
{
    /// <summary>Filtro genérico: un valor con un interruptor (checkbox + valor en el editor).</summary>
    public class Filter<T> : INotifyPropertyChanged
    {
        private T _value;
        private bool _enabled;
        private bool _isEnabledEditable = true;
        private readonly List<Action<T>> _valueHandlers = new();

        public Filter()
        {
        }

        public Filter(bool isEnabled)
        {
            _enabled = isEnabled;
        }

        public Filter(bool isEnabled, T value)
        {
            _enabled = isEnabled;
            _value = value;
        }

        /// <summary>Como en ATAS: (si el interruptor se puede cambiar, si está activado).</summary>
        protected Filter(bool isEnabledEditable, bool isEnabled, int marker)
        {
            _isEnabledEditable = isEnabledEditable;
            _enabled = isEnabled;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private Func<ValueChangingArgs<T>, T> _coerce;

        [Display(Name = "Value")]
        public virtual T Value
        {
            get => _value;
            set
            {
                if (_coerce != null) value = _coerce(new ValueChangingArgs<T>(_value, value));
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                _value = value;
                OnPropertyChanged();
                foreach (var h in _valueHandlers.ToArray()) h(value);
            }
        }

        [Display(Name = "Enabled")]
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Si el usuario puede cambiar el interruptor.</summary>
        [Browsable(false)]
        public bool IsEnabledEditable => _isEnabledEditable;

        public void SetEnabled(bool editable)
        {
            _isEnabledEditable = editable;
            OnPropertyChanged(nameof(IsEnabledEditable));
        }

        /// <summary>Corrige el valor antes de aplicarlo (por ejemplo, limitarlo a positivos).</summary>
        public Filter<T> ValueOnChanging(Func<ValueChangingArgs<T>, T> coerce)
        {
            _coerce = coerce;
            return this;
        }

        /// <summary>Registra una acción a ejecutar cuando cambia el valor.</summary>
        public Filter<T> ValueOnChanged(Action<T> handler)
        {
            if (handler != null) _valueHandlers.Add(handler);
            return this;
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public override string ToString() => Enabled ? $"{Value}" : "-";
    }

    /// <summary>Valor anterior y nuevo de un filtro que está por cambiar.</summary>
    public readonly record struct ValueChangingArgs<T>(T OldValue, T NewValue);

    public class FilterInt : Filter<int>
    {
        public FilterInt() { }
        public FilterInt(bool isEnabled) : base(isEnabled) { }
        public FilterInt(bool isEnabled, int value) : base(isEnabled, value) { }
        public FilterInt(bool isEnabledEditable, bool isEnabled) : base(isEnabledEditable, isEnabled, 0) { }
    }

    public class FilterDecimal : Filter<decimal>
    {
        public FilterDecimal() { }
        public FilterDecimal(bool isEnabled) : base(isEnabled) { }
        public FilterDecimal(bool isEnabled, decimal value) : base(isEnabled, value) { }
        public FilterDecimal(bool isEnabledEditable, bool isEnabled) : base(isEnabledEditable, isEnabled, 0) { }
    }

    public class FilterBool : Filter<bool>
    {
        public FilterBool() { }
        public FilterBool(bool isEnabled) : base(isEnabled) { }
        public FilterBool(bool isEnabled, bool value) : base(isEnabled, value) { }
    }

    public class FilterString : Filter<string>
    {
        public FilterString() { }
        public FilterString(bool isEnabled) : base(isEnabled) { }
        public FilterString(bool isEnabled, string value) : base(isEnabled, value) { }
        public FilterString(bool isEnabledEditable, bool isEnabled) : base(isEnabledEditable, isEnabled, 0) { }
    }

    public class FilterColor : Filter<Color>
    {
        public FilterColor() { }
        public FilterColor(bool isEnabled) : base(isEnabled) { }
        public FilterColor(bool isEnabled, Color value) : base(isEnabled, value) { }
        public FilterColor(bool isEnabledEditable, bool isEnabled) : base(isEnabledEditable, isEnabled, 0) { }

        public Color Color => Value;
    }

    public class FilterTimeSpan : Filter<TimeSpan>
    {
        public FilterTimeSpan() { }
        public FilterTimeSpan(bool isEnabled) : base(isEnabled) { }
        public FilterTimeSpan(bool isEnabled, TimeSpan value) : base(isEnabled, value) { }
        public FilterTimeSpan(bool isEnabledEditable, bool isEnabled) : base(isEnabledEditable, isEnabled, 0) { }
    }

    public class FilterKey : Filter<Avalonia.Input.Key>
    {
        public FilterKey() { }
        public FilterKey(bool isEnabled) : base(isEnabled) { }
        public FilterKey(bool isEnabled, Avalonia.Input.Key value) : base(isEnabled, value) { }
        public FilterKey(bool isEnabledEditable, bool isEnabled) : base(isEnabledEditable, isEnabled, 0) { }
    }

    /// <summary>Escalar para filtros en dinero (no se usa en esta plataforma: siempre volumen).</summary>
    public class MoneyScalar
    {
        public decimal Amount { get; set; }
        public bool IsEnabled { get; set; }
        public string Unit { get; set; } = "";

        public override string ToString() => Amount.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>En qué se expresa un filtro de volumen.</summary>
    public enum VolumeFilterUnit
    {
        [Display(Name = "Volume")] Volume,
        [Display(Name = "Money")] Money,
    }

    /// <summary>
    /// Filtro de volumen (umbral) en volumen o en dinero. Compare(volumen, precio) devuelve -1/0/1
    /// comparando contra el umbral.
    /// </summary>
    public class VolumeFilter : Filter<decimal>
    {
        public VolumeFilter() { }
        public VolumeFilter(bool isEnabled) : base(isEnabled) { }
        public VolumeFilter(bool isEnabled, decimal value) : base(isEnabled, value) { }
        public VolumeFilter(bool isEnabledEditable, bool isEnabled) : base(isEnabledEditable, isEnabled, 0) { }

        public new VolumeFilter ValueOnChanging(Func<ValueChangingArgs<decimal>, decimal> coerce)
        {
            base.ValueOnChanging(coerce);
            return this;
        }

        public new VolumeFilter ValueOnChanged(Action<decimal> handler)
        {
            base.ValueOnChanged(handler);
            return this;
        }

        [Browsable(false)]
        public VolumeFilterUnit Unit { get; set; } = VolumeFilterUnit.Volume;

        [Browsable(false)]
        public bool IsMoney
        {
            get => Unit == VolumeFilterUnit.Money;
            set => Unit = value ? VolumeFilterUnit.Money : VolumeFilterUnit.Volume;
        }

        /// <summary>Monto en dinero (cuando el filtro está en dinero).</summary>
        [Browsable(false)]
        public decimal Amount { get; set; }

        /// <summary>Monto en dinero como texto, tal como lo edita el usuario ("1000", "1.5k"…).</summary>
        [Browsable(false)]
        public string MoneyScalar
        {
            get => Amount == 0 ? null : Amount.ToString("0.########", CultureInfo.InvariantCulture);
            set => Amount = ParseAmount(value);
        }

        [Browsable(false)]
        public Valuation Valuation { get; set; }

        public decimal Volume => Value;

        public bool HasThreshold() => Enabled && (IsMoneyApplied() ? Amount != 0 : Value != 0);

        public bool IsMoneyApplied() => Unit == VolumeFilterUnit.Money && Amount != 0;

        /// <summary>Factor para pasar de volumen a dinero.</summary>
        public bool TryGetMoneyFactor(Valuation valuation, out VolumeValueFactor factor)
        {
            var v = valuation ?? Valuation;
            if (v != null && v.TryGetDisplayFactor(out factor)) return true;
            factor = new VolumeValueFactor(1m);
            return IsMoneyApplied();
        }

        /// <summary>Umbral expresado en volumen para un precio dado.</summary>
        public decimal GetVolumeThreshold(decimal price, Valuation valuation = null)
        {
            if (!IsMoneyApplied()) return Value;
            return TryGetMoneyFactor(valuation, out var f) ? f.ToVolume(Amount, price) : Value;
        }

        /// <summary>Compara un volumen (o su valor en dinero si el filtro es en dinero) contra el umbral.</summary>
        public int Compare(decimal volume, decimal price)
        {
            if (IsMoneyApplied()) return (volume * price).CompareTo(Amount);
            return volume.CompareTo(Value);
        }

        public int Compare(decimal volume, decimal price, Valuation valuation)
        {
            if (IsMoneyApplied() && TryGetMoneyFactor(valuation, out var f)) return f.ToMoney(volume, price).CompareTo(Amount);
            return volume.CompareTo(Value);
        }

        public int Compare(decimal volume) => volume.CompareTo(Value);

        public string FormatThreshold() => IsMoneyApplied() ? "$" + Amount.ToString("0.##", CultureInfo.InvariantCulture) : Value.ToString("0.########", CultureInfo.InvariantCulture);

        public VolumeFilter Clone() => new(Enabled, Value) { Unit = Unit, Amount = Amount };

        private static decimal ParseAmount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            text = text.Trim().Replace("$", "").Replace(" ", "");
            decimal mult = 1;
            char last = char.ToLowerInvariant(text[^1]);
            if (last is 'k' or 'm' or 'b')
            {
                mult = last == 'k' ? 1_000 : last == 'm' ? 1_000_000 : 1_000_000_000;
                text = text[..^1];
            }

            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v * mult : 0;
        }
    }
}
