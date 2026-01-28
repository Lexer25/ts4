using System.Collections.Generic;

namespace TS4
{
    public class DeviceState
    {
        public int id { get; set; }
        public string ip { get; set; } = "";
        public string mac { get; set; } = "00-00-00-00-00-00";
        public bool Onl { get; set; }
        public bool isWp { get; set; }
        public bool isTst { get; set; }
        public string dMode_0 { get; set; } = "";
        public string dMode_1 { get; set; } = "";
        public List<string> InputPortState { get; set; } = new List<string>();
        public string sver { get; set; } = "n/a";
        public Dictionary<string, int> kc { get; set; } = new Dictionary<string, int>();
        public string scud { get; set; } = "d0";
        public string te { get; set; } = "0";
        public long timef { get; set; }
        public bool isNew { get; set; }
        public long lastTimef { get; set; }
    }
}