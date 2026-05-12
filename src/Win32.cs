using System.Buffers;
using System.Runtime.InteropServices;

namespace speach;

public static partial class Win32
{
    public const int VK_F4 = 0x73;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_WIN = 0x0008;
    public const int VK_Q = 0x51;
    public const int VK_SPACE = 0x20;
    public const int WM_HOTKEY = 0x0312;
    public const int VK_OEM_3 = 0xC0;
    public const int HOTKEY_ID = 9001;

    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_V = 0x56;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint WM_QUIT = 0x0012;

    private static readonly INPUT[] _inputs =
    [
        CreateKey(VK_CONTROL, 0),
        CreateKey(VK_V, 0),
        CreateKey(VK_V, KEYEVENTF_KEYUP),
        CreateKey(VK_CONTROL, KEYEVENTF_KEYUP),
    ];

    public static void SendTextDirectly(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputCount = text.Length * 2;
        var inputs = ArrayPool<Win32.INPUT>.Shared.Rent(inputCount);

        try
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                inputs[i * 2] = Win32.CreateUnicodePress(c);
                inputs[i * 2 + 1] = Win32.CreateUnicodeRelease(c);
            }

            var result = Win32.SendInput((uint)inputCount, inputs, Marshal.SizeOf<Win32.INPUT>());
            if (result == 0 && inputCount > 0)
            {
                var error = Marshal.GetLastWin32Error();
                Console.WriteLine($"Ошибка SendInput: {error}");
            }
        }
        finally
        {
            ArrayPool<Win32.INPUT>.Shared.Return(inputs);
        }
    }

    public static void Paste()
    {
        uint result = SendInput(4, _inputs, Marshal.SizeOf<INPUT>());
        if (result == 0)
        {
            throw new Exception("SendInput заблокирован (UIPI/UAC) или ошибка структуры");
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "SendInput")]
    private static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    public static partial int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    public static partial void PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG 
    { 
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y; 
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        private readonly long padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    private static INPUT CreateKey(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = flags }
    };

    private static INPUT CreateUnicodePress(char c) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wScan = c,
            wVk = 0,
            dwFlags = KEYEVENTF_UNICODE
        }
    };

    private static INPUT CreateUnicodeRelease(char c) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wScan = c,
            wVk = 0,
            dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
        }
    };
}