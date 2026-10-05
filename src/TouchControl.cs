using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

// Only the two verified touchscreen HID collections on this UX581GV.
// Do not disable the ACPI parent, touchpad, pen, keyboard or display adapter.
public static class TouchControl
{
    // Match verified touchscreen collections, not machine-specific instance suffixes.
    // Refuse ambiguous/missing collections rather than selecting another HID device.
    private static string[] Devices
    {
        get
        {
            string[] prefixes = { @"HID\ELAN9008&COL01\", @"HID\ELAN9009&COL01\" };
            var matches = new List<string>[] { new List<string>(), new List<string>() };
            using (var search = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'HIDClass'"))
            using (var rows = search.Get())
                foreach (ManagementObject row in rows)
                using (row)
                    for (int i = 0; i < prefixes.Length; i++)
                    {
                        string id = Convert.ToString(row["PNPDeviceID"]);
                        if (id.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) matches[i].Add(id);
                    }
            if (matches[0].Count != 1 || matches[1].Count != 1)
                throw new InvalidOperationException("Expected exactly one ELAN9008 COL01 and one ELAN9009 COL01 touchscreen.");
            return new string[] { matches[0][0], matches[1][0] };
        }
    }
    private const uint DisabledProblem = 22;
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    private static extern uint Locate(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    private static extern uint Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Disable_DevNode")]
    private static extern uint Disable(uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Enable_DevNode")]
    private static extern uint Enable(uint devInst, uint flags);

    private static uint GetNode(string id)
    {
        uint node;
        uint result = Locate(out node, id, 0);
        if (result != 0) throw new InvalidOperationException("Touch device unavailable: " + id + ", code=" + result);
        return node;
    }
    private static uint GetProblem(uint node)
    {
        uint status, problem;
        uint result = Status(out status, out problem, node, 0);
        if (result != 0) throw new InvalidOperationException("Cannot read touch device state, code=" + result);
        return problem;
    }
    public static bool BothDisabled()
    {
        bool all = true;
        foreach (string id in Devices) all &= GetProblem(GetNode(id)) == DisabledProblem;
        return all;
    }
    public static string Describe()
    {
        string output = "";
        foreach (string id in Devices) output += id + " problem=" + GetProblem(GetNode(id)) + Environment.NewLine;
        return output;
    }
    public static void SetEnabled(bool enabled)
    {
        string[] devices = Devices;
        uint[] nodes = new uint[devices.Length];
        bool[] previouslyDisabled = new bool[devices.Length];
        for (int i = 0; i < devices.Length; i++)
        {
            nodes[i] = GetNode(devices[i]);
            uint problem = GetProblem(nodes[i]);
            if (problem != 0 && problem != DisabledProblem)
                throw new InvalidOperationException("Touch device has an unexpected problem code: " + problem);
            previouslyDisabled[i] = problem == DisabledProblem;
        }
        try
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                uint result = enabled ? Enable(nodes[i], 0) : Disable(nodes[i], 4); // UI_NOT_OK, no FORCE/PERSIST
                if (result != 0)
                    throw new InvalidOperationException("Touch " + (enabled ? "enable" : "disable") + " rejected for " + devices[i] + ", code=" + result + ". No reboot requested by this tool.");
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (GetProblem(nodes[i]) != (enabled ? 0 : DisabledProblem))
                {
                    if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("Touch state change did not complete.");
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception original)
        {
            string rollbackErrors = "";
            for (int i = 0; i < nodes.Length; i++)
            {
                // If an enable attempt fails, keep enabling all the remaining devices.
                uint result = (!enabled && previouslyDisabled[i]) ? Disable(nodes[i], 4) : Enable(nodes[i], 0);
                if (result != 0) rollbackErrors += " Recovery " + devices[i] + ": " + result;
            }
            throw new InvalidOperationException(original.Message + rollbackErrors, original);
        }
    }
}
