namespace TimerResTuner;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var build = BuildInfo.Current;

        if (args.Length >= 2 && args[0] == "--hold" && uint.TryParse(args[1], out var units))
        {
            // Launched (usually by the logon task) after a test build expired: don't hold the timer,
            // and remove the logon task so it doesn't keep launching a dead exe on every logon.
            if (build.IsExpired(DateTime.UtcNow))
            {
                SystemConfig.RemoveStartupTask();
                return 3;
            }

            return Holder.Run(units);
        }

        if (args.Length >= 1 && args[0] == "--stop")
        {
            Holder.Stop(); // always allowed: it only undoes
            return 0;
        }

        // An expired test build still opens if it has something to undo, so the tester can put
        // everything back. With nothing applied there is nothing to show.
        if (build.IsExpired(DateTime.UtcNow) && !SystemConfig.HasAnythingToUndo())
        {
            MessageBox.Show(
                build.Describe(DateTime.UtcNow) + "\n\nAsk for a new test build.",
                "TimerResTuner — test build expired",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
