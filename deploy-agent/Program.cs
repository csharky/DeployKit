using Microsoft.Extensions.Logging.Console;
using PrepForge.DeployAgent;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.FormatterName = SimpleLogFormatter.FormatterName)
               .AddConsoleFormatter<SimpleLogFormatter, SimpleConsoleFormatterOptions>();

builder.Services.Configure<DeployAgentSettings>(builder.Configuration.GetSection("DeployAgent"));
builder.Services.AddSingleton<StepRunner>();
builder.Services.AddSingleton<DeployServerClient>();
builder.Services.AddHttpClient<DeployServerClient>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
