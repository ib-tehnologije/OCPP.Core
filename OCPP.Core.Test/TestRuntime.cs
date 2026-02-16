using System;

namespace OCPP.Core.Test
{
    internal static class TestRuntime
    {
        internal static string GetSetting(string key, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        internal static bool NonInteractive
        {
            get
            {
                string value = GetSetting("OCPP_TEST_NON_INTERACTIVE", "0");
                return value.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("y", StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static void Pause(string message)
        {
            Console.WriteLine(message);
            if (!NonInteractive)
            {
                Console.ReadLine();
            }
        }
    }
}
