using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;

namespace AxialSqlTools
{
    internal static class ShortcutManager
    {
        private static readonly Guid QueryHistoryCommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");
        private const int QueryHistoryCommandId = 4144;
        private const int ScriptObjectCommandId = 4134;

        public static bool ApplyQueryHistoryShortcut(string shortcut, out string error)
            => ApplyShortcut(QueryHistoryCommandId, "Query History", shortcut, out error);

        public static bool ApplyScriptObjectShortcut(string shortcut, out string error)
            => ApplyShortcut(ScriptObjectCommandId, "Script Object Definition", shortcut, out error);

        private static bool ApplyShortcut(int commandId, string commandName, string shortcut, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte == null) throw new InvalidOperationException("SSMS automation service is unavailable.");

                Command target = null;
                foreach (Command command in dte.Commands)
                {
                    if (command.ID == commandId && Guid.TryParse(command.Guid, out Guid commandGuid) && commandGuid == QueryHistoryCommandSet)
                    {
                        target = command;
                        break;
                    }
                }
                if (target == null) throw new InvalidOperationException("The " + commandName + " command is not registered yet.");

                target.Bindings = string.IsNullOrWhiteSpace(shortcut)
                    ? new object[0]
                    : new object[] { "Global::" + shortcut.Trim() };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
