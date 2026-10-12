using System;
using System.Device.I2c;
using System.Diagnostics;
using System.Threading;

namespace nfaz3166iotcentral
{
    /// <summary>Register access helpers for the AZ3166 I2C sensors.</summary>
    internal static class Register
    {
        /// <summary>
        /// Logs every transaction (with a pause so the line gets out before the transaction runs).
        /// Used during start up to find a transaction that hangs the device.
        /// </summary>
        public static bool Trace = true;

        private static void Log(I2cDevice device, string operation, byte register, int length)
        {
            if (Trace)
            {
                Debug.WriteLine("  I2C 0x" + device.ConnectionSettings.DeviceAddress.ToString("X2") + " " + operation +
                    " reg 0x" + register.ToString("X2") + " len " + length.ToString());
                Thread.Sleep(200);
            }
        }

        private static void Result(I2cTransferResult result)
        {
            if (Trace)
            {
                Debug.WriteLine("    -> " + ((int)result.Status).ToString() + " (" + result.BytesTransferred.ToString() + " bytes)");
            }
        }

        public static int Read(I2cDevice device, byte register)
        {
            byte[] value = new byte[1];

            Log(device, "read", register, 1);
            var result = device.WriteRead(new SpanByte(new byte[] { register }), new SpanByte(value));
            Result(result);

            return result.Status == I2cTransferStatus.FullTransfer ? value[0] : -1;
        }

        public static bool Read(I2cDevice device, byte register, byte[] buffer)
        {
            Log(device, "read", register, buffer.Length);
            var result = device.WriteRead(new SpanByte(new byte[] { register }), new SpanByte(buffer));
            Result(result);

            return result.Status == I2cTransferStatus.FullTransfer;
        }

        public static bool Write(I2cDevice device, byte register, byte value)
        {
            Log(device, "write", register, 1);
            var result = device.Write(new SpanByte(new byte[] { register, value }));
            Result(result);

            return result.Status == I2cTransferStatus.FullTransfer;
        }

        public static short Int16(byte[] buffer, int offset)
        {
            return (short)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        public static I2cDevice Open(int address)
        {
            return I2cDevice.Create(new I2cConnectionSettings(Program.I2cBus, address, I2cBusSpeed.FastMode));
        }
    }

    /// <summary>HTS221 temperature and humidity sensor (0x5F).</summary>
    public class Hts221
    {
        private readonly I2cDevice _device;
        private readonly byte[] _data = new byte[4];
        private readonly float _t0, _t1, _h0, _h1;
        private readonly short _t0Out, _t1Out, _h0Out, _h1Out;

        public bool IsPresent { get; }
        public float Temperature { get; private set; }
        public float Humidity { get; private set; }

        public Hts221()
        {
            _device = Register.Open(0x5F);

            IsPresent = Register.Read(_device, 0x0F) == 0xBC;

            if (!IsPresent)
            {
                return;
            }

            // power on, block data update, 1 Hz
            Register.Write(_device, 0x20, 0x85);

            // calibration (0x80 = auto increment)
            byte[] c = new byte[16];
            Register.Read(_device, 0x30 | 0x80, c);

            _h0 = c[0] / 2f;
            _h1 = c[1] / 2f;
            _t0 = (c[2] | ((c[5] & 0x03) << 8)) / 8f;
            _t1 = (c[3] | ((c[5] & 0x0C) << 6)) / 8f;
            _h0Out = Register.Int16(c, 6);
            _h1Out = Register.Int16(c, 10);
            _t0Out = Register.Int16(c, 12);
            _t1Out = Register.Int16(c, 14);
        }

        public bool Read()
        {
            if (!IsPresent || !Register.Read(_device, 0x28 | 0x80, _data))
            {
                return false;
            }

            short hOut = Register.Int16(_data, 0);
            short tOut = Register.Int16(_data, 2);

            if (_t1Out != _t0Out)
            {
                Temperature = _t0 + (tOut - _t0Out) * (_t1 - _t0) / (_t1Out - _t0Out);
            }

            if (_h1Out != _h0Out)
            {
                float h = _h0 + (hOut - _h0Out) * (_h1 - _h0) / (_h1Out - _h0Out);
                Humidity = h < 0 ? 0 : (h > 100 ? 100 : h);
            }

            return true;
        }
    }

    /// <summary>LPS22HB pressure sensor (0x5C).</summary>
    public class Lps22hb
    {
        private readonly I2cDevice _device;
        private readonly byte[] _data = new byte[5];

        public bool IsPresent { get; }
        public float Pressure { get; private set; }
        public float Temperature { get; private set; }

        public Lps22hb()
        {
            _device = Register.Open(0x5C);

            IsPresent = Register.Read(_device, 0x0F) == 0xB1;

            if (IsPresent)
            {
                // 1 Hz, block data update
                Register.Write(_device, 0x10, 0x12);
            }
        }

        public bool Read()
        {
            if (!IsPresent || !Register.Read(_device, 0x28, _data))
            {
                return false;
            }

            int raw = _data[0] | (_data[1] << 8) | (_data[2] << 16);

            if ((raw & 0x800000) != 0)
            {
                raw |= unchecked((int)0xFF000000);
            }

            Pressure = raw / 4096f;
            Temperature = Register.Int16(_data, 3) / 100f;

            return true;
        }
    }

    /// <summary>LSM6DSL accelerometer and gyroscope (0x6A).</summary>
    public class Lsm6dsl
    {
        private readonly I2cDevice _device;
        private readonly byte[] _data = new byte[12];

        public bool IsPresent { get; }

        // g
        public float AccelX { get; private set; }
        public float AccelY { get; private set; }
        public float AccelZ { get; private set; }

        // degrees per second
        public float GyroX { get; private set; }
        public float GyroY { get; private set; }
        public float GyroZ { get; private set; }

        public Lsm6dsl()
        {
            _device = Register.Open(0x6A);

            IsPresent = Register.Read(_device, 0x0F) == 0x6A;

            if (IsPresent)
            {
                Register.Write(_device, 0x10, 0x40); // accelerometer 104 Hz, +/-2 g
                Register.Write(_device, 0x11, 0x40); // gyroscope 104 Hz, 250 dps
                Register.Write(_device, 0x12, 0x44); // block data update, auto increment
            }
        }

        public bool Read()
        {
            if (!IsPresent || !Register.Read(_device, 0x22, _data))
            {
                return false;
            }

            GyroX = Register.Int16(_data, 0) * 0.00875f;
            GyroY = Register.Int16(_data, 2) * 0.00875f;
            GyroZ = Register.Int16(_data, 4) * 0.00875f;
            AccelX = Register.Int16(_data, 6) * 0.000061f;
            AccelY = Register.Int16(_data, 8) * 0.000061f;
            AccelZ = Register.Int16(_data, 10) * 0.000061f;

            return true;
        }
    }

    /// <summary>LIS2MDL magnetometer (0x1E).</summary>
    public class Lis2mdl
    {
        private readonly I2cDevice _device;
        private readonly byte[] _data = new byte[6];

        public bool IsPresent { get; }

        // milligauss
        public float X { get; private set; }
        public float Y { get; private set; }
        public float Z { get; private set; }

        /// <summary>Heading in degrees (0 = magnetic north), assuming the board lies flat.</summary>
        public float Heading { get; private set; }

        public Lis2mdl()
        {
            _device = Register.Open(0x1E);

            IsPresent = Register.Read(_device, 0x4F) == 0x40;

            if (IsPresent)
            {
                Register.Write(_device, 0x60, 0x80); // temperature compensation, 10 Hz, continuous
                Register.Write(_device, 0x62, 0x10); // block data update
            }
        }

        public bool Read()
        {
            if (!IsPresent || !Register.Read(_device, 0x68, _data))
            {
                return false;
            }

            X = Register.Int16(_data, 0) * 1.5f;
            Y = Register.Int16(_data, 2) * 1.5f;
            Z = Register.Int16(_data, 4) * 1.5f;

            double heading = Math.Atan2(Y, X) * 180.0 / Math.PI;

            if (heading < 0)
            {
                heading += 360;
            }

            Heading = (float)heading;

            return true;
        }
    }
}
