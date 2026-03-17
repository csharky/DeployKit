using PrepForge.DeployAgent;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<DeployAgentSettings>(builder.Configuration.GetSection("DeployAgent"));
builder.Services.AddSingleton<EasBuildRunner>();
builder.Services.AddSingleton<DeployServerClient>();
builder.Services.AddHttpClient<DeployServerClient>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
