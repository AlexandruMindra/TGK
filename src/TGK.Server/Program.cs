using System;

namespace TGK.Server;

public sealed class Program
{
    public static int Main(string[] args) => Cli.Run(args, Console.In, Console.Out, Console.Error);
}
