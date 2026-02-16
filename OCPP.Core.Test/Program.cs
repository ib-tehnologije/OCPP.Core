using System;
using System.Linq;

namespace OCPP.Core.Test
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Any(arg => arg.Equals("--non-interactive", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable("OCPP_TEST_NON_INTERACTIVE", "1");
            }

            OCPP16Test.Execute();
            OCPP20Test.Execute();
            OCPP21Test.Execute();
        }
    }
}
