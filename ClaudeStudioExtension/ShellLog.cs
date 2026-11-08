using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeStudioExtension;

// The "Claude Code Studio: Shell" Output pane: every command the persistent
// shell runs and what it returned (agent "shell-log" chunks). Never takes focus.
internal static class ShellLog
{
    private static readonly Guid PaneGuid = new("8d1f4c2e-5b3a-4e7f-9a61-2c7d0e9b4f58");
    private const string PaneName = "Claude Code Studio: Shell";

    private static IVsOutputWindowPane? _pane;

    [SuppressMessage("Usage", "VSTHRD010", Justification = "Lambda runs on UI thread via SwitchToMainThreadAsync.")]
    public static void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var block = text.EndsWith("\n", StringComparison.Ordinal) ? text : text + Environment.NewLine;
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                if (_pane == null)
                {
                    if (Package.GetGlobalService(typeof(SVsOutputWindow)) is not IVsOutputWindow window) return;
                    var guid = PaneGuid;
                    if (ErrorHandler.Failed(window.GetPane(ref guid, out _pane)) || _pane == null)
                    {
                        window.CreatePane(ref guid, PaneName, fInitVisible: 1, fClearWithSolution: 0);
                        window.GetPane(ref guid, out _pane);
                    }
                }
                _pane?.OutputStringThreadSafe(block);
            }
            catch { /* never throw from logging */ }
        });
    }
}
