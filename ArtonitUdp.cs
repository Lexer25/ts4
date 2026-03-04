using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace TS4
{
    public class ArtonitUdp
    {
        private readonly string ip;
        private readonly int port;
        public ArtonitUdp(string ip, int port)
        {
            this.ip = ip;
            this.port = port;
        }

        private static ushort Crc16Ccitt(byte[] data)
        {
            const ushort poly = 0x1021;
            ushort crc = 0xFFFF;
            foreach (var b in data)
            {
                byte rb = ReverseByte(b);
                crc ^= (ushort)(rb << 8);
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ poly); else crc <<= 1;
                }
            }
            crc ^= 0xFFFF;
            return ReverseBits(crc);
        }
        private static byte ReverseByte(byte b)
        {
            byte ob = 0;
            for (int i = 0; i < 8; i++) if (((b >> (7 - i)) & 1) != 0) ob |= (byte)(1 << i);
            return ob;
        }
        private static ushort ReverseBits(ushort v)
        {
            ushort ob = 0;
            for (int i = 0; i < 16; i++) if (((v >> (15 - i)) & 1) != 0) ob |= (ushort)(1 << i);
            return ob;
        }

        private static byte[] MakeBinaryCommand(string code)
        {
            Dictionary<string, byte[]> known = new Dictionary<string, byte[]>
            {
                {"GetVersion", new byte[]{0x03,0x00,0x00}},
                {"GetJmp", new byte[]{0x03,0x00,0x01}},
                {"GetMAC", new byte[]{0x03,0x01,0x00}},
                {"GetIO", new byte[]{0x21}},
                {"GetKeyCount", new byte[]{0x1b}},
            };
            var body = known[code];
            byte len = (byte)(body.Length - 1);
            var payload = new byte[1 + body.Length + 2];
            payload[0] = len;
            Buffer.BlockCopy(body, 0, payload, 1, body.Length);
            var crc = Crc16Ccitt(payload.AsSpan(0, 1 + body.Length).ToArray());
            payload[payload.Length - 2] = (byte)(crc & 0xFF);
            payload[payload.Length - 1] = (byte)((crc >> 8) & 0xFF);
            return payload;
        }

        public async Task<Dictionary<string, int>> GetKeyCountAsync()
        {
            var cmd = MakeBinaryCommand("GetKeyCount");
            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            {
                udp.Client.ReceiveTimeout = 1000;
                string ipTrim = (ip ?? string.Empty).Trim();
                IPAddress ipAddress;
                if (!IPAddress.TryParse(ipTrim, out ipAddress))
                {
                    var addresses = await Dns.GetHostAddressesAsync(ipTrim);
                    ipAddress = addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork);
                }
                udp.Connect(ipAddress, port);
                await udp.SendAsync(cmd, cmd.Length);
                try
                {
                    const int receiveTimeoutMs = 3000;
                    var receiveTask = udp.ReceiveAsync();
                    var delayTask = Task.Delay(receiveTimeoutMs);
                    if (await Task.WhenAny(receiveTask, delayTask) != receiveTask)
                        return new Dictionary<string, int>();
                    var res = await receiveTask;
                    var data = res.Buffer;
                    if (data.Length < 6) return new Dictionary<string, int>();
                    int headerLen = data[0];
                    int start = headerLen + 2;
                    int len = data.Length - headerLen - 4;
                    if (len <= 0) return new Dictionary<string, int>();
                    var body = new byte[len];
                    Buffer.BlockCopy(data, start, body, 0, len);
                    // reverse by bytes, then parse two 16-bit values
                    Array.Reverse(body);
                    // low 16 at index 0..1, high 16 at 2..3
                    int ch1 = (body[0] << 8) | body[1];
                    int ch0 = (body[2] << 8) | body[3];
                    return new Dictionary<string, int> { { "0", ch0 }, { "1", ch1 } };
                }
                catch
                {
                    return new Dictionary<string, int>();
                }
            }
        }
    }
}
