using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace TS4
{
    public class ArtonitHttp
    {
        private readonly string ip;
        private readonly HttpClient http;
        public bool IsOnline { get; private set; }
        public bool IsWp { get; private set; }
        public bool IsTest { get; private set; }
        public string[] DoorMode { get; private set; } = new string[] { "no", "no" };
        public List<string> PortStateInput { get; private set; } = new List<string>();
        public string SoftVersion { get; private set; } = "n/a";
        public string Scud { get; private set; } = "d0";
        public string Mac { get; private set; } = "00-00-00-00-00-00";

        public ArtonitHttp(string ip)
        {
            this.ip = ip;
            this.http = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
        }

        private async Task<string?> GetAsync(string page)
        {
            try
            {
                var url = $"http://{ip}/{page}";
                return await http.GetStringAsync(url);
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> CheckOnline()
        {
            var html = await GetAsync("1.htm");
            IsOnline = !string.IsNullOrEmpty(html);
            return IsOnline;
        }

        public async Task GetDeviceInfo()
        {
            if (!IsOnline) return;
            await GetDoorMode();
            await GetSoftVersion();
            await GetDeviceMode();
            await GetInputPortState();
            await GetScudMode();
        }

        private static bool Contains(string? s, string needle) => !string.IsNullOrEmpty(s) && s.Contains(needle, StringComparison.OrdinalIgnoreCase);
        private static string ExtractBetween(string? s, string from, int shift, string to)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var i = s.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return string.Empty;
            i += shift;
            var j = s.IndexOf(to, i, StringComparison.OrdinalIgnoreCase);
            if (j < 0 || j <= i) return string.Empty;
            return s.Substring(i, j - i);
        }

        public async Task GetInputPortState()
        {
            var html = await GetAsync("E.htm");
            var state = new List<string>();
            for (int i = 1; i <= 8; i++)
            {
                state.Add(ExtractBetween(html, $"<tr><td>IN{i}", 34, "</td>").Trim());
            }
            PortStateInput = state;
        }

        public async Task GetDoorMode()
        {
            var xml = await GetAsync("8.xml");
            if (!string.IsNullOrEmpty(xml))
            {
                DoorMode[0] = ExtractBetween(xml, "<A>", 3, "</A>");
                DoorMode[1] = ExtractBetween(xml, "<B>", 3, "</B>");
            }
        }

        public async Task GetDeviceMode()
        {
            var html = await GetAsync("1.htm");
            if (html == null) return;
            IsWp = Contains(html, "checked><b> Jumper \"WP\"");
            IsTest = Contains(html, "checked><b> Jumper \"TEST\"");
            var mac = ExtractBetween(html, "00-20-A6", 0, "</b>");
            if (!string.IsNullOrWhiteSpace(mac)) Mac = mac.Trim();
        }

        public async Task GetScudMode()
        {
            var html = await GetAsync("F.htm");
            if (html == null) return;
            if (Contains(html, "name=\"S0f7F1\" value=\"Y\" checked")) Scud = "d1";
            if (Contains(html, "name=\"S0f7F1\" value=\"N\" checked")) Scud = "d2";
        }

        public async Task GetSoftVersion()
        {
            var html = await GetAsync("A.htm");
            if (html == null) return;
            var from = "www.artonit.ru</a> ";
            var ver = ExtractBetween(html, from, from.Length, "</TD></TR>");
            if (!string.IsNullOrWhiteSpace(ver))
            {
                SoftVersion = ver.Replace("|", "").Trim();
            }
        }
    }
}

