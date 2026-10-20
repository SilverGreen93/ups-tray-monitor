using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UpSTrayMonitor;

/// <summary>
/// Gives a GUI-subsystem process a usable console for the command-line modes.
/// Output that is already redirected (pipe / file) is left untouched.
/// </summary>
internal static class ConsoleAttach
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    public static void Attach()
    {
        bool hadStdOut = HasStdHandle(StdOutputHandle);
        bool hadStdErr = HasStdHandle(StdErrorHandle);
        if (hadStdOut && hadStdErr)
        {
            return;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole(); // started from Explorer: open a window of our own
        }

        // Console.Out/Error were bound to the (missing) std handles at startup;
        // point them at the console that was just attached.
        const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareReadWrite = 3, OpenExisting = 3;
        if (!hadStdOut)
        {
            Console.SetOut(OpenConsoleWriter(GenericRead | GenericWrite, ShareReadWrite, OpenExisting));
        }

        if (!hadStdErr)
        {
            Console.SetError(OpenConsoleWriter(GenericRead | GenericWrite, ShareReadWrite, OpenExisting));
        }
    }

    private static bool HasStdHandle(int which)
    {
        IntPtr h = GetStdHandle(which);
        return h != IntPtr.Zero && h != new IntPtr(-1);
    }

    private static StreamWriter OpenConsoleWriter(uint access, uint share, uint disposition)
    {
        IntPtr h = CreateFile("CONOUT$", access, share, IntPtr.Zero, disposition, 0, IntPtr.Zero);
        var stream = new FileStream(new SafeFileHandle(h, ownsHandle: true), FileAccess.Write);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
}
