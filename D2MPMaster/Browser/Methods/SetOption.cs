﻿using System.Text.RegularExpressions;
using d2mpserver;

namespace D2MPMaster.Browser.Methods
{
    public class SetName
    {
        public string name { get; set; }

        public string Validate()
        {
            name = Regex.Replace(name, "^[\\w \\.\"'[]\\{\\}\\(\\)]+", "");
            if (name.Length > 40)
            {
                name = name.Substring(0, 40);
            }
            return name.Length < 5 ? "The name is too short." : null;
        }
    }

    public class SetRegion
    {
        public ServerRegion region { get; set; }
    }
}