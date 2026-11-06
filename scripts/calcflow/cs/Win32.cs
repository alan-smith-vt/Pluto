// Copied from pipe-review (src/cs/Win32.cs at d4b33aa, 2026-11-06) for CalcFlow; namespace PipeReview -> CalcFlow.
// Win32.cs -- the process id behind an Excel window handle (Application.Hwnd), so the tool can stop exactly the
// Excel instance it started and never one the engineer has open. Windows only; never called elsewhere.

using System;
using System.Runtime.InteropServices;

namespace CalcFlow
{
    public static class Win32
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        public static int ProcessIdOfWindow(long hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(new IntPtr(hwnd), out pid);
            return (int)pid;
        }
    }
}
