using System;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Threading;

// ASUS UX581GV: follow the main panel's hardware brightness, without ScreenXpert.
// ASUS device identifiers corroborated against seerge/g-helper, app/AsusACPI.cs.
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class DuoMinimal : ServiceBase
{
    private const string MainPanelPrefix = "DISPLAY\\SDCA029\\";
    private const uint ScreenPadPower = 0x00050031;
    private const uint ScreenPadBrightness = 0x00050032;
    private const uint MinimumScreenPadBrightness = 4;
    private enum PanelMode { Visible = 0, Dark = 1, Disconnected = 2 }
    private PanelMode panelMode = PanelMode.Visible;
    private bool restoreMode = true;
    private long lastButtonTick;
    private Timer clickTimer;
    private long clickGeneration;
    private long pendingClickTick;
    private const int DoubleClickMs = 500;
    private long lastTouchTick;
    private readonly object gate = new object();
    private Timer timer;
    private ManagementEventWatcher watcher;
    private ManagementEventWatcher buttonWatcher;
    private bool stopping;
    private int lastPercent = -1;
    private DateTime nextRefresh = DateTime.MinValue;
    private DateTime nextErrorLog = DateTime.MinValue;
    private static readonly object logGate = new object();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DuoMinimal", "service.log");
    private static readonly string ModePath = Path.Combine(Path.GetDirectoryName(LogPath), "panel-mode.txt");
    private static readonly string TouchMarkerPath = Path.Combine(Path.GetDirectoryName(LogPath), "touch-disabled-by-service.txt");

    private DuoMinimal()
    {
        ServiceName = "DuoMinimal";
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        AutoLog = false;
    }

    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--restore")
        {
            try
            {
                RestoreOwnedTouch();
                if (File.Exists(TouchMarkerPath)) throw new InvalidOperationException("Touch recovery incomplete.");
                ApplyMode(PanelMode.Visible, ReadMainBrightness());
                Log("Uninstall recovery: display visible; owned touch suppression cleared.");
            }
            catch (Exception ex) { Log("Uninstall recovery failed: " + ex); Environment.ExitCode = 1; }
            return;
        }
        ServiceBase.Run(new DuoMinimal());
    }

    protected override void OnStart(string[] args)
    {
        using (var search = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
        using (var rows = search.Get())
        {
            bool supported = false;
            foreach (ManagementObject row in rows)
            using (row)
                supported |= Convert.ToString(row["Model"]).IndexOf("UX581GV", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!supported) throw new InvalidOperationException("This service is configured for ASUS UX581GV only.");
        }
        stopping = false;
        RestoreOwnedTouch();
        int savedMode;
        if (File.Exists(ModePath) && Int32.TryParse(File.ReadAllText(ModePath).Trim(), out savedMode) && savedMode >= 0 && savedMode <= 2)
            panelMode = (PanelMode)savedMode;
        else
            WithAsus(delegate(ManagementObject atk) { panelMode = IsPanelPowered(atk) ? PanelMode.Visible : PanelMode.Disconnected; });
        timer = new Timer(Synchronize, null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            watcher = new ManagementEventWatcher("root\\wmi", "SELECT * FROM WmiMonitorBrightnessEvent");
            watcher.EventArrived += OnBrightnessEvent;
            watcher.Start();
            Log("Started; brightness events enabled, 5-second fallback polling.");
        }
        catch (Exception ex)
        {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            Log("Brightness event subscription unavailable; using polling: " + ex.Message);
        }
        try
        {
            buttonWatcher = new ManagementEventWatcher("root\\wmi", "SELECT * FROM AsusAtkWmiEvent");
            buttonWatcher.EventArrived += OnAsusButton;
            buttonWatcher.Start();
            Log("ScreenPad button (106): single=backlight, double within 500ms=power; touch toggle (156) enabled; saved mode=" + panelMode + ".");
        }
        catch
        {
            StopWork();
            throw;
        }
        timer.Change(0, 5000);
    }

    private static void WithAsus(Action<ManagementObject> action)
    {
        using (var search = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM AsusAtkWmi_WMNB"))
        using (var rows = search.Get())
        {
            foreach (ManagementObject atk in rows)
            using (atk) { action(atk); return; }
        }
        throw new InvalidOperationException("ASUS WMI control instance unavailable.");
    }

    private static bool IsPanelPowered(ManagementObject atk)
    {
        using (var input = atk.GetMethodParameters("DSTS"))
        {
            input["Device_ID"] = ScreenPadPower;
            using (var output = atk.InvokeMethod("DSTS", input, null))
            {
                if (output == null) throw new InvalidOperationException("Cannot read ScreenPad power state.");
                uint state = Convert.ToUInt32(output["device_status"]);
                if ((state & 0x10000) == 0) throw new InvalidOperationException("ScreenPad power control unavailable.");
                return (state & 1) != 0;
            }
        }
    }

    private static void SetDevice(ManagementObject atk, uint device, uint value)
    {
        using (var input = atk.GetMethodParameters("DEVS"))
        {
            input["Device_ID"] = device;
            input["Control_status"] = value;
            using (var output = atk.InvokeMethod("DEVS", input, null))
                if (output == null || Convert.ToUInt32(output["result"]) != 1)
                    throw new InvalidOperationException("ASUS rejected device " + device.ToString("X") + " value " + value + ".");
        }
    }

    private static int ReadMainBrightness()
    {
        using (var search = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE"))
        using (var rows = search.Get())
        {
            foreach (ManagementObject row in rows)
            using (row)
                if (Convert.ToString(row["InstanceName"]).StartsWith(MainPanelPrefix, StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(row["CurrentBrightness"]);
        }
        return -1;
    }

    private static uint MapBrightness(int percent)
    {
        if (percent < 0 || percent > 100) throw new InvalidOperationException("Main panel brightness unavailable.");
        return Math.Max(MinimumScreenPadBrightness, (uint)Math.Round(255.0 * percent / 100.0));
    }

    private static void ApplyMode(PanelMode mode, int percent)
    {
        WithAsus(delegate(ManagementObject atk)
        {
            if (mode == PanelMode.Disconnected)
                SetDevice(atk, ScreenPadPower, 0);
            else
            {
                SetDevice(atk, ScreenPadPower, 1);
                SetDevice(atk, ScreenPadBrightness, mode == PanelMode.Dark ? 0 : MapBrightness(percent));
            }
            bool expectedPower = mode != PanelMode.Disconnected;
            // Firmware/display-driver completion is asynchronous (especially detach).
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (IsPanelPowered(atk) != expectedPower)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException("ScreenPad power state did not match requested mode " + mode + ".");
                Thread.Sleep(100);
            }
        });
    }

    private void OnAsusButton(object sender, EventArrivedEventArgs args)
    {
        uint eventId = Convert.ToUInt32(args.NewEvent["EventID"]);
        if (eventId != 106 && eventId != 156) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (gate)
        {
            long previous = eventId == 156 ? lastTouchTick : lastButtonTick;
            double debounceSeconds = eventId == 156 ? 0.35 : 0.08;
            if (stopping || (previous != 0 && (now - previous) < System.Diagnostics.Stopwatch.Frequency * debounceSeconds)) return;
            if (eventId == 156)
            {
                lastTouchTick = now;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    lock (gate)
                    {
                        if (stopping) return;
                        try { ToggleTouch(); }
                        catch (Exception ex) { Log("Touch toggle failed: " + ex.ToString()); }
                    }
                });
                return;
            }
            lastButtonTick = now;
            if (clickTimer != null)
            {
                double elapsed = (now - pendingClickTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                clickTimer.Dispose();
                clickTimer = null;
                ++clickGeneration; // Invalidate an already-queued timeout callback.
                if (elapsed <= DoubleClickMs)
                {
                    QueuePanelAction(true);
                    return;
                }
                QueuePanelAction(false);
            }
            pendingClickTick = now;
            long generation = ++clickGeneration;
            clickTimer = new Timer(delegate
            {
                lock (gate)
                {
                    if (stopping || generation != clickGeneration || clickTimer == null) return;
                    clickTimer.Dispose();
                    clickTimer = null;
                    ++clickGeneration;
                    QueuePanelAction(false);
                }
            }, null, DoubleClickMs, Timeout.Infinite);
        }
    }

    private void QueuePanelAction(bool doubleClick)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            lock (gate)
            {
                if (stopping) return;
                PanelMode next = doubleClick
                    ? (panelMode == PanelMode.Disconnected ? PanelMode.Visible : PanelMode.Disconnected)
                    : (panelMode == PanelMode.Visible ? PanelMode.Dark : PanelMode.Visible);
                try
                {
                    int percent = ReadMainBrightness();
                    ApplyMode(next, percent);
                    panelMode = next;
                    restoreMode = false;
                    lastPercent = -1;
                    Directory.CreateDirectory(Path.GetDirectoryName(ModePath));
                    string temp = ModePath + ".new";
                    File.WriteAllText(temp, ((int)panelMode).ToString());
                    if (File.Exists(ModePath)) File.Replace(temp, ModePath, null); else File.Move(temp, ModePath);
                    Log("ScreenPad button: " + (doubleClick ? "DOUBLE" : "SINGLE") + ", mode=" + panelMode + ".");
                    if (timer != null) timer.Change(0, 5000);
                }
                catch (Exception ex) { Log("ScreenPad button failed: " + ex.ToString()); }
            }
        });
    }
    private static void ToggleTouch()
    {
        bool enable = TouchControl.BothDisabled();
        if (!enable)
        {
            // Write before touching the devices, so service restart can recover
            // even if the process exits between disabling the two collections.
            Directory.CreateDirectory(Path.GetDirectoryName(TouchMarkerPath));
            File.WriteAllText(TouchMarkerPath, DateTime.UtcNow.ToString("o"));
        }
        try
        {
            TouchControl.SetEnabled(enable);
            if (enable && File.Exists(TouchMarkerPath)) File.Delete(TouchMarkerPath);
            Log("Touch button: both touchscreens " + (enable ? "ENABLED" : "DISABLED") + ".");
        }
        catch
        {
            RestoreOwnedTouch();
            throw;
        }
    }

    private static void RestoreOwnedTouch()
    {
        if (!File.Exists(TouchMarkerPath)) return;
        try
        {
            TouchControl.SetEnabled(true);
            File.Delete(TouchMarkerPath);
            Log("Restored touch input on both screens.");
        }
        catch (Exception ex) { Log("Touch recovery needs attention: " + ex.ToString()); }
    }

    private void OnBrightnessEvent(object sender, EventArrivedEventArgs args)
    {
        string instance = Convert.ToString(args.NewEvent["InstanceName"]);
        if (!instance.StartsWith(MainPanelPrefix, StringComparison.OrdinalIgnoreCase)) return;
        lock (gate)
            if (!stopping && timer != null) timer.Change(0, 5000);
    }

    private void Synchronize(object ignored)
    {
        if (!Monitor.TryEnter(gate)) return;
        string phase = "read main brightness";
        try
        {
            if (stopping) return;
            int percent = ReadMainBrightness();
            if (percent < 0 || percent > 100) return;
            if (restoreMode)
            {
                phase = "restore saved ScreenPad mode";
                ApplyMode(panelMode, percent);
                restoreMode = false;
                Log("Restored mode=" + panelMode + ".");
            }
            // Dark mode intentionally uses zero backlight; detached mode stays detached.
            // Main-panel brightness changes must never wake either of these modes.
            if (panelMode != PanelMode.Visible) return;
            bool changed = percent != lastPercent;
            if (!changed && DateTime.UtcNow < nextRefresh) return;
            // Main-panel 0% is still visible, but ScreenPad raw 0 turns its
            // backlight off. Keep a small nonzero floor at the minimum setting.
            uint raw = MapBrightness(percent);
            bool applied = false;
            phase = "locate ASUS WMI instance";
            using (var search = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM AsusAtkWmi_WMNB"))
            using (var rows = search.Get())
            {
                foreach (ManagementObject atk in rows)
                using (atk)
                {
                    phase = "read ScreenPad power state";
                    using (var input = atk.GetMethodParameters("DSTS"))
                    {
                        input["Device_ID"] = ScreenPadPower;
                        using (var output = atk.InvokeMethod("DSTS", input, null))
                        {
                            if (output == null)
                                throw new InvalidOperationException("Cannot read ScreenPad power state.");
                            uint state = Convert.ToUInt32(output["device_status"]);
                            // Do not turn on a panel which the user has disabled.
                            if ((state & 0x10000) == 0 || (state & 1) == 0) return;
                        }
                    }
                    phase = "write ScreenPad brightness";
                    using (var input = atk.GetMethodParameters("DEVS"))
                    {
                        input["Device_ID"] = ScreenPadBrightness;
                        input["Control_status"] = raw;
                        using (var output = atk.InvokeMethod("DEVS", input, null))
                            if (output == null || Convert.ToUInt32(output["result"]) != 1)
                                throw new InvalidOperationException("ASUS rejected the ScreenPad brightness request.");
                    }
                    applied = true;
                    break;
                }
            }
            if (!applied) throw new InvalidOperationException("ASUS WMI control instance unavailable.");
            if (changed) Log("Applied main=" + percent + "%, ScreenPad raw=" + raw + ".");
            lastPercent = percent;
            nextRefresh = DateTime.UtcNow.AddSeconds(30);
            nextErrorLog = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow >= nextErrorLog)
            {
                Log("Retrying after error (" + phase + "): " + ex.ToString());
                nextErrorLog = DateTime.UtcNow.AddSeconds(30);
            }
        }
        finally { Monitor.Exit(gate); }
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        if (status == PowerBroadcastStatus.ResumeAutomatic || status == PowerBroadcastStatus.ResumeSuspend)
        {
            lock (gate)
            {
                lastPercent = -1;
                restoreMode = true;
                if (!stopping && timer != null) timer.Change(0, 5000);
            }
        }
        return true;
    }

    protected override void OnStop() { StopWork(); }
    protected override void OnShutdown() { StopWork(); }
    private void StopWork()
    {
        lock (gate)
        {
            stopping = true;
            ++clickGeneration;
            if (clickTimer != null) { clickTimer.Dispose(); clickTimer = null; }
            if (timer != null) { timer.Dispose(); timer = null; }
        }
        if (watcher != null)
        {
            watcher.EventArrived -= OnBrightnessEvent;
            try { watcher.Stop(); } catch { }
            watcher.Dispose();
            watcher = null;
        }
        if (buttonWatcher != null)
        {
            buttonWatcher.EventArrived -= OnAsusButton;
            try { buttonWatcher.Stop(); } catch { }
            buttonWatcher.Dispose();
            buttonWatcher = null;
        }
        RestoreOwnedTouch();
        Log("Stopped.");
    }

    private static void Log(string message)
    {
        try
        {
            lock (logGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 131072)
                {
                    string previous = LogPath + ".previous";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(LogPath, previous);
                }
                File.AppendAllText(LogPath, DateTime.Now.ToString("o") + " " + message + Environment.NewLine);
            }
        }
        catch { /* Logging failure must not interrupt brightness synchronization. */ }
    }
}



