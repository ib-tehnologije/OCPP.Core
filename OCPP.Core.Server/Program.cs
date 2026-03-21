/*
 * OCPP.Core - https://github.com/dallmann-consulting/OCPP.Core
 * Copyright (C) 2020-2021 dallmann consulting GmbH.
 * All Rights Reserved.
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.Server
{
    public class Program
    {
        internal static IConfiguration _configuration;

        public static void Main(string[] args)
        {
            _configuration = BuildBootstrapConfiguration();

            CreateHostBuilder(args, _configuration).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args, IConfiguration bootstrapConfiguration = null) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    if (HasSentryDsn(bootstrapConfiguration))
                    {
                        webBuilder.UseSentry();
                    }

                    webBuilder
                    .ConfigureLogging((ctx, builder) =>
                                        {
                                            builder.AddConfiguration(ctx.Configuration.GetSection("Logging"));
                                            builder.AddFile(o => o.RootPath = ctx.HostingEnvironment.ContentRootPath);
                                        })
                    .UseStartup<Startup>();
                });

        private static IConfiguration BuildBootstrapConfiguration()
        {
            string environmentName =
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

            var configurationBuilder = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false);

            if (!string.IsNullOrWhiteSpace(environmentName))
            {
                configurationBuilder.AddJsonFile($"appsettings.{environmentName}.json", optional: true);
            }

            configurationBuilder.AddEnvironmentVariables();
            return configurationBuilder.Build();
        }

        private static bool HasSentryDsn(IConfiguration configuration)
        {
            if (configuration == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(configuration["Sentry:Dsn"]) ||
                   !string.IsNullOrWhiteSpace(configuration["SENTRY_DSN"]);
        }
    }
}
