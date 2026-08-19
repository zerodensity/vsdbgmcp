using System;
using System.Runtime.InteropServices;
using System.Text;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Reads and writes the console of the program being debugged.
    ///
    /// A console program's output goes to its own console window, not to any Visual
    /// Studio pane, so without this an agent that launches one is blind to everything
    /// it prints. Attaching to the debuggee's console lets us read the screen buffer
    /// even while the process is stopped at a breakpoint.
    /// </summary>
    static class ConsoleBridge
    {
        const uint GenericRead = 0x80000000;
        const uint GenericWrite = 0x40000000;
        const uint FileShareRead = 1;
        const uint FileShareWrite = 2;
        const uint OpenExisting = 3;
        static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out CONSOLE_SCREEN_BUFFER_INFO info);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ReadConsoleOutputCharacter(IntPtr handle, [Out] char[] buffer, uint length,
            COORD position, out uint read);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool WriteConsoleInput(IntPtr handle, INPUT_RECORD[] records, uint count, out uint written);

        [StructLayout(LayoutKind.Sequential)]
        struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct SMALL_RECT { public short Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public ushort wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT_RECORD
        {
            public ushort EventType;
            public int bKeyDown;
            public ushort wRepeatCount;
            public ushort wVirtualKeyCode;
            public ushort wVirtualScanCode;
            public char UnicodeChar;
            public uint dwControlKeyState;
        }

        const ushort KeyEvent = 1;

        static readonly object Gate = new object();

        public static ConsoleResult Read(int pid)
        {
            lock (Gate)
            {
                var result = new ConsoleResult();
                if (!Attach(pid, result)) return result;

                var handle = InvalidHandle;
                try
                {
                    handle = CreateFile("CONOUT$", GenericRead | GenericWrite,
                        FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

                    if (handle == InvalidHandle)
                    {
                        result.Error = "Could not open the console buffer.";
                        return result;
                    }

                    if (!GetConsoleScreenBufferInfo(handle, out var info))
                    {
                        result.Error = "Could not read the console buffer size.";
                        return result;
                    }

                    result.Width = info.dwSize.X;
                    result.Height = info.dwCursorPosition.Y + 1;
                    result.CursorRow = info.dwCursorPosition.Y;
                    result.CursorCol = info.dwCursorPosition.X;

                    var width = info.dwSize.X;
                    var rows = Math.Min(info.dwCursorPosition.Y + 1, info.dwSize.Y);
                    var text = new StringBuilder();
                    var line = new char[width];

                    for (short y = 0; y < rows; y++)
                    {
                        if (!ReadConsoleOutputCharacter(handle, line, (uint)width, new COORD { X = 0, Y = y }, out var read))
                            break;
                        text.AppendLine(new string(line, 0, (int)read).TrimEnd());
                    }

                    result.Text = text.ToString().TrimEnd();
                    return result;
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                    return result;
                }
                finally
                {
                    if (handle != InvalidHandle) CloseHandle(handle);
                    FreeConsole();
                }
            }
        }

        public static OpResult Send(int pid, string text, string keys)
        {
            lock (Gate)
            {
                var probe = new ConsoleResult();
                if (!Attach(pid, probe)) return OpResult.Bad(probe.Error);

                var handle = InvalidHandle;
                try
                {
                    handle = CreateFile("CONIN$", GenericRead | GenericWrite,
                        FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

                    if (handle == InvalidHandle) return OpResult.Bad("Could not open the console input buffer.");

                    var payload = text;
                    if (!string.IsNullOrEmpty(payload) && !payload.EndsWith("\n")) payload += "\r";
                    if (!string.IsNullOrEmpty(keys)) payload = (payload ?? "") + Translate(keys);
                    if (string.IsNullOrEmpty(payload)) return OpResult.Bad("Nothing to send.");

                    var records = new INPUT_RECORD[payload.Length * 2];
                    var index = 0;
                    foreach (var c in payload)
                    {
                        records[index++] = KeyRecord(c, true);
                        records[index++] = KeyRecord(c, false);
                    }

                    if (!WriteConsoleInput(handle, records, (uint)index, out var written))
                        return OpResult.Bad("Could not write to the console.");

                    return OpResult.Good("Sent " + written / 2 + " characters.");
                }
                catch (Exception ex)
                {
                    return OpResult.Bad(ex.Message);
                }
                finally
                {
                    if (handle != InvalidHandle) CloseHandle(handle);
                    FreeConsole();
                }
            }
        }

        static INPUT_RECORD KeyRecord(char c, bool down) => new INPUT_RECORD
        {
            EventType = KeyEvent,
            bKeyDown = down ? 1 : 0,
            wRepeatCount = 1,
            UnicodeChar = c,
            wVirtualKeyCode = 0,
            wVirtualScanCode = 0,
            dwControlKeyState = 0
        };

        static string Translate(string keys)
        {
            switch ((keys ?? "").Trim().ToLowerInvariant())
            {
                case "enter": case "return": return "\r";
                case "tab": return "\t";
                case "escape": case "esc": return "";
                case "backspace": return "\b";
                case "ctrl+c": return "";
                case "ctrl+d": return "";
                case "ctrl+z": return "";
                default: return keys;
            }
        }

        static bool Attach(int pid, ConsoleResult result)
        {
            // Visual Studio has no console of its own, so borrowing the debuggee's is
            // safe as long as we always give it back in the finally block.
            FreeConsole();
            if (AttachConsole((uint)pid)) return true;

            var error = Marshal.GetLastWin32Error();
            result.Error = error == 5
                ? "Access denied attaching to the console. The debuggee may be elevated."
                : "That process has no console (error " + error + "). Only console programs have one.";
            return false;
        }
    }
}
