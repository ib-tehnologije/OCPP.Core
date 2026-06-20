/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2025 dallmann consulting GmbH.
 * All Rights Reserved.
 */

using System.Collections.Generic;

namespace OCPP.Core.Management.Models
{
    public class AlfenConfigOption
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public string Type { get; set; } // bool, int, string, list
        public string Unit { get; set; }
        public string DefaultValue { get; set; }
        public long? Min { get; set; }
        public long? Max { get; set; }
        public List<string> AllowedValues { get; set; }
        public string Notes { get; set; }
        public string Access { get; set; } // RO, RW
    }
}
