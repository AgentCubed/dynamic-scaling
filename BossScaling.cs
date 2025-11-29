using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Localization;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.DataStructures;
using System.IO;
using Terraria.ModLoader.IO;

namespace DynamicScaling
{
    public class BossScaling : GlobalNPC
    {
        private const double DefaultExpectedMinutes = 4.0;
        private const double DeadZoneDivisor = 5.0;
        private const int HitPointBuckets = 10;
        private const int FullHealthPercent = 100;
        private const int AggroBoost = 1500;
        private const int HealthThreshold = 20;

        private static int totalBossDeaths;
        // Historically we used a dict to store original aggro but now use an additive approach with boostedPlayers
        private System.Collections.Generic.HashSet<int> boostedPlayers = new System.Collections.Generic.HashSet<int>();
        // Intentionally not used: leave for future state tracking for multiple boost cases
        private double deadZoneMinutes = 2.0;
        private const double SCALING_CONSTANT = 1.0;
        private double idealTotalTicks = 12.0 * 3600.0;

        public override bool InstancePerEntity => true;

        public double spawnTime = -1;
        public double currentDefenseModifier = 1.0;
        public double currentOffenseModifier = 1.0;
        public int lastHpInterval = 100;
        public double lastTimeDifference = 0.0;
        public bool isScalingDisabled = false;

        // Player+Weapon adaptation state
        private System.Collections.Generic.Dictionary<(int playerId, int weaponKey), double> comboDamagePhase = new System.Collections.Generic.Dictionary<(int, int), double>();
        private System.Collections.Generic.Dictionary<(int playerId, int weaponKey), double> comboDamageRunning = new System.Collections.Generic.Dictionary<(int, int), double>();
        private const double PhaseAvgAlpha = 0.4;
        private System.Collections.Generic.Dictionary<(int playerId, int weaponKey), float> comboAdaptationFactor = new System.Collections.Generic.Dictionary<(int, int), float>();
        private System.Collections.Generic.HashSet<(int playerId, int weaponKey)> comboAdaptationWarned = new System.Collections.Generic.HashSet<(int, int)>();
        private double lastPhaseTime = 0.0;

        public override void OnSpawn(NPC npc, IEntitySource source)
        {
            if (npc.boss)
            {
                ResetForNewBoss();
                ApplyConfigOverrides();
                currentDefenseModifier = 1.0;
                currentOffenseModifier = 1.0;
                lastHpInterval = 100;
                lastTimeDifference = 0.0;

                var config = ModContent.GetInstance<ServerConfig>();
                try
                {
                    if (config != null && config.ExpectedTotalMinutes > 0)
                    {
                        idealTotalTicks = config.ExpectedTotalMinutes * 3600.0;
                        deadZoneMinutes = config.ExpectedTotalMinutes / 5.0;
                    }
                    else
                    {
                        idealTotalTicks = 4.0 * 3600.0;
                        deadZoneMinutes = 4.0 / 5.0;
                    }
                    if (config?.DebugMode == true)
                        Main.NewText($"Boss {npc.FullName} spawned. Expected time: {config?.ExpectedTotalMinutes ?? 4} min", Color.Gray);
                }
                catch
                {
                    idealTotalTicks = 4.0 * 3600.0;
                    deadZoneMinutes = 4.0 / 5.0;
                    if (config?.DebugMode == true)
                        Main.NewText($"Boss {npc.FullName} spawned. Using default expected time: 4 min", Color.Gray);
                }
                isScalingDisabled = (config?.ExpectedTotalMinutes == 0);

                // If configured, disable time scaling for bosses with progression less than threshold (from BossChecklist)
                try
                {
                    if (!isScalingDisabled && config?.BossProgressionThresholdValue > 0)
                    {
                        double? prog = BossChecklist.GetBossChecklistProgressionForNPC(npc.type);
                        if (prog.HasValue && prog.Value < config.BossProgressionThresholdValue)
                        {
                            isScalingDisabled = true;
                            if (config.DebugMode)
                                Utils.EmitDebug($"[Boss] Scaling disabled for {npc.FullName} (progression {prog.Value} < threshold {config.BossProgressionThresholdValue})", Color.Yellow);
                        }
                    }
                }
                catch { }

                // Sync scaling disabled value to clients
                try
                {
                    if (Main.netMode == NetmodeID.Server)
                    {
                        BossSyncPacket.SendScalingDisabledForNPC(npc.whoAmI, isScalingDisabled);
                        BossSyncPacket.SendBossModifiersForNPC(npc.whoAmI, (float)currentDefenseModifier, (float)currentOffenseModifier);
                    }
                }
                catch { }

                // Register for group-based tracking only if this boss has a special boss bar
                try { BossGroupTracker.RegisterBoss(npc); } catch { }
                BossBar.ClearCache();
            }
        }

        // Include scaling disabled state in NPC spawn extra AI so clients receive initial value before damage calculation can occur
        public override void SendExtraAI(NPC npc, BitWriter bitWriter, BinaryWriter binaryWriter)
        {
            try
            {
                if (!npc.boss) return;
                // Only the server writes extra data; clients don't need to send it.
                if (Main.netMode == NetmodeID.Server)
                {
                    binaryWriter.Write(isScalingDisabled);
                }
            }
            catch { }
        }

        public override void ReceiveExtraAI(NPC npc, BitReader bitReader, BinaryReader binaryReader)
        {
            try
            {
                if (!npc.boss) return;
                // When we receive spawn extra AI, the server may have indicated scaling is disabled
                bool disabled = false;
                try { disabled = binaryReader.ReadBoolean(); } catch { }
                SetClientScalingDisabled(npc.whoAmI, disabled);
                // If instance exists, update instance field as well
                isScalingDisabled = disabled;
            }
            catch { }
        }

        // Client-side cache for network-synced modifiers and adaptation factors.
        private static Dictionary<int, (float defenseModifier, float offenseModifier)> clientModifiers = new Dictionary<int, (float, float)>();
        private static Dictionary<int, Dictionary<(int playerId, int weaponKey), float>> clientAdaptationFactors = new Dictionary<int, Dictionary<(int, int), float>>();
        private static Dictionary<int, bool> clientScalingDisabled = new Dictionary<int, bool>();

        public static bool TryGetClientModifiers(int npcWhoAmI, out float defense, out float offense)
        {
            defense = 1f; offense = 1f;
            if (clientModifiers.TryGetValue(npcWhoAmI, out var tup))
            {
                defense = tup.defenseModifier;
                offense = tup.offenseModifier;
                return true;
            }
            return false;
        }

        public static void SetClientScalingDisabled(int npcWhoAmI, bool disabled)
        {
            clientScalingDisabled[npcWhoAmI] = disabled;
        }

        public static bool TryGetClientScalingDisabled(int npcWhoAmI, out bool disabled)
        {
            disabled = false;
            if (clientScalingDisabled.TryGetValue(npcWhoAmI, out bool val))
            {
                disabled = val;
                return true;
            }
            return false;
        }

        public static void SetClientModifiers(int npcWhoAmI, float defense, float offense)
        {
            clientModifiers[npcWhoAmI] = (defense, offense);
        }

        public static void SetClientAdaptationFactor(int npcWhoAmI, (int playerId, int weaponKey) key, float factor)
        {
            if (!clientAdaptationFactors.TryGetValue(npcWhoAmI, out var dict))
            {
                dict = new Dictionary<(int, int), float>();
                clientAdaptationFactors[npcWhoAmI] = dict;
            }
            dict[key] = factor;
        }

        public static bool TryGetClientAdaptationFactor(int npcWhoAmI, (int playerId, int weaponKey) key, out float factor)
        {
            factor = 1f;
            if (clientAdaptationFactors.TryGetValue(npcWhoAmI, out var dict) && dict.TryGetValue(key, out float val))
            {
                factor = val;
                return true;
            }
            return false;
        }

        public static void ClearClientCaches()
        {
            clientModifiers.Clear();
            clientAdaptationFactors.Clear();
            clientScalingDisabled.Clear();
        }

        public static void RemoveClientScalingDisabled(int npcWhoAmI)
        {
            if (clientScalingDisabled.ContainsKey(npcWhoAmI))
                clientScalingDisabled.Remove(npcWhoAmI);
        }

        // Server-side helper called by the BossSyncPacket when clients report damage.
        // This finds the NPC and applies the recorded combo damage into that NPC's scaling instance.
        public static void ReportComboDamage(int npcWhoAmI, int playerId, int weaponKey, int amount)
        {
            if (Main.netMode != NetmodeID.Server) return;
            if (npcWhoAmI < 0 || npcWhoAmI >= Main.maxNPCs) return;
            var npc = Main.npc[npcWhoAmI];
            if (npc == null || !npc.active || !npc.boss) return;
            var g = npc.GetGlobalNPC<BossScaling>();
            if (g == null) return;
            g.RecordComboDamage(npc, playerId, weaponKey, amount);
        }

        public static int TotalBossDeaths => totalBossDeaths;

        public int PlayerDeathsThisFight { get; private set; }

        public static void RecordBossDeath()
        {
            totalBossDeaths++;

            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (!npc.active || !npc.boss)
                {
                    continue;
                }

                npc.GetGlobalNPC<BossScaling>().PlayerDeathsThisFight++;
            }
        }

        public static int GetCurrentFightDeathCount()
        {
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (!npc.active || !npc.boss)
                {
                    continue;
                }

                return npc.GetGlobalNPC<BossScaling>().PlayerDeathsThisFight;
            }

            return 0;
        }

        public override void OnKill(NPC npc)
        {
            if (npc.boss)
            {
                BossBar.ClearCache();
                try { BossGroupTracker.CleanupDeadNPC(npc.whoAmI); } catch { }
            }
        }

        private void ResetForNewBoss()
        {
            spawnTime = Main.time;
            currentDefenseModifier = 1.0;
            currentOffenseModifier = 1.0;
            lastHpInterval = FullHealthPercent;
            lastTimeDifference = 0.0;
            PlayerDeathsThisFight = 0;
            SetDefaultConfigValues();
            comboDamagePhase.Clear();
            comboDamageRunning.Clear();
            comboAdaptationFactor.Clear();
            comboAdaptationWarned.Clear();
            lastPhaseTime = spawnTime;
        }

        private void ApplyConfigOverrides()
        {
            try
            {
                var config = ModContent.GetInstance<ServerConfig>();
                if (config?.ExpectedTotalMinutes > 0)
                {
                    idealTotalTicks = config.ExpectedTotalMinutes * 3600.0;
                    deadZoneMinutes = config.ExpectedTotalMinutes / DeadZoneDivisor;
                }
            }
            catch
            {
                SetDefaultConfigValues();
            }
        }

        private void SetDefaultConfigValues()
        {
            idealTotalTicks = DefaultExpectedMinutes * 3600.0;
            deadZoneMinutes = DefaultExpectedMinutes / DeadZoneDivisor;
        }

        public override void ModifyIncomingHit(NPC npc, ref NPC.HitModifiers modifiers)
        {
            if (!npc.boss || spawnTime < 0)
            {
                return;
            }

            // Prefer client-side cached flag for clients; server uses instance flag assigned on spawn
            if (Main.netMode == NetmodeID.MultiplayerClient && TryGetClientScalingDisabled(npc.whoAmI, out bool clientDisabled) && clientDisabled)
            {
                return;
            }

            if (isScalingDisabled)
            {
                return;
            }

            double timeAlive = Main.time - spawnTime;

            if (timeAlive <= 60)
            {
                return;
            }

            double hpPercent;
            if (BossBar.TryGetBossHealth(npc.whoAmI, out float life, out float lifeMax) && lifeMax > 0)
            {
                hpPercent = life / lifeMax;
            }
            else
            {
                hpPercent = npc.life / (double)npc.lifeMax;
            }

            int currentHpInterval = (int)Math.Floor(hpPercent * 10) * 10;

            if (currentHpInterval < lastHpInterval)
            {
                UpdatePaceModifiers(npc, timeAlive, currentHpInterval, hpPercent);
                EvaluateWeaponAdaptationOnInterval(npc);
            }

            ApplyDamageModifiers(ref modifiers);

            // Apply Expected Players scaling (Nerf boss damage if fewer players than expected)
            var config = ModContent.GetInstance<ServerConfig>();
            if (config != null && config.ExpectedPlayers > 1)
            {
                int nearbyPlayers = BossProximityCache.GetNearbyPlayerCount(npc);
                if (nearbyPlayers < config.ExpectedPlayers)
                {
                    float diff = config.ExpectedPlayers - nearbyPlayers;
                    float factor = config.ScalingMultiplier * (diff * diff) + 1f;
                    modifiers.FinalDamage /= factor;
                    
                    if (config.DebugMode)
                    {
                        // Main.NewText($"Nerfing damage: {nearbyPlayers}/{config.ExpectedPlayers} players. Factor: {factor:F2}", Color.Orange);
                    }
                }
            }
        }

        public override void ModifyHitByItem(NPC npc, Player player, Item item, ref NPC.HitModifiers modifiers)
        {
            if (!npc.boss)
            {
                return;
            }

            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.WeaponAdaptationEnabled != true)
            {
                return;
            }

            int weaponKey = GetWeaponKeyFromItem(item);
            int playerId = player?.whoAmI ?? -1;
            if (playerId >= 0)
            {
                var comboKey = (playerId, weaponKey);
                if (comboAdaptationFactor.TryGetValue(comboKey, out float factor) && factor < 1f)
                {
                    modifiers.FinalDamage *= factor;
                }
            }
        }

        public override void ModifyHitByProjectile(NPC npc, Projectile projectile, ref NPC.HitModifiers modifiers)
        {
            if (!npc.boss)
            {
                return;
            }

            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.WeaponAdaptationEnabled != true)
            {
                return;
            }

            if (projectile != null && projectile.TryGetOwner(out Player owner) && owner != null)
            {
                int weaponKey = GetWeaponKeyFromProjectile(projectile);
                int playerId = owner.whoAmI;
                var comboKey = (playerId, weaponKey);
                if (comboAdaptationFactor.TryGetValue(comboKey, out float factor) && factor < 1f)
                {
                    modifiers.FinalDamage *= factor;
                }
            }
        }

        private void UpdatePaceModifiers(NPC npc, double timeAlive, int currentHpInterval, double hpPercent)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            if (config == null)
                return;

            double hpLost = 1.0 - currentHpInterval / (double)FullHealthPercent;
            double idealTime = idealTotalTicks * hpLost;
            double timeDifferenceMinutes = (timeAlive - idealTime) / 3600.0;
            lastTimeDifference = timeDifferenceMinutes;

            double absTimeDiff = Math.Abs(timeDifferenceMinutes);

            double oldDefense = currentDefenseModifier;
            double oldOffense = currentOffenseModifier;

            if (absTimeDiff <= deadZoneMinutes)
            {
                currentDefenseModifier = 1.0;
                currentOffenseModifier = 1.0;
            }
            else
            {
                double scaledDifference = absTimeDiff - deadZoneMinutes;
                double modifier = 1.0 + SCALING_CONSTANT * Math.Pow(scaledDifference, 2);
                modifier = Math.Min(modifier, (double)config.MaxDefenseModifier);

                if (timeDifferenceMinutes > 0)
                {
                    currentOffenseModifier = modifier;
                    currentDefenseModifier = 1.0;
                }
                else
                {
                    currentDefenseModifier = modifier;
                    currentOffenseModifier = 1.0;
                }
            }

            // Emit per-NPC debug at the 10% interval boundary
            if (config?.DebugMode == true)
            {
                EmitDebugText(npc, hpPercent);
            }

            lastHpInterval = currentHpInterval;
        }

        private void EvaluateWeaponAdaptationOnInterval(NPC npc)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.WeaponAdaptationEnabled != true)
            {
                return;
            }

            var keys = comboDamagePhase.Keys.ToList();
            if (keys.Count <= 0)
            {
                if (config?.DebugMode == true)
                {
                    Main.NewText($"{npc.GivenOrTypeName} reached 10% HP but NO damage was recorded this phase", Color.Red);
                }
                lastPhaseTime = Main.time;
                return;
            }

            bool phaseTooFast = currentDefenseModifier > 1.0;

            if (config?.DebugMode == true)
            {
                Main.NewText($"{npc.GivenOrTypeName} evaluating weapon adaptation: {keys.Count} combos, phaseTooFast={phaseTooFast}", Color.Gray);
            }

            foreach (var key in keys)
            {
                double curPhaseDamage = comboDamagePhase.TryGetValue(key, out double val) ? val : 0.0;
                double prevRunning = comboDamageRunning.TryGetValue(key, out double prev) ? prev : 0.0;
                double newRunning = (1.0 - PhaseAvgAlpha) * prevRunning + PhaseAvgAlpha * curPhaseDamage;
                comboDamageRunning[key] = newRunning;
            }

            var runningKeys = comboDamageRunning.Keys.ToList();
            foreach (var key in runningKeys)
            {
                CheckAdaptationForComboRunning(npc, key, phaseTooFast);
            }

            if (config?.DebugMode == true)
            {
                EmitComboAdaptationProximity(npc);
            }

            comboDamagePhase.Clear();
            lastPhaseTime = Main.time;
        }

        private void CheckAdaptationForComboRunning(NPC npc, (int playerId, int weaponKey) comboKey, bool phaseTooFast)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            if (config == null)
            {
                return;
            }

            if (!comboDamageRunning.TryGetValue(comboKey, out double comboRunning))
            {
                return;
            }

            int countRunning = comboDamageRunning.Count;
            if (countRunning <= 1)
            {
                return;
            }

            double totalRunning = 0.0;
            foreach (var v in comboDamageRunning.Values) totalRunning += v;
            double meanOthers = (totalRunning - comboRunning) / Math.Max(1, countRunning - 1);
            if (meanOthers <= 0.0)
            {
                return;
            }

            double startMultiplier = config.WeaponAdaptationStartMultiplier;
            double completeMultiplier = config.WeaponAdaptationCompleteMultiplier;
            double minDamage = config.WeaponAdaptationMinDamage;
            double maxReduction = config.WeaponAdaptationMaxReduction;

            if (comboRunning < minDamage)
            {
                return;
            }

            double ratio = comboRunning / meanOthers;

            if (ratio >= startMultiplier && !comboAdaptationWarned.Contains(comboKey) && !comboAdaptationFactor.ContainsKey(comboKey))
            {
                comboAdaptationWarned.Add(comboKey);
                string playerName = GetPlayerName(comboKey.playerId);
                string weaponName = GetWeaponNameFromKey(comboKey.weaponKey);
                Main.NewText(Language.GetTextValue("Mods.DynamicScaling.Messages.BossBeginningAdaptation", npc.GivenOrTypeName, playerName, weaponName), Color.Orange);
            }

            if (ratio >= completeMultiplier && phaseTooFast)
            {
                float factor = (float)Math.Max(maxReduction, Math.Min(1.0, meanOthers / comboRunning));
                if (comboAdaptationFactor.TryGetValue(comboKey, out float existingFactor))
                {
                    if (factor < existingFactor - 0.001f)
                    {
                        comboAdaptationFactor[comboKey] = factor;
                        string playerName = GetPlayerName(comboKey.playerId);
                        string weaponName = GetWeaponNameFromKey(comboKey.weaponKey);
                        Main.NewText(Language.GetTextValue("Mods.DynamicScaling.Messages.BossAdapted", npc.GivenOrTypeName, playerName, weaponName), Color.Yellow);
                    }
                }
                else
                {
                    comboAdaptationFactor[comboKey] = factor;
                    string playerName = GetPlayerName(comboKey.playerId);
                    string weaponName = GetWeaponNameFromKey(comboKey.weaponKey);
                    Main.NewText(Language.GetTextValue("Mods.DynamicScaling.Messages.BossAdapted", npc.GivenOrTypeName, playerName, weaponName), Color.Yellow);
                }
            }
            else
            {
                if (config?.DebugMode == true)
                {
                    string playerName = GetPlayerName(comboKey.playerId);
                    string weaponName = GetWeaponNameFromKey(comboKey.weaponKey);
                    string reason = ratio < completeMultiplier ? $"ratio {ratio:F2} < {completeMultiplier:F2}" : "phase not too fast";
                    Main.NewText($"{npc.GivenOrTypeName} did not adapt to {playerName}'s {weaponName}: {reason}", Color.Gray);
                }
            }
        }

        private void EmitComboAdaptationProximity(NPC npc)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            if (config == null || comboDamagePhase.Count <= 0)
            {
                return;
            }
            int comboCount = comboDamagePhase.Count;
            if (comboCount <= 1)
            {
                if (config?.DebugMode == true)
                    Main.NewText($"{npc.GivenOrTypeName} adaptation: not enough combos to evaluate proximity (count={comboCount})", Color.Gray);
                return;
            }

            double startMultiplier = config.WeaponAdaptationStartMultiplier;
            double completeMultiplier = config.WeaponAdaptationCompleteMultiplier;

            double phaseDurationTicks = Math.Max(1.0, Main.time - lastPhaseTime);
            double phaseDurationMinutes = phaseDurationTicks / 3600.0;
            double totalPhase = 0.0;
            foreach (var v in comboDamagePhase.Values) totalPhase += v;
            foreach (var kvp in comboDamagePhase)
            {
                var key = kvp.Key;
                double comboDmg = kvp.Value;
                double meanOthers = (totalPhase - comboDmg) / Math.Max(1, comboCount - 1);
                if (meanOthers <= 0.0)
                {
                    continue;
                }

                double ratio = comboDmg / meanOthers;
                double closeness = Math.Min(100.0, (ratio / completeMultiplier) * 100.0);
                string playerName = GetPlayerName(key.playerId);
                string weaponName = GetWeaponNameFromKey(key.weaponKey);

                double dmgPerMinute = comboDmg / Math.Max(phaseDurationMinutes, 1e-6);
                string proximityMsg = $"{playerName}'s {weaponName}: ratio={ratio:F2} (start={startMultiplier:F2},complete={completeMultiplier:F2}) {closeness:F1}% to complete, {comboDmg:F0} dmg this phase (~{dmgPerMinute:F1}/min)";
                if (config?.DebugMode == true)
                    Main.NewText($"{npc.GivenOrTypeName} adaptation check: {proximityMsg}", Color.Gray);
            }
        }

        private void RecordComboDamage(NPC npc, int playerId, int weaponKey, int amount)
        {
            if (playerId < 0 || weaponKey == 0 || amount <= 0)
            {
                return;
            }

            var key = (playerId, weaponKey);
            if (comboDamagePhase.TryGetValue(key, out double curPhase))
            {
                comboDamagePhase[key] = curPhase + amount;
            }
            else
            {
                comboDamagePhase[key] = amount;
            }
        }

        private int GetWeaponKeyFromItem(Item item)
        {
            if (item == null)
            {
                return ItemID.None;
            }

            return item.type + 1; // positive key for items
        }

        private int GetWeaponKeyFromProjectile(Projectile projectile)
        {
            if (projectile == null)
            {
                return 0;
            }

            if (projectile.TryGetOwner(out Player player) && player != null && player.HeldItem != null && player.HeldItem.type != ItemID.None)
            {
                return player.HeldItem.type + 1;
            }

            return -(projectile.type + 1);
        }

        private string GetWeaponNameFromKey(int key)
        {
            if (key > 0)
            {
                int itemType = key - 1;
                return Lang.GetItemNameValue(itemType);
            }
            int projType = -key - 1;
            return Lang.GetProjectileName(projType).Value;
        }

        private string GetPlayerName(int playerId)
        {
            if (playerId >= 0 && playerId < Main.maxPlayers)
            {
                var p = Main.player[playerId];
                if (p != null && p.active)
                {
                    return p.name;
                }
            }
            return "Unknown";
        }

        private void EmitDebugText(NPC npc, double hpPercent)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            string paceMessage = "On Pace";
            double displayDefMod = 1.0; // What we show as the boss defense multiplier

            // If we are a multiplayer client, prefer the server-synced modifiers from the cache
            if (Main.netMode == NetmodeID.MultiplayerClient && TryGetClientModifiers(npc.whoAmI, out float cdef, out float coff))
            {
                // Use the client-cached defense modifier from the server for display
                if (coff > 1.0f)
                {
                    paceMessage = $"Pace: +{lastTimeDifference:F1} min";
                    displayDefMod = 1.0 / coff;
                }
                else if (cdef > 1.0f)
                {
                    paceMessage = $"Pace: {lastTimeDifference:F1} min";
                    displayDefMod = cdef;
                }
                if (config?.DebugMode == true)
                    Main.NewText($"(SYNC) {(int)(hpPercent * FullHealthPercent)}% HP | {paceMessage} | {displayDefMod:F2}x Def", Color.Gray);
                return;
            }

            // If scaling disabled on the client use cached value for debug display
            if (Main.netMode == NetmodeID.MultiplayerClient && TryGetClientScalingDisabled(npc.whoAmI, out bool clientDisabled) && clientDisabled)
            {
                if (config?.DebugMode == true)
                    Main.NewText($"(SYNC) {(int)(hpPercent * FullHealthPercent)}% HP | Scaling disabled via server", Color.Gray);
                return;
            }

                if (currentOffenseModifier > 1.0)
                {
                    // Player is slow: we increase player damage via currentOffenseModifier.
                    // For debug we want to show the equivalent boss defense multiplier (<1.0).
                    paceMessage = $"Pace: +{lastTimeDifference:F1} min";
                    displayDefMod = 1.0 / currentOffenseModifier;
                }
                else if (currentDefenseModifier > 1.0)
                {
                    // Player is fast: boss defense is increased (player deals less).
                    paceMessage = $"Pace: {lastTimeDifference:F1} min";
                    displayDefMod = currentDefenseModifier;
                }

            if (config?.DebugMode == true)
                Main.NewText($"{(int)(hpPercent * FullHealthPercent)}% HP | {paceMessage} | {displayDefMod:F2}x Def", Color.Gray);
        }

        private void ApplyDamageModifiers(ref NPC.HitModifiers modifiers)
        {
            modifiers.FinalDamage /= (float)currentDefenseModifier;
            modifiers.FinalDamage *= (float)currentOffenseModifier;
        }

        public override void OnHitByProjectile(NPC npc, Projectile projectile, NPC.HitInfo hit, int damageDone)
        {
            if (!npc.boss)
            {
                return;
            }

            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.WeaponAdaptationEnabled != true)
            {
                return;
            }

            if (projectile != null && projectile.TryGetOwner(out Player owner) && owner != null)
            {
                int weaponKey = GetWeaponKeyFromProjectile(projectile);
                int playerId = owner.whoAmI;
                RecordComboDamage(npc, playerId, weaponKey, damageDone);
            }
        }

        public override void OnHitByItem(NPC npc, Player player, Item item, NPC.HitInfo hit, int damageDone)
        {
            if (!npc.boss)
            {
                return;
            }

            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.WeaponAdaptationEnabled != true)
            {
                if (config?.DebugMode == true)
                {
                    Main.NewText($"Weapon adaptation is disabled (WeaponAdaptationEnabled = {config?.WeaponAdaptationEnabled})", Color.Red);
                }
                return;
            }

            int weaponKey = GetWeaponKeyFromItem(item);
            int playerId = player?.whoAmI ?? -1;
            if (playerId >= 0)
            {
                RecordComboDamage(npc, playerId, weaponKey, damageDone);
                if (config?.DebugMode == true)
                {
                    string playerName = player?.name ?? "Unknown";
                    string weaponName = item?.Name ?? "Unknown";
                    Main.NewText($"Damage recorded: {playerName}'s {weaponName} = {damageDone}", Color.Gray);
                }
            }
        }

        public override bool PreAI(NPC npc)
        {
            // Ensure client-side initialization for npcs that are synced from the server.
            // The server will run OnSpawn for real initialization; clients receive NPCs via network
            // and do not invoke OnSpawn, leaving spawnTime uninitialized. Initialize minimal state
            // on the client so that client-side debug and read-only displays function.
            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                if (npc.boss && spawnTime < 0)
                {
                    // Initialize client-only state to avoid skipping scaling-related UI on clients.
                    spawnTime = Main.time;
                    ApplyConfigOverrides();
                    currentDefenseModifier = 1.0;
                    currentOffenseModifier = 1.0;
                    lastHpInterval = FullHealthPercent;
                    lastTimeDifference = 0.0;
                    // Clear client-only caches to avoid displaying stale values
                    comboDamagePhase.Clear();
                    comboDamageRunning.Clear();
                    comboAdaptationFactor.Clear();
                    comboAdaptationWarned.Clear();
                    lastPhaseTime = spawnTime;
                    // Apply cached scaling disabled flag if server told us
                    if (TryGetClientScalingDisabled(npc.whoAmI, out bool clientDisabled))
                    {
                        isScalingDisabled = clientDisabled;
                    }
                    // Apply cached defense/offense modifiers if available
                    if (TryGetClientModifiers(npc.whoAmI, out float cdef, out float coff))
                    {
                        currentDefenseModifier = cdef;
                        currentOffenseModifier = coff;
                    }
                }

                // Clients should not run the following server-only logic.
                return base.PreAI(npc);
            }

            if (!npc.boss)
            {
                return base.PreAI(npc);
            }

            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.TargetHighestHealth != true)
            {
                return base.PreAI(npc);
            }

            // We don't rely on restoring previous values via stored originals (which can conflict with other NPCs).
            // Instead, track which players we add Aggro to and subtract it in PostAI. This is additive and safe across NPCs.
            boostedPlayers.Clear();
            int targetPlayer = FindHighestHealthPlayer(npc);
            int lowestPlayer = FindLowestHealthPlayer(npc);

            // Old "below 30% invalidate" logic removed: we now only use lowest-health invalidation

            if (targetPlayer >= 0)
            {
                for (int i = 0; i < Main.maxPlayers; i++)
                {
                    Player player = Main.player[i];
                    if (player.active && !player.dead)
                    {
                        if (i == targetPlayer)
                        {
                            if (config?.TargetHighestHealth == true)
                            {
                                if (!boostedPlayers.Contains(i))
                                {
                                    player.aggro += AggroBoost;
                                    boostedPlayers.Add(i);
                                }
                            }
                        }
                    }
                }
            }

            // If the NPC currently has a player target and it's the lowest-health player within range (within threshold), invalidate it
            if (npc.HasPlayerTarget && lowestPlayer >= 0)
            {
                int curTarget = npc.target;
                if (curTarget >= 0 && curTarget < Main.maxPlayers)
                {
                    Player curP = Main.player[curTarget];
                    if (curP != null && curP.active && !curP.dead)
                    {
                        int lowestHealth = Main.player[lowestPlayer].statLife;
                        if (Vector2.DistanceSquared(npc.Center, curP.Center) > BossProximityCache.BossRangeSq)
                        {
                            return base.PreAI(npc);
                        }
                        if (Math.Abs(curP.statLife - lowestHealth) <= HealthThreshold)
                        {
                            npc.target = 255;
                            npc.netUpdate = true;
                            if (config?.DebugMode == true)
                            {
                                Main.NewText($"{npc.GivenOrTypeName} invalidated target {curP.name} due to being close to lowest HP ({curP.statLife} vs {lowestHealth})", Color.Orange);
                            }
                        }
                    }
                }
            }

            return base.PreAI(npc);
        }

        public override void PostAI(NPC npc)
        {
            if (!npc.boss)
            {
                return;
            }

            foreach (int pid in boostedPlayers)
            {
                if (pid >= 0 && pid < Main.maxPlayers && Main.player[pid].active)
                {
                    Main.player[pid].aggro = Math.Max(0, Main.player[pid].aggro - AggroBoost);
                }
            }
            boostedPlayers.Clear();
        }

        private int FindHighestHealthPlayer(NPC npc)
        {
            int bestPlayer = -1;
            int highestHealth = 0;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player player = Main.player[i];
                if (!player.active || player.dead || player.ghost)
                {
                    continue;
                }

                float distance = Vector2.Distance(npc.Center, player.Center);

                if (distance <= BossProximityCache.BossRange)
                {
                    if (player.statLife > highestHealth || (player.statLife == highestHealth && distance < bestDistance))
                    {
                        highestHealth = player.statLife;
                        bestPlayer = i;
                        bestDistance = distance;
                    }
                }
            }

            return bestPlayer;
        }

        private int FindLowestHealthPlayer(NPC npc)
        {
            int bestPlayer = -1;
            int lowestHealth = int.MaxValue;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player player = Main.player[i];
                if (!player.active || player.dead || player.ghost)
                {
                    continue;
                }

                float distance = Vector2.Distance(npc.Center, player.Center);

                if (distance <= BossProximityCache.BossRange)
                {
                    if (player.statLife < lowestHealth || (player.statLife == lowestHealth && distance < bestDistance))
                    {
                        lowestHealth = player.statLife;
                        bestPlayer = i;
                        bestDistance = distance;
                    }
                }
            }

            return bestPlayer;
        }
    }
}
