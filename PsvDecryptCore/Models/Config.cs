using System;
using System.Collections.Generic;
using System.Text;

namespace PsvDecryptCore.Models
{
    public class Config
    {
        public Subtitle Subtitle { get; set; }
    }

    public class Subtitle
    {
        public bool HasSuffix { get; set; } = false;
        public string Suffix { get; set; } = ".en";
    }
}
