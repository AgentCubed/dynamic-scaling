using Terraria.ModLoader;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.ModLoader.Config;
using Terraria.UI.Chat;
using System;
using Terraria.Localization;
using System.Collections.Generic;
using System.Linq;

namespace DynamicScaling
{
    public class DealCommand : ModCommand
    {
        public override string Command => "deal";
        public override CommandType Type => CommandType.Chat;
        public override string Usage => "/deal <amount> [playername]\n /deal (+|-) <amount> [playername]";
        public override string Description => "Set the DealDamage config value or player's DealDamageModifierDifference. Use separate + or - token for relative changes, e.g. '/deal + 2'.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (args.Length < 1 || args.Length > 3)
            {
                caller.Reply("Usage: " + Usage);
                return;
            }
            // Support two different syntaxes:
            // 1) /deal <amount> [playername]           -> assignment ("2" or "-2")
            // 2) /deal (+|-) <amount> [playername]     -> relative change ("+ 2" or "- 2")
            bool isRelative = false;
            int amount = 0;
            string playerName;

            if ((args[0] == "+" || args[0] == "-") )
            {
                // Syntax: /deal + 2 [player]
                if (args.Length < 2)
                {
                    caller.Reply("Usage: " + Usage);
                    return;
                }
                isRelative = true;
                if (!int.TryParse(args[1], out amount))
                {
                    caller.Reply("Invalid amount. Usage: " + Usage);
                    return;
                }
                if (args[0] == "-")
                    amount = -Math.Abs(amount);
                playerName = args.Length == 3 ? args[2] : null;
            }
            else
            {
                // Syntax: /deal <amount> [player]
                string amountStr = args[0];
                if (!int.TryParse(amountStr, out amount))
            {
                caller.Reply("Invalid amount. Usage: " + Usage);
                return;
            }
                playerName = args.Length == 2 ? args[1] : null;
            }

            var active = ModContent.GetInstance<ServerConfig>();
            var pending = ConfigManager.GeneratePopulatedClone(active) as ServerConfig;
            if (pending == null)
            {
                if (playerName != null)
                {
                    if (!active.PlayerOverrides.TryGetValue(playerName, out var tuning))
                    {
                        tuning = new ServerConfig.PlayerTuning();
                        active.PlayerOverrides[playerName] = tuning;
                    }
                    tuning.DealDamageModifierDifference = isRelative ? tuning.DealDamageModifierDifference + amount : amount;
                    caller.Reply($"Player {playerName} DealDamageModifierDifference set to {tuning.DealDamageModifierDifference}.");
                }
                else
                {
                    active.DealDamage = isRelative ? active.DealDamage + amount : amount;
                    caller.Reply($"DealDamage set to {active.DealDamage}.");
                }
                return;
            }

            if (playerName != null)
            {
                if (!pending.PlayerOverrides.TryGetValue(playerName, out var tuning))
                {
                    tuning = new ServerConfig.PlayerTuning();
                    pending.PlayerOverrides[playerName] = tuning;
                }
                tuning.DealDamageModifierDifference = isRelative ? tuning.DealDamageModifierDifference + amount : amount;
                active.SaveChanges(pending, (msg, color) => caller.Reply($"Player {playerName} DealDamageModifierDifference set to {tuning.DealDamageModifierDifference}."), silent: false, broadcast: true);
            }
            else
            {
                pending.DealDamage = isRelative ? pending.DealDamage + amount : amount;
                active.SaveChanges(pending, (msg, color) => caller.Reply($"DealDamage set to {pending.DealDamage}."), silent: false, broadcast: true);
            }
        }
    }

    public class TakeCommand : ModCommand
    {
        public override string Command => "take";
        public override CommandType Type => CommandType.Chat;
        public override string Usage => "/take <amount> [playername]\n /take (+|-) <amount> [playername]";
        public override string Description => "Set the TakeDamage config value or player's TakeDamageModifierDifference. Use separate + or - token for relative changes, e.g. '/take - 2'.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            if (args.Length < 1 || args.Length > 3)
            {
                caller.Reply("Usage: " + Usage);
                return;
            }
            // Support two different syntaxes:
            // 1) /take <amount> [playername]           -> assignment ("2" or "-2")
            // 2) /take (+|-) <amount> [playername]     -> relative change ("+ 2" or "- 2")
            bool isRelative = false;
            int amount = 0;
            string playerName;
            string amountStr;
            if ((args[0] == "+" || args[0] == "-") )
            {
                // Syntax: /take + 2 [player]
                if (args.Length < 2)
                {
                    caller.Reply("Usage: " + Usage);
                    return;
                }
                isRelative = true;
                if (!int.TryParse(args[1], out amount))
                {
                    caller.Reply("Invalid amount. Usage: " + Usage);
                    return;
                }
                if (args[0] == "-")
                    amount = -Math.Abs(amount);
                playerName = args.Length == 3 ? args[2] : null;
            }
            else
            {
                // Syntax: /take <amount> [player]
                amountStr = args[0];
                if (!int.TryParse(amountStr, out amount))
            {
                caller.Reply("Invalid amount. Usage: " + Usage);
                return;
            }
                playerName = args.Length == 2 ? args[1] : null;
            }

            var active = ModContent.GetInstance<ServerConfig>();
            var pending = ConfigManager.GeneratePopulatedClone(active) as ServerConfig;
            if (pending == null)
            {
                if (playerName != null)
                {
                    if (!active.PlayerOverrides.TryGetValue(playerName, out var tuning))
                    {
                        tuning = new ServerConfig.PlayerTuning();
                        active.PlayerOverrides[playerName] = tuning;
                    }
                    tuning.TakeDamageModifierDifference = isRelative ? tuning.TakeDamageModifierDifference + amount : amount;
                    caller.Reply($"Player {playerName} TakeDamageModifierDifference set to {tuning.TakeDamageModifierDifference}.");
                }
                else
                {
                    active.TakeDamage = isRelative ? active.TakeDamage + amount : amount;
                    caller.Reply($"TakeDamage set to {active.TakeDamage}.");
                }
                return;
            }

            if (playerName != null)
            {
                if (!pending.PlayerOverrides.TryGetValue(playerName, out var tuning))
                {
                    tuning = new ServerConfig.PlayerTuning();
                    pending.PlayerOverrides[playerName] = tuning;
                }
                tuning.TakeDamageModifierDifference = isRelative ? tuning.TakeDamageModifierDifference + amount : amount;
                active.SaveChanges(pending, (msg, color) => caller.Reply($"Player {playerName} TakeDamageModifierDifference set to {tuning.TakeDamageModifierDifference}."), silent: false, broadcast: true);
            }
            else
            {
                pending.TakeDamage = isRelative ? pending.TakeDamage + amount : amount;
                active.SaveChanges(pending, (msg, color) => caller.Reply($"TakeDamage set to {pending.TakeDamage}."), silent: false, broadcast: true);
            }
        }
    }

    public class DumpProgressionCommand : ModCommand
    {
        public override string Command => "dumpprogression";
        public override CommandType Type => CommandType.Chat;
        public override string Description => "Dump BossChecklist progression data to chat/console.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            var bossChecklistMod = ModLoader.GetMod("BossChecklist");
            if (bossChecklistMod == null)
            {
                caller.Reply("BossChecklist not loaded or not installed.");
                return;
            }

            object result = bossChecklistMod.Call("GetBossInfoDictionary", ModLoader.GetMod("DynamicScaling"), "2.0");
            if (result is string || result == null)
            {
                // Fallback to older version for compatibility
                result = bossChecklistMod.Call("GetBossInfoDictionary", ModLoader.GetMod("DynamicScaling"), "1.6");
            }
            if (result is string sResult)
            {
                caller.Reply($"BossChecklist returned: {sResult}");
                return;
            }
            // Normalize return types. The API returns a Dictionary<string, Dictionary<string, object>> as of newer versions
            var normalized = BossChecklist.NormalizeBossChecklistReturn(result);
            if (normalized == null)
            {
                caller.Reply("Unexpected BossChecklist return type: " + (result?.GetType().ToString() ?? "null"));
                return;
            }

            // Sort by progression value, if present
            var sorted = normalized.OrderBy(kv => {
                if (kv.Value != null && kv.Value.TryGetValue("progression", out var p))
                {
                    try { return Convert.ToSingle(p); }
                    catch { return float.MaxValue; }
                }
                return float.MaxValue;
            });

            caller.Reply($"BossChecklist entries: {normalized.Count}");
            foreach (var kv in sorted)
            {
                var key = kv.Key;
                var data = kv.Value as IDictionary<string, object>;
                float prog = float.NaN;
                string modsrc = "Unknown";
                string displayName = key;
                string typeStr = "Unknown";
                if (data != null)
                {
                    if (data.TryGetValue("progression", out var pVal))
                    {
                        try { prog = Convert.ToSingle(pVal); } catch { prog = float.NaN; }
                    }
                    if (data.TryGetValue("modSource", out var modVal) && modVal != null)
                        modsrc = modVal.ToString();
                    if (data.TryGetValue("displayName", out var nameVal) && nameVal != null)
                    {
                        if (nameVal is LocalizedText lt) displayName = lt.Value;
                        else displayName = nameVal.ToString();
                    }
                    if (data.TryGetValue("isBoss", out var b) && Convert.ToBoolean(b)) typeStr = "Boss";
                    else if (data.TryGetValue("isMiniboss", out var m) && Convert.ToBoolean(m)) typeStr = "MiniBoss";
                    else if (data.TryGetValue("isEvent", out var e) && Convert.ToBoolean(e)) typeStr = "Event";
                }

                caller.Reply($"{prog:0.00}\t{typeStr}\t[{modsrc}] {displayName} ({key})");
            }
        }
    }

        // Use BossChecklist.NormalizeBossChecklistReturn instead (shared helper)
    }
