using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FortItemTracker;

/// <summary>
/// Finds the game process. While it runs, keeps a handle and only asks whether it has exited (nearly
/// free); the process list (~4 ms) is walked only while waiting for the game.
/// </summary>
sealed class GameWatcher(IEnumerable<string> names) : IDisposable
{
    const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    readonly string[] names = names
        .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n).ToArray();
    readonly Dictionary<int, IntPtr> running = [];  // pid → handle (zero if the game refused one)

    public HashSet<int> Poll()
    {
        foreach (var (pid, handle) in running.ToList())
        {
            if (handle == IntPtr.Zero || WaitForSingleObject(handle, 0) != 0) continue;  // 0 = signalled = exited
            CloseHandle(handle);
            running.Remove(pid);
        }
        // Without a handle for every process we can't tell it exited, so walk the list again.
        if (running.Count == 0 || running.ContainsValue(IntPtr.Zero)) Scan();
        return [.. running.Keys];
    }

    void Scan()
    {
        var found = new HashSet<int>();
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name))
            {
                found.Add(p.Id);
                p.Dispose();
            }
        foreach (var pid in running.Keys.Where(p => !found.Contains(p)).ToList())
        {
            if (running[pid] != IntPtr.Zero) CloseHandle(running[pid]);
            running.Remove(pid);
        }
        foreach (var pid in found.Where(p => !running.ContainsKey(p)))
            running[pid] = OpenProcess(SYNCHRONIZE, false, pid);
    }

    public void Dispose()
    {
        foreach (var h in running.Values)
            if (h != IntPtr.Zero) CloseHandle(h);
        running.Clear();
    }
}
