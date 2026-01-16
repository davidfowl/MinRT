using Newtonsoft.Json;

var obj = new { Message = "Hello from NuGet!", Runtime = Environment.Version.ToString() };
var json = JsonConvert.SerializeObject(obj, Formatting.Indented);

Console.WriteLine(json);
