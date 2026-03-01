using FluentModbus;
using System;
using System.Net;
using System.Threading.Tasks;

namespace OwenModbusMonitor
{
    public class ModbusService(string ipAddress, int port = 502) : IDisposable
    {
        private ModbusTcpClient _client = new ModbusTcpClient();
        private string _ipAddress = ipAddress;
        private int _port = port;

        public bool IsConnected => _client != null && _client.IsConnected;

        public void Connect()
        {
            if (!IsConnected)
                _client.Connect(new IPEndPoint(IPAddress.Parse(_ipAddress), _port), ModbusEndianness.BigEndian);
        }

        public void Disconnect()
        {
            if (_client != null) _client.Disconnect();
        }

        // Чтение целого числа (1 регистр)
        public async Task<short> ReadShortAsync(int unitId, int address)
        {
            if (!IsConnected) throw new Exception("Нет соединения");
            var data = await _client.ReadHoldingRegistersAsync<short>(unitId, address, 1);
            return data.ToArray()[0];
        }

        // Чтение дробного числа (2 регистра = 32 бита)
        public async Task<float> ReadFloatAsync(int unitId, int address)
        {
            if (!IsConnected) throw new Exception("Нет соединения");
            // Читаем как float (библиотека сама склеит 2 регистра)
            // ВАЖНО: Если значение "странное" (очень большое/маленькое), возможно нужно менять Endianness в настройках Connect
            var data = await _client.ReadHoldingRegistersAsync<float>(unitId, address, 1);
            return data.ToArray()[0];
        }

        // Запись целого числа (short)
        public async Task WriteShortAsync(int unitId, int address, short value)
        {
            if (!IsConnected) throw new Exception("Нет соединения");
            // Используем WriteSingleRegister для одного регистра
            await _client.WriteSingleRegisterAsync(unitId, address, value);
        }

        // Запись дробного числа (float)
        public async Task WriteFloatAsync(int unitId, int address, float value)
        {
            if (!IsConnected) throw new Exception("Нет соединения");
            // Float занимает 2 регистра, поэтому используем WriteMultipleRegisters
            // Библиотека сама разобьет float на 2 части (слова)
            await _client.WriteMultipleRegistersAsync(unitId, address, new float[] { value });
        }

        // Чтение блока регистров (для оптимизации)
        // Возвращает массив short, который затем можно разобрать
        public async Task<short[]> ReadBlockAsync(int unitId, int startAddress, int length)
        {
            if (!IsConnected) throw new Exception("Нет соединения");
            var data = await _client.ReadHoldingRegistersAsync<short>(unitId, startAddress, length);
            return data.ToArray();
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
