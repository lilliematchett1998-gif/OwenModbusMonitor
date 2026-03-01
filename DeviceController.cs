using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;

namespace OwenModbusMonitor
{
    public class DeviceController : IDisposable
    {
        private readonly ModbusService _modbusService;
        public int GoodCount { get; private set; } = 0;
        public int FailCount { get; private set; } = 0;
        public int LogCount { get; private set; } = 0;
        public DateTime? ConnectionLostTime { get; private set; }
        private const string CountersFileName = "counters.txt";
        private const string ErrorLogFileName = "errors.log";
        private const string HistoryFileName = "history.csv";
        private DateTime _lastHistoryLog = DateTime.MinValue;

        public DeviceController(string ip, int port)
        {
            _modbusService = new ModbusService(ip, port);
            LoadCounters();
            if (File.Exists(ErrorLogFileName)) LogCount = File.ReadLines(ErrorLogFileName).Count();

            // Увеличиваем счетчик, когда Success меняется на 1
            Success.ValueChanged += (s, val) => 
            { 
                if (val == 1) 
                {
                    GoodCount++; 
                    SaveCounters();
                }
            };
            Fail.ValueChanged += (s, val) => 
            { 
                if (val == 1) 
                {
                    FailCount++; 
                    SaveCounters();
                    LogError("Изделие не годно");
                }
            };

            // Подписка на события ошибок для логирования
            Utechka.ValueChanged += (s, val) => { if (val == 1) LogError("Обнаружена утечка"); };
            OverPress.ValueChanged += (s, val) => { if (val == 1) LogError("Превышение давления"); };
            ErrDD.ValueChanged += (s, val) => { if (val == 1) LogError("Ошибка датчика давления"); };
            ErrSetpoint.ValueChanged += (s, val) => { if (val == 1) LogError("Ошибка выхода на уставку"); };
        }

        private CancellationTokenSource? _cts;
        private bool _isWriting = false;

        // --- КАРТА РЕГИСТРОВ ---
        private const int Addr_Start = 16384;
        private const int Addr_Stop = 16385;
        private const int Addr_Ustavka = 16386;
        private const int Addr_Davlenie = 16388;
        private const int Addr_SetpointReached = 16390;
        private const int Addr_Utechka = 16391;
        private const int Addr_OverPress = 16392;
        private const int Addr_ErrDD = 16393;
        private const int Addr_ErrSetpoint = 16394;
        private const int Addr_Success = 16395;
        private const int Addr_Fail = 16396;

        private const int UnitId = 1;

        // --- ПЕРЕМЕННЫЕ ---
        // Аналоговые (Float)
        public MonitoredFloat Ustavka { get; } = new MonitoredFloat();
        public MonitoredFloat Davlenie { get; } = new MonitoredFloat();

        // Дискретные (Short)
        public MonitoredShort StartVar { get; } = new MonitoredShort();
        public MonitoredShort StopVar { get; } = new MonitoredShort();
        public MonitoredShort SetpointReached { get; } = new MonitoredShort();
        public MonitoredShort Utechka { get; } = new MonitoredShort();
        public MonitoredShort OverPress { get; } = new MonitoredShort();
        public MonitoredShort ErrDD { get; } = new MonitoredShort();
        public MonitoredShort ErrSetpoint { get; } = new MonitoredShort();
        public MonitoredShort Success { get; } = new MonitoredShort();
        public MonitoredShort Fail { get; } = new MonitoredShort();

        public bool IsConnected => _modbusService.IsConnected;

        public void Connect() => _modbusService.Connect();
        public void Disconnect() => _modbusService.Disconnect();

        public void StartMonitoring()
        {
            if (_cts != null && !_cts.IsCancellationRequested) return;

            _cts = new CancellationTokenSource();
            // Запускаем цикл опроса в фоновом потоке
            Task.Run(() => PollLoop(_cts.Token));
        }

        public void StopMonitoring()
        {
            _cts?.Cancel();
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_isWriting)
                {
                    await Task.Delay(200, token);
                    continue;
                }

                if (!_modbusService.IsConnected)
                {
                    if (ConnectionLostTime == null) ConnectionLostTime = DateTime.Now;
                    try { _modbusService.Connect(); ConnectionLostTime = null; }
                    catch { await Task.Delay(1000, token); continue; }
                }

                try
                {
                    // Чтение блока данных (13 регистров) для оптимизации
                    // Читаем всё сразу от Addr_Start до Addr_Fail одним запросом
                    var data = await _modbusService.ReadBlockAsync(UnitId, Addr_Start, 13);
                    ConnectionLostTime = null;

                    // Разбор данных (индексы смещены относительно Addr_Start)
                    StartVar.CurrentValue = data[Addr_Start - Addr_Start];
                    StopVar.CurrentValue = data[Addr_Stop - Addr_Start];

                    // Float занимает 2 регистра, используем вспомогательный метод
                    Ustavka.CurrentValue = ParseFloat(data, Addr_Ustavka - Addr_Start);
                    Davlenie.CurrentValue = ParseFloat(data, Addr_Davlenie - Addr_Start);

                    SetpointReached.CurrentValue = data[Addr_SetpointReached - Addr_Start];
                    Utechka.CurrentValue = data[Addr_Utechka - Addr_Start];
                    OverPress.CurrentValue = data[Addr_OverPress - Addr_Start];
                    ErrDD.CurrentValue = data[Addr_ErrDD - Addr_Start];
                    ErrSetpoint.CurrentValue = data[Addr_ErrSetpoint - Addr_Start];
                    Success.CurrentValue = data[Addr_Success - Addr_Start];
                    Fail.CurrentValue = data[Addr_Fail - Addr_Start];

                    // Логирование истории в файл (каждые 500 мс)
                    if ((DateTime.Now - _lastHistoryLog).TotalMilliseconds >= 500)
                    {
                        LogHistory(Davlenie.CurrentValue, Ustavka.CurrentValue);
                        _lastHistoryLog = DateTime.Now;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Ошибка Modbus: {ex.Message}");
                    _modbusService.Disconnect();
                    if (ConnectionLostTime == null) ConnectionLostTime = DateTime.Now;
                }

                await Task.Delay(100, token);
            }
        }

        private float ParseFloat(short[] registers, int offset)
        {
            // Получаем два слова (регистра)
            short highWord = registers[offset];
            short lowWord = registers[offset + 1];

            // Конвертируем слова в байты
            byte[] lowBytes = BitConverter.GetBytes(lowWord);
            byte[] highBytes = BitConverter.GetBytes(highWord);

            // Собираем float: для Little Endian (PC) порядок байт: [LowWord_Low, LowWord_High, HighWord_Low, HighWord_High]
            // Это соответствует перестановке слов (Word Swap), стандартной для Modbus Float
            byte[] floatBytes = { lowBytes[0], lowBytes[1], highBytes[0], highBytes[1] };

            return BitConverter.ToSingle(floatBytes, 0);
        }

        public async Task WriteStartAsync()
        {
            _isWriting = true;
            try
            {
                await _modbusService.WriteShortAsync(UnitId, Addr_Start, 1);
                await _modbusService.WriteShortAsync(UnitId, Addr_Stop, 0);
            }
            finally { _isWriting = false; }
        }

        public async Task WriteStopAsync()
        {
            _isWriting = true;
            try
            {
                await _modbusService.WriteShortAsync(UnitId, Addr_Stop, 1);
                await _modbusService.WriteShortAsync(UnitId, Addr_Start, 0);
            }
            finally { _isWriting = false; }
        }

        public async Task WriteUstavkaAsync(float value)
        {
            _isWriting = true;
            try
            {
                await _modbusService.WriteFloatAsync(UnitId, Addr_Ustavka, value);
            }
            finally { _isWriting = false; }
        }

        public async Task ResetStatusAsync()
        {
            GoodCount = 0; // Сбрасываем счетчик
            FailCount = 0;
            SaveCounters();
            _isWriting = true;
            try
            {
                await _modbusService.WriteShortAsync(UnitId, Addr_Start, 0);
                await _modbusService.WriteShortAsync(UnitId, Addr_Stop, 0);
            }
            finally { _isWriting = false; }
        }

        public async Task ResetAllAsync()
        {
            if (_modbusService.IsConnected)
            {
                await _modbusService.WriteShortAsync(UnitId, Addr_Start, 0);
                await _modbusService.WriteShortAsync(UnitId, Addr_Stop, 0);
                await _modbusService.WriteFloatAsync(UnitId, Addr_Ustavka, 0);
                // Дополнительные сбросы можно добавить здесь
            }
        }

        private void LoadCounters()
        {
            try
            {
                if (File.Exists(CountersFileName))
                {
                    var lines = File.ReadAllLines(CountersFileName);
                    if (lines.Length >= 2)
                    {
                        if (int.TryParse(lines[0], out int good)) GoodCount = good;
                        if (int.TryParse(lines[1], out int fail)) FailCount = fail;
                    }
                }
            }
            catch { /* Игнорируем ошибки чтения */ }
        }

        private void SaveCounters()
        {
            try
            {
                File.WriteAllLines(CountersFileName, new[] { GoodCount.ToString(), FailCount.ToString() });
            }
            catch { /* Игнорируем ошибки записи */ }
        }

        private void LogError(string message)
        {
            try
            {
                string logEntry = $"{DateTime.Now}: {message}";
                File.AppendAllText(ErrorLogFileName, logEntry + Environment.NewLine);
                LogCount++;
            }
            catch { /* Игнорируем ошибки записи лога */ }
        }

        private void LogHistory(float pressure, float setpoint)
        {
            try
            {
                string line = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss};{pressure};{setpoint}";
                if (!File.Exists(HistoryFileName))
                {
                    // Добавляем BOM (\uFEFF) для корректного открытия кириллицы в Excel
                    File.WriteAllText(HistoryFileName, "\uFEFFВремя;Давление;Уставка" + Environment.NewLine + line + Environment.NewLine);
                }
                else
                {
                    File.AppendAllText(HistoryFileName, line + Environment.NewLine);
                }
            }
            catch { /* Игнорируем ошибки доступа к файлу (например, если он открыт) */ }
        }

        public void ClearHistory()
        {
            try { if (File.Exists(HistoryFileName)) File.Delete(HistoryFileName); } catch { }
        }

        public void ClearLogs()
        {
            try
            {
                File.WriteAllText(ErrorLogFileName, string.Empty);
                LogCount = 0;
            }
            catch { /* Игнорируем ошибки */ }
        }

        public void Dispose()
        {
            StopMonitoring();
            _modbusService?.Dispose();
        }
    }
}