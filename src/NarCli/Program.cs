using NarTool.Core;

namespace NarTool.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            return Cli.Run(args);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException
                                       or NotSupportedException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            return 1;
        }
    }
}
