using System;
using System.Device.I2c;

namespace nfaz3166iothub
{
    /// <summary>
    /// SSD1306 128x64 OLED (the AZ3166 display, I2C address 0x3C) with a frame buffer and simple drawing primitives.
    /// Draw into the buffer, then call <see cref="Show"/>.
    /// </summary>
    public class Ssd1306
    {
        public const int Width = 128;
        public const int Height = 64;

        // set to true if the image is upside down on your board
        private const bool Rotate180 = false;

        private readonly I2cDevice _device;
        private readonly byte[] _buffer = new byte[Width * Height / 8];
        private readonly byte[] _pageData = new byte[Width + 1];

        public bool IsPresent { get; private set; }

        public Ssd1306(int busId, int address = 0x3C)
        {
            _device = I2cDevice.Create(new I2cConnectionSettings(busId, address, I2cBusSpeed.FastMode));

            IsPresent = Command(new byte[] { 0xAE }); // display off

            if (!IsPresent)
            {
                return;
            }

            Command(new byte[]
            {
                0xD5, 0x80,             // clock divide
                0xA8, 0x3F,             // multiplex 64
                0xD3, 0x00,             // display offset
                0x40,                   // start line 0
                0x8D, 0x14,             // charge pump on
                0x20, 0x00,             // horizontal addressing
                (byte)(Rotate180 ? 0xA0 : 0xA1), // segment remap
                (byte)(Rotate180 ? 0xC0 : 0xC8), // COM scan direction
                0xDA, 0x12,             // COM pins
                0x81, 0xCF,             // contrast
                0xD9, 0xF1,             // pre-charge
                0xDB, 0x40,             // VCOMH
                0xA4,                   // display from RAM
                0xA6,                   // normal (not inverted)
                0xAF                    // display on
            });

            Clear();
            Show();
        }

        private bool Command(byte[] commands)
        {
            byte[] data = new byte[commands.Length + 1];
            data[0] = 0x00;
            Array.Copy(commands, 0, data, 1, commands.Length);

            return _device.Write(new SpanByte(data)).Status == I2cTransferStatus.FullTransfer;
        }

        /// <summary>Sends the frame buffer to the display.</summary>
        public void Show()
        {
            if (!IsPresent)
            {
                return;
            }

            Command(new byte[] { 0x21, 0, Width - 1, 0x22, 0, (Height / 8) - 1 });

            _pageData[0] = 0x40;

            for (int page = 0; page < Height / 8; page++)
            {
                Array.Copy(_buffer, page * Width, _pageData, 1, Width);
                _device.Write(new SpanByte(_pageData));
            }
        }

        public void SetContrast(byte contrast)
        {
            Command(new byte[] { 0x81, contrast });
        }

        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public void SetPixel(int x, int y, bool on = true)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return;
            }

            int index = x + (y / 8) * Width;
            byte mask = (byte)(1 << (y & 7));

            if (on)
            {
                _buffer[index] |= mask;
            }
            else
            {
                _buffer[index] &= (byte)~mask;
            }
        }

        public void FillRect(int x, int y, int w, int h, bool on = true)
        {
            for (int i = x; i < x + w; i++)
            {
                for (int j = y; j < y + h; j++)
                {
                    SetPixel(i, j, on);
                }
            }
        }

        public void DrawRect(int x, int y, int w, int h, bool on = true)
        {
            DrawLine(x, y, x + w - 1, y, on);
            DrawLine(x, y + h - 1, x + w - 1, y + h - 1, on);
            DrawLine(x, y, x, y + h - 1, on);
            DrawLine(x + w - 1, y, x + w - 1, y + h - 1, on);
        }

        public void DrawLine(int x0, int y0, int x1, int y1, bool on = true)
        {
            int dx = Math.Abs(x1 - x0);
            int sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0);
            int sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                SetPixel(x0, y0, on);

                if (x0 == x1 && y0 == y1)
                {
                    break;
                }

                int e2 = 2 * err;

                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }

                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        public void DrawCircle(int cx, int cy, int r, bool on = true)
        {
            int x = r;
            int y = 0;
            int err = 1 - r;

            while (x >= y)
            {
                SetPixel(cx + x, cy + y, on);
                SetPixel(cx + y, cy + x, on);
                SetPixel(cx - y, cy + x, on);
                SetPixel(cx - x, cy + y, on);
                SetPixel(cx - x, cy - y, on);
                SetPixel(cx - y, cy - x, on);
                SetPixel(cx + y, cy - x, on);
                SetPixel(cx + x, cy - y, on);

                y++;

                if (err < 0)
                {
                    err += 2 * y + 1;
                }
                else
                {
                    x--;
                    err += 2 * (y - x) + 1;
                }
            }
        }

        public void FillCircle(int cx, int cy, int r, bool on = true)
        {
            for (int y = -r; y <= r; y++)
            {
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y <= r * r)
                    {
                        SetPixel(cx + x, cy + y, on);
                    }
                }
            }
        }

        /// <summary>Width in pixels of a text drawn at the given scale.</summary>
        public static int TextWidth(string text, int scale = 1)
        {
            return text.Length == 0 ? 0 : (text.Length * 6 - 1) * scale;
        }

        /// <summary>Draws text (5x7 font, 1 pixel spacing). Returns the x coordinate after the text.</summary>
        public int DrawText(int x, int y, string text, int scale = 1, bool on = true)
        {
            for (int i = 0; i < text.Length; i++)
            {
                DrawChar(x, y, text[i], scale, on);
                x += 6 * scale;
            }

            return x;
        }

        public void DrawTextCentered(int y, string text, int scale = 1, bool on = true)
        {
            DrawText((Width - TextWidth(text, scale)) / 2, y, text, scale, on);
        }

        private void DrawChar(int x, int y, char c, int scale, bool on)
        {
            int code = c;

            if (code == '°')
            {
                code = 127;
            }

            if (code < 32 || code > 127)
            {
                code = '?';
            }

            int offset = (code - 32) * 5;

            for (int column = 0; column < 5; column++)
            {
                byte bits = Font5x7[offset + column];

                for (int row = 0; row < 7; row++)
                {
                    if ((bits & (1 << row)) != 0)
                    {
                        if (scale == 1)
                        {
                            SetPixel(x + column, y + row, on);
                        }
                        else
                        {
                            FillRect(x + column * scale, y + row * scale, scale, scale, on);
                        }
                    }
                }
            }
        }

        // classic 5x7 font, ASCII 32..126, plus a degree sign at 127 (columns, LSB at the top)
        private static readonly byte[] Font5x7 = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, 0x00, // ' '
            0x00, 0x00, 0x5F, 0x00, 0x00, // !
            0x00, 0x07, 0x00, 0x07, 0x00, // "
            0x14, 0x7F, 0x14, 0x7F, 0x14, // #
            0x24, 0x2A, 0x7F, 0x2A, 0x12, // $
            0x23, 0x13, 0x08, 0x64, 0x62, // %
            0x36, 0x49, 0x55, 0x22, 0x50, // &
            0x00, 0x05, 0x03, 0x00, 0x00, // '
            0x00, 0x1C, 0x22, 0x41, 0x00, // (
            0x00, 0x41, 0x22, 0x1C, 0x00, // )
            0x08, 0x2A, 0x1C, 0x2A, 0x08, // *
            0x08, 0x08, 0x3E, 0x08, 0x08, // +
            0x00, 0x50, 0x30, 0x00, 0x00, // ,
            0x08, 0x08, 0x08, 0x08, 0x08, // -
            0x00, 0x60, 0x60, 0x00, 0x00, // .
            0x20, 0x10, 0x08, 0x04, 0x02, // /
            0x3E, 0x51, 0x49, 0x45, 0x3E, // 0
            0x00, 0x42, 0x7F, 0x40, 0x00, // 1
            0x42, 0x61, 0x51, 0x49, 0x46, // 2
            0x21, 0x41, 0x45, 0x4B, 0x31, // 3
            0x18, 0x14, 0x12, 0x7F, 0x10, // 4
            0x27, 0x45, 0x45, 0x45, 0x39, // 5
            0x3C, 0x4A, 0x49, 0x49, 0x30, // 6
            0x01, 0x71, 0x09, 0x05, 0x03, // 7
            0x36, 0x49, 0x49, 0x49, 0x36, // 8
            0x06, 0x49, 0x49, 0x29, 0x1E, // 9
            0x00, 0x36, 0x36, 0x00, 0x00, // :
            0x00, 0x56, 0x36, 0x00, 0x00, // ;
            0x08, 0x14, 0x22, 0x41, 0x00, // <
            0x14, 0x14, 0x14, 0x14, 0x14, // =
            0x00, 0x41, 0x22, 0x14, 0x08, // >
            0x02, 0x01, 0x51, 0x09, 0x06, // ?
            0x32, 0x49, 0x79, 0x41, 0x3E, // @
            0x7E, 0x11, 0x11, 0x11, 0x7E, // A
            0x7F, 0x49, 0x49, 0x49, 0x36, // B
            0x3E, 0x41, 0x41, 0x41, 0x22, // C
            0x7F, 0x41, 0x41, 0x22, 0x1C, // D
            0x7F, 0x49, 0x49, 0x49, 0x41, // E
            0x7F, 0x09, 0x09, 0x09, 0x01, // F
            0x3E, 0x41, 0x49, 0x49, 0x7A, // G
            0x7F, 0x08, 0x08, 0x08, 0x7F, // H
            0x00, 0x41, 0x7F, 0x41, 0x00, // I
            0x20, 0x40, 0x41, 0x3F, 0x01, // J
            0x7F, 0x08, 0x14, 0x22, 0x41, // K
            0x7F, 0x40, 0x40, 0x40, 0x40, // L
            0x7F, 0x02, 0x0C, 0x02, 0x7F, // M
            0x7F, 0x04, 0x08, 0x10, 0x7F, // N
            0x3E, 0x41, 0x41, 0x41, 0x3E, // O
            0x7F, 0x09, 0x09, 0x09, 0x06, // P
            0x3E, 0x41, 0x51, 0x21, 0x5E, // Q
            0x7F, 0x09, 0x19, 0x29, 0x46, // R
            0x46, 0x49, 0x49, 0x49, 0x31, // S
            0x01, 0x01, 0x7F, 0x01, 0x01, // T
            0x3F, 0x40, 0x40, 0x40, 0x3F, // U
            0x1F, 0x20, 0x40, 0x20, 0x1F, // V
            0x3F, 0x40, 0x38, 0x40, 0x3F, // W
            0x63, 0x14, 0x08, 0x14, 0x63, // X
            0x07, 0x08, 0x70, 0x08, 0x07, // Y
            0x61, 0x51, 0x49, 0x45, 0x43, // Z
            0x00, 0x7F, 0x41, 0x41, 0x00, // [
            0x02, 0x04, 0x08, 0x10, 0x20, // backslash
            0x00, 0x41, 0x41, 0x7F, 0x00, // ]
            0x04, 0x02, 0x01, 0x02, 0x04, // ^
            0x40, 0x40, 0x40, 0x40, 0x40, // _
            0x00, 0x01, 0x02, 0x04, 0x00, // `
            0x20, 0x54, 0x54, 0x54, 0x78, // a
            0x7F, 0x48, 0x44, 0x44, 0x38, // b
            0x38, 0x44, 0x44, 0x44, 0x20, // c
            0x38, 0x44, 0x44, 0x48, 0x7F, // d
            0x38, 0x54, 0x54, 0x54, 0x18, // e
            0x08, 0x7E, 0x09, 0x01, 0x02, // f
            0x0C, 0x52, 0x52, 0x52, 0x3E, // g
            0x7F, 0x08, 0x04, 0x04, 0x78, // h
            0x00, 0x44, 0x7D, 0x40, 0x00, // i
            0x20, 0x40, 0x44, 0x3D, 0x00, // j
            0x7F, 0x10, 0x28, 0x44, 0x00, // k
            0x00, 0x41, 0x7F, 0x40, 0x00, // l
            0x7C, 0x04, 0x18, 0x04, 0x78, // m
            0x7C, 0x08, 0x04, 0x04, 0x78, // n
            0x38, 0x44, 0x44, 0x44, 0x38, // o
            0x7C, 0x14, 0x14, 0x14, 0x08, // p
            0x08, 0x14, 0x14, 0x18, 0x7C, // q
            0x7C, 0x08, 0x04, 0x04, 0x08, // r
            0x48, 0x54, 0x54, 0x54, 0x20, // s
            0x04, 0x3F, 0x44, 0x40, 0x20, // t
            0x3C, 0x40, 0x40, 0x20, 0x7C, // u
            0x1C, 0x20, 0x40, 0x20, 0x1C, // v
            0x3C, 0x40, 0x30, 0x40, 0x3C, // w
            0x44, 0x28, 0x10, 0x28, 0x44, // x
            0x0C, 0x50, 0x50, 0x50, 0x3C, // y
            0x44, 0x64, 0x54, 0x4C, 0x44, // z
            0x00, 0x08, 0x36, 0x41, 0x00, // {
            0x00, 0x00, 0x7F, 0x00, 0x00, // |
            0x00, 0x41, 0x36, 0x08, 0x00, // }
            0x08, 0x04, 0x08, 0x10, 0x08, // ~
            0x00, 0x06, 0x09, 0x09, 0x06, // degree sign (127)
        };
    }
}
