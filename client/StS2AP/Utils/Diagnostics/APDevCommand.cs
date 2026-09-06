using Archipelago.MultiClient.Net;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using StS2AP.UI;

namespace StS2AP.Utils
{
    // StS 2 picks up these reflectively out of the mods without problem.
    public class APDevCommand : AbstractConsoleCmd
    {
        public override string CmdName => "ap";

        public override string Args =>
            "report | !command | state [summary|lobby|run|ledger|grants|assignments|multiplayer|grant <slot:index>]";

        public override string Description =>
            "ap report exports the newest divergence ZIP and game logs; also supports AP server commands and state inspection";

        public override bool IsNetworked => false;

        public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
        {
            if (args.Length <= 1)
                return CompleteArgument(["report", "state"], [], args.FirstOrDefault() ?? "", CompletionType.Subcommand);
            if (args.Length == 2 && args[0].Equals("state", StringComparison.OrdinalIgnoreCase))
                return CompleteArgument(ApDevStateProviders.Names, [args[0]], args[1]);
            return base.GetArgumentCompletions(player, args);
        }

        public override CmdResult Process(Player? issuingPlayer, string[] args)
        {
            if (args.Length == 0)
            {
                return new CmdResult(false, "Usage: ap report | ap !command | ap state [section]");
            }

            if (args[0].Equals("report", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 1)
                    return new CmdResult(false, "Usage: ap report (exports the newest divergence report and opens its folder)");
                bool started = ApBugReport.TryStart(out string message);
                return new CmdResult(started, message);
            }

            if (args[0].StartsWith("!", StringComparison.Ordinal))
            {
                string sendMe = string.Join(" ", args);
                ArchipelagoSession? session = ArchipelagoClient.Session;
                if (!ArchipelagoClient.IsConnected || session == null)
                    return new CmdResult(false, "Not connected to AP");
                session.Say(sendMe);
                return new CmdResult(true);
            }

            if (!args[0].Equals("state", StringComparison.OrdinalIgnoreCase))
            {
                return new CmdResult(
                    false,
                    "Unknown AP command. Use ap report, ap !command or ap state [section]."
                );
            }

            string section = args.Length >= 2 ? args[1] : "summary";
            string[] sectionArgs = args.Skip(2).ToArray();
            if (!ApDevStateProviders.TryCapture(
                section,
                sectionArgs,
                out string output,
                out string error))
            {
                return new CmdResult(false, error);
            }
            return new CmdResult(true, output);
        }
    }

    /// <summary>
    /// Toggles the live counters used to debug progressive Relic receipt/bank behavior.
    /// </summary>
    public class APRelicDebugCommand : AbstractConsoleCmd
    {
        public override string CmdName => "aprelicdebug";

        public override string Args => "[on|off]";

        public override string Description => "Toggles the AP Relic receipt/bank debug overlay";

        public override bool IsNetworked => false;

        public override CmdResult Process(Player? issuingPlayer, string[] args)
        {
            bool shouldShow;
            if (args.Length == 0)
            {
                shouldShow = !RelicRewardDebugUI.IsVisible;
            }
            else if (args.Length == 1 && args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                shouldShow = true;
            }
            else if (args.Length == 1 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                shouldShow = false;
            }
            else
            {
                return new CmdResult(false, "Usage: aprelicdebug [on|off]");
            }

            if (shouldShow)
                RelicRewardDebugUI.Show();
            else
                RelicRewardDebugUI.Hide();

            if (shouldShow && !RelicRewardDebugUI.IsVisible)
                return new CmdResult(false, "Could not create the AP Relic debug overlay; check the log.");

            return new CmdResult(
                true,
                $"AP Relic debug overlay {(RelicRewardDebugUI.IsVisible ? "enabled" : "disabled")}."
            );
        }
    }
}
