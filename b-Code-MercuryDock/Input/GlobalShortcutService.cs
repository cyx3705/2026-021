using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace Mercury.Input;

/// <summary>
/// User-session global shortcut host. The hook only queues key metadata; command
/// execution happens on a worker and no input data is persisted or forwarded.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GlobalShortcutService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x00000010;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLAlt = 0xA4;
    private const int VkRAlt = 0xA5;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, Registration> _registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<KeyEvent> _events = Channel.CreateBounded<KeyEvent>(
        new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HookProc _hookProc;
    private readonly ManualResetEventSlim _hookReady = new(false);
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;
    private Task? _pump;
    private readonly GlobalShortcutMatcher _matcher = new();
    private int _disposed;

    /// <summary>Creates a global shortcut host bound to the given command bus.</summary>
    public GlobalShortcutService(CommandBus bus, IShellLog log)
    {
        _bus = bus;
        _log = log;
        _hookProc = HookCallback;
    }

    /// <inheritdoc />
    public bool IsEnabled { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<GlobalShortcutRegistrationInfo> Registrations
    {
        get
        {
            lock (_sync)
                return _registrations.Values.Select(item => item.Info).ToArray();
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (IsEnabled)
            return;

        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "HistoryMercury.GlobalShortcutHook",
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        _hookReady.Wait(TimeSpan.FromSeconds(5));
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("全局快捷键钩子启动失败");

        IsEnabled = true;
        _pump = Task.Run(PumpAsync);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!IsEnabled)
            return;
        IsEnabled = false;
        _events.Writer.TryComplete();
        _lifetime.Cancel();
        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (_hookThread is { IsAlive: true } && !ReferenceEquals(Thread.CurrentThread, _hookThread))
            _hookThread.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
    }

    /// <inheritdoc />
    public IDisposable Register(GlobalShortcutDescriptor descriptor, string owner)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("快捷键 owner 不能为空", nameof(owner));
        Validate(descriptor);
        var normalized = Normalize(descriptor);

        lock (_sync)
        {
            if (_registrations.Values.Any(item => Conflicts(item.Descriptor, normalized)))
                throw new InvalidOperationException($"快捷键冲突: {descriptor.Id}");
            if (_registrations.ContainsKey(Key(owner, descriptor.Id)))
                throw new InvalidOperationException($"快捷键标识重复: {owner}/{descriptor.Id}");

            var info = new GlobalShortcutRegistrationInfo(
                descriptor.Id,
                owner,
                normalized.Strokes,
                descriptor.CommandText,
                descriptor.MaxIntervalMilliseconds);
            _registrations.Add(Key(owner, descriptor.Id),
                new Registration(descriptor, info, normalized.Strokes));
            return new RegistrationHandle(this, Key(owner, descriptor.Id));
        }
    }

    /// <inheritdoc />
    public IGlobalShortcutRegistrar CreateOwnerRegistrar(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return new OwnerRegistrar(this, owner);
    }

    /// <inheritdoc />
    public void UnregisterOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return;
        lock (_sync)
        {
            foreach (var key in _registrations.Keys
                         .Where(key => key.StartsWith(owner + "/", StringComparison.OrdinalIgnoreCase))
                         .ToArray())
                _registrations.Remove(key);
        }
    }

    private async Task PumpAsync()
    {
        var pressed = new HashSet<int>();
        try
        {
            await foreach (var item in _events.Reader.ReadAllAsync(_lifetime.Token))
            {
                if (item.IsDown)
                {
                    if (!pressed.Add(item.VirtualKey))
                        continue;
                    ProcessDown(item);
                }
                else
                {
                    pressed.Remove(item.VirtualKey);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void ProcessDown(KeyEvent item)
    {
        GlobalShortcutRegistrationInfo[] registrations;
        lock (_sync)
            registrations = _registrations.Values.Select(item => item.Info).ToArray();

        foreach (var info in _matcher.Process(
                     registrations,
                     new GlobalShortcutStroke(item.VirtualKey, item.Modifiers),
                     item.Timestamp))
            _ = ExecuteAsync(info);
    }

    private async Task ExecuteAsync(GlobalShortcutRegistrationInfo info)
    {
        try
        {
            var result = await _bus.ExecuteAsync(
                info.CommandText,
                $"Hotkey:{info.Owner}/{info.Id}",
                _lifetime.Token).ConfigureAwait(false);
            if (!result.Success)
                _log.Warn("hotkey", $"快捷键 {info.Owner}/{info.Id} 执行失败: {result.Message}");
        }
        catch (Exception ex)
        {
            _log.Warn("hotkey", $"快捷键 {info.Owner}/{info.Id} 执行异常: {ex.Message}");
        }
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(
            WhKeyboardLl,
            _hookProc,
            GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName),
            0);
        _hookReady.Set();
        if (_hook == IntPtr.Zero)
            return;

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _hookThreadId = 0;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((uint)wParam.ToInt64());
            var down = message is WmKeyDown or WmSysKeyDown;
            var up = message is WmKeyUp or WmSysKeyUp;
            if ((down || up) && Marshal.ReadInt32(lParam + 8) is var flags
                && (((uint)flags & LlkhfInjected) == 0))
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                var modifiers = ReadModifiers();
                _events.Writer.TryWrite(new KeyEvent(
                    data.VkCode,
                    modifiers,
                    down,
                    Stopwatch.GetTimestamp()));
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static GlobalShortcutModifiers ReadModifiers()
    {
        var modifiers = GlobalShortcutModifiers.None;
        if (Down(VkLControl) || Down(VkRControl)) modifiers |= GlobalShortcutModifiers.Control;
        if (Down(VkLAlt) || Down(VkRAlt)) modifiers |= GlobalShortcutModifiers.Alt;
        if (Down(VkLShift) || Down(VkRShift)) modifiers |= GlobalShortcutModifiers.Shift;
        if (Down(VkLWin) || Down(VkRWin)) modifiers |= GlobalShortcutModifiers.Windows;
        return modifiers;
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static void Validate(GlobalShortcutDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id))
            throw new ArgumentException("快捷键 id 不能为空", nameof(descriptor));
        if (descriptor.Strokes is not { Count: >= 1 and <= 4 })
            throw new ArgumentException("快捷键序列必须包含 1 到 4 步", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.CommandText))
            throw new ArgumentException("快捷键必须绑定命令", nameof(descriptor));
        if (descriptor.MaxIntervalMilliseconds is < 100 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(descriptor), "快捷键间隔必须在 100 到 2000 毫秒之间");
        if (descriptor.Strokes.Any(stroke => stroke.VirtualKey is < 1 or > 0xFF))
            throw new ArgumentException("快捷键包含无效 VirtualKey", nameof(descriptor));
    }

    private static GlobalShortcutDescriptor Normalize(GlobalShortcutDescriptor descriptor)
        => descriptor with { Strokes = descriptor.Strokes.ToArray() };

    private static bool Conflicts(GlobalShortcutDescriptor left, GlobalShortcutDescriptor right)
    {
        var length = Math.Min(left.Strokes.Count, right.Strokes.Count);
        return left.Strokes.Take(length).SequenceEqual(right.Strokes.Take(length));
    }

    private static string Key(string owner, string id) => owner + "/" + id;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
        lock (_sync)
            _registrations.Clear();
        _hookReady.Dispose();
        _lifetime.Dispose();
    }

    private sealed class OwnerRegistrar(GlobalShortcutService service, string owner) : IGlobalShortcutRegistrar
    {
        public IDisposable Register(GlobalShortcutDescriptor descriptor) => service.Register(descriptor, owner);

        public void Dispose() => service.UnregisterOwner(owner);
    }

    private sealed class RegistrationHandle(GlobalShortcutService service, string key) : IDisposable
    {
        public void Dispose()
        {
            lock (service._sync)
                service._registrations.Remove(key);
        }
    }

    private sealed record Registration(
        GlobalShortcutDescriptor Descriptor,
        GlobalShortcutRegistrationInfo Info,
        IReadOnlyList<GlobalShortcutStroke> Strokes)
    {
        public string InfoKey => Info.Owner + "/" + Info.Id;
    }

    private readonly record struct KeyEvent(
        int VirtualKey,
        GlobalShortcutModifiers Modifiers,
        bool IsDown,
        long Timestamp);

    private readonly record struct KbdLlHookStruct(
        int VkCode,
        int ScanCode,
        uint Flags,
        uint Time,
        IntPtr DwExtraInfo);

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    private const uint WmQuit = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr HWnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

internal sealed class GlobalShortcutMatcher
{
    private readonly Dictionary<string, MatchState> _states = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GlobalShortcutRegistrationInfo> Process(
        IReadOnlyList<GlobalShortcutRegistrationInfo> registrations,
        GlobalShortcutStroke stroke,
        long timestamp)
    {
        var matches = new List<GlobalShortcutRegistrationInfo>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registrations)
        {
            var key = registration.Owner + "/" + registration.Id;
            live.Add(key);
            var state = _states.GetValueOrDefault(key) ?? new MatchState();
            if (state.Index >= registration.Strokes.Count ||
                state.Index > 0 && Stopwatch.GetElapsedTime(state.LastTimestamp, timestamp)
                    > TimeSpan.FromMilliseconds(registration.MaxIntervalMilliseconds))
                state.Index = 0;

            if (registration.Strokes[state.Index] == stroke)
            {
                state.Index++;
                state.LastTimestamp = timestamp;
                if (state.Index == registration.Strokes.Count)
                {
                    state.Index = 0;
                    matches.Add(registration);
                }
            }
            else
            {
                state.Index = registration.Strokes[0] == stroke ? 1 : 0;
                state.LastTimestamp = state.Index == 0 ? 0 : timestamp;
            }

            _states[key] = state;
        }

        foreach (var stale in _states.Keys.Where(key => !live.Contains(key)).ToArray())
            _states.Remove(stale);
        return matches;
    }

    private sealed class MatchState
    {
        public int Index;
        public long LastTimestamp;
    }
}
