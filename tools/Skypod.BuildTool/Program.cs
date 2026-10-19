using Skypod.Core;

if (args.Length != 2 || !string.Equals(args[0], "create-base-plugin", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: Skypod.BuildTool create-base-plugin <output-path>");
    return 2;
}

SkypodMcmPlugin.WriteBasePlugin(args[1]);
Console.WriteLine($"Created {Path.GetFullPath(args[1])}");
return 0;
