var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => $"Hello from ASP.NET Core! Runtime: {Environment.Version}");

// Run on a specific port and exit after first request for testing
app.Urls.Add("http://localhost:5099");

Console.WriteLine($"Starting on http://localhost:5099");
Console.WriteLine($"Runtime: {Environment.Version}");

app.Run();
