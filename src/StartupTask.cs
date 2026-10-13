using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;

internal sealed class ComScope : IDisposable
{
    readonly List<object> owned = new List<object>();
    internal dynamic Own(object value) { owned.Add(value); return value; }
    public void Dispose()
    {
        for (int i = owned.Count - 1; i >= 0; i--)
            if (Marshal.IsComObject(owned[i])) Marshal.ReleaseComObject(owned[i]);
    }
}

internal enum StartupState { Disabled, Enabled, DifferentExecutable, DifferentConfiguration }

internal static class StartupTask
{
    static string TaskName { get { using (var user = WindowsIdentity.GetCurrent()) return "AutoHello-" + user.User.Value; } }

    internal static StartupState Classify(bool enabled, int type, string path, string arguments)
    {
        if (type != 0) return StartupState.DifferentConfiguration;
        if (!String.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return StartupState.DifferentExecutable;
        if (arguments != "--tray") return StartupState.DifferentConfiguration;
        return enabled ? StartupState.Enabled : StartupState.Disabled;
    }

    internal static StartupState GetState()
    {
        using (var com = new ComScope()) {
            dynamic service = com.Own(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")));
            service.Connect();
            try {
                dynamic folder = com.Own(service.GetFolder("\\"));
                dynamic task = com.Own(folder.GetTask(TaskName));
                dynamic definition = com.Own(task.Definition);
                dynamic actions = com.Own(definition.Actions);
                if (actions.Count != 1) return StartupState.DifferentConfiguration;
                dynamic action = com.Own(actions.Item(1));
                if (action.Type != 0) return StartupState.DifferentConfiguration;
                return Classify((bool)task.Enabled, (int)action.Type, (string)action.Path, (string)action.Arguments);
            }
            catch (COMException error) { if ((uint)error.HResult == 0x80070002) return StartupState.Disabled; throw; }
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!Program.IsAdministrator()) throw new UnauthorizedAccessException();
        using (var com = new ComScope()) {
            dynamic service = com.Own(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")));
            service.Connect();
            dynamic folder = com.Own(service.GetFolder("\\"));
            if (!enabled) {
                try { folder.DeleteTask(TaskName, 0); }
                catch (COMException error) { if ((uint)error.HResult != 0x80070002) throw; }
                return;
            }
            string sid;
            using (var user = WindowsIdentity.GetCurrent()) sid = user.User.Value;
            dynamic task = CreateDefinition(service, sid, com);
            com.Own(folder.RegisterTaskDefinition(TaskName, task, 6, sid, null, 3, null));
        }
    }

    static dynamic CreateDefinition(dynamic service, string sid, ComScope com)
    {
        dynamic task = com.Own(service.NewTask(0));
        dynamic registration = com.Own(task.RegistrationInfo);
        dynamic principal = com.Own(task.Principal);
        dynamic settings = com.Own(task.Settings);
        dynamic triggers = com.Own(task.Triggers);
        dynamic actions = com.Own(task.Actions);
        registration.Description = "Start AutoHello in the tray at sign-in for this user.";
        principal.UserId = sid;
        principal.LogonType = 3; // Interactive token; no password stored.
        principal.RunLevel = 1; // Highest available privileges.
        settings.Enabled = true;
        settings.DisallowStartIfOnBatteries = false;
        settings.StopIfGoingOnBatteries = false;
        settings.ExecutionTimeLimit = "PT0S";
        settings.MultipleInstances = 2; // Ignore a duplicate scheduled start.
        dynamic trigger = com.Own(triggers.Create(9)); // Logon.
        trigger.UserId = sid;
        dynamic action = com.Own(actions.Create(0));
        action.Path = Application.ExecutablePath;
        action.Arguments = "--tray";
        action.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
        return task;
    }

    internal static void VerifyDefinition()
    {
        // Build a definition in memory only. Do not register or run a task.
        using (var com = new ComScope()) {
            dynamic service = com.Own(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")));
            service.Connect();
            string sid;
            using (var user = WindowsIdentity.GetCurrent()) sid = user.User.Value;
            dynamic task = CreateDefinition(service, sid, com);
            var xml = new System.Xml.XmlDocument();
            xml.LoadXml((string)task.XmlText);
            var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            if (xml.SelectSingleNode("//t:RunLevel", ns).InnerText != "HighestAvailable" ||
                xml.SelectSingleNode("//t:LogonType", ns).InnerText != "InteractiveToken" ||
                xml.SelectSingleNode("//t:LogonTrigger/t:UserId", ns).InnerText != sid ||
                xml.SelectSingleNode("//t:Exec/t:Arguments", ns).InnerText != "--tray" ||
                xml.SelectSingleNode("//t:Exec/t:Command", ns).InnerText != Application.ExecutablePath)
                throw new InvalidOperationException("Startup definition validation failed.");
        }
    }
}
