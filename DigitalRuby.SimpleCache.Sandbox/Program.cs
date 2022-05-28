// playground for testing pub-sub

using DigitalRuby.SimpleCache.Sandbox;

Console.WriteLine("Setting up...");
var builder = Host.CreateDefaultBuilder(args);
builder.ConfigureServices((context, services) =>
{
    services.AddSimpleCache(context.Configuration);
    services.AddHostedService<CacheTestService>();
});

/*
builder.ConfigureLogging(logBuilder =>
    logBuilder.AddJsonConsole(options =>
    {
        options.IncludeScopes = false;
        options.TimestampFormat = "hh:mm:ss ";
        options.JsonWriterOptions = new JsonWriterOptions
        {
            Indented = true
        };
    }
));
*/

Console.WriteLine("Building...");
var host = builder.Build();

Console.WriteLine("Running... Ctrl-C to quit");
await host.RunAsync();

