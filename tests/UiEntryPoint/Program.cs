using System.Reflection;

var entry = Assembly.LoadFrom(Path.GetFullPath(args[0])).EntryPoint
    ?? throw new Exception("Missing application entry point");
if (entry.GetCustomAttribute<STAThreadAttribute>() == null)
    throw new Exception("The UI entry point must use STA for Windows file dialogs.");
Console.WriteLine("PASS: published UI entry point uses STA.");
