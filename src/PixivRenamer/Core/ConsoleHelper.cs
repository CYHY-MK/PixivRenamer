using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PixivRenamer.Core;

public static class ConsoleHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    public static void Attach()
    {
        try
        {
            if (!AttachConsole(-1)) AllocConsole();
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stderr);
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        }
        catch { }
    }
}

public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public SyncProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}