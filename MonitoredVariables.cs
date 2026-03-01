using System;

namespace OwenModbusMonitor
{
    /// <summary>
    /// Класс-обертка для отслеживания изменений переменной типа float.
    /// </summary>
    public class MonitoredFloat
    {
        private float _value;
        public bool HasValue { get; private set; } = false;

        // Событие, которое срабатывает при изменении значения
        public event EventHandler<float>? ValueChanged;

        public float CurrentValue
        {
            get => _value;
            set
            {
                // Проверка на изменение с учетом погрешности float, чтобы не спамить событиями
                // Также обновляем, если это первое присвоение (!HasValue)
                if (!HasValue || Math.Abs(_value - value) > 0.0001f)
                {
                    HasValue = true;
                    _value = value;
                    ValueChanged?.Invoke(this, _value);
                }
            }
        }
    }

    /// <summary>
    /// Класс-обертка для отслеживания изменений переменной типа short.
    /// </summary>
    public class MonitoredShort
    {
        private short _value;
        public event EventHandler<short>? ValueChanged;

        public short CurrentValue
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    ValueChanged?.Invoke(this, _value);
                }
            }
        }
    }
}