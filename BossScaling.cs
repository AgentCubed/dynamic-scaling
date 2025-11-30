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

        // All state moved to BossGroupTracker for unified group-based handling

        public override void OnSpawn(NPC npc, IEntitySource source)
        {
            if (npc.boss)
            {
                var gdata = BossGroupTracker.GetGroupData(npc);
                if (gdata != null)
                {
                    gdata.SpawnTime = Main.time;
                    gdata.CurrentDefenseModifier = 1.0;
                    gdata.CurrentOffenseModifier = 1.0;
                    gdata.LastHpInterval = FullHealthPercent;
                    gdata.LastTimeDifference = 0.0;
                    gdata.PlayerDeathsThisFight = 0;
                    gdata.LastPhaseTime = gdata.SpawnTime;
                    gdata.WeaponDamagePhase.Clear();
                    gdata.WeaponDamageRunning.Clear();
                    gdata.AdaptationFactors.Clear();
                    gdata.AdaptationWarned.Clear();
                }

                ApplyConfigOverrides();

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
                bool scalingDisabled = (config?.ExpectedTotalMinutes == 0);

                // If configured, disable time scaling for bosses with progression less than threshold (from BossChecklist)
                try
                {
                    if (!scalingDisabled && config?.BossProgressionThresholdValue > 0)
                    {
                        double? prog = BossChecklist.GetBossChecklistProgressionForNPC(npc.type);
                        if (prog.HasValue && prog.Value < config.BossProgressionThresholdValue)
                        {
                            scalingDisabled = true;
                            if (config.DebugMode)
                                Utils.EmitDebug($"[Boss] Scaling disabled for {npc.FullName} (progression {prog.Value} < threshold {config.BossProgressionThresholdValue})", Color.Yellow);
                        }
                    }
                }
                catch { }

                gdata.IsScalingDisabled = scalingDisabled;

                // Sync scaling disabled value to clients
                try
                {
                    if (Main.netMode == NetmodeID.Server)
                    {
                        BossSyncPacket.SendScalingDisabledForNPC(npc.whoAmI, scalingDisabled);
                        BossSyncPacket.SendBossModifiersForNPC(npc.whoAmI, (float)gdata.CurrentDefenseModifier, (float)gdata.CurrentOffenseModifier);
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
                    var gdata = BossGroupTracker.GetGroupData(npc);
                    bool scalingDisabled = gdata?.IsScalingDisabled ?? false;
                    binaryWriter.Write(scalingDisabled);
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
                BossGroupTracker.SetClientScalingDisabled(npc.whoAmI, disabled);
                // Update group data if available
                var gdata = BossGroupTracker.GetGroupData(npc);
                if (gdata != null)
                {
                    gdata.IsScalingDisabled = disabled;
                }
            }
            catch { }
        }

        // Client-side cache for network-synced modifiers and adaptation factors.
        private static Dictionary<int, (float defenseModifier, float offenseModifier)> clientModifiers = new Dictionary<int, (float, float)>();
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

        public static void ClearClientCaches()
        {
            clientModifiers.Clear();
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
            // Route the report to the group tracker
            BossGroupTracker.ReportComboDamage(npcWhoAmI, playerId, weaponKey, amount);
        }

        public static int TotalBossDeaths => totalBossDeaths;

        public static void RecordBossDeath()
        {
            totalBossDeaths++;

            var processedGroups = new HashSet<int>();
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (!npc.active || !npc.boss)
                {
                    continue;
                }

                var gdata = BossGroupTracker.GetGroupData(npc);
                if (gdata != null && !processedGroups.Contains(gdata.GroupId))
                {
                    gdata.PlayerDeathsThisFight++;
                    processedGroups.Add(gdata.GroupId);
                }
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

                var gdata = BossGroupTracker.GetGroupData(npc);
                if (gdata != null)
                {
                    return gdata.PlayerDeathsThisFight;
                }
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
            var gdata = BossGroupTracker.GetGroupData(npc);
            if (gdata == null || gdata.SpawnTime < 0)
            {
                return;
            }

            // Prefer client-side cached flag for clients; server uses instance flag assigned on spawn
            if (Main.netMode == NetmodeID.MultiplayerClient && BossGroupTracker.TryGetClientScalingDisabled(npc.whoAmI, out bool clientDisabled) && clientDisabled)
            {
                return;
            }

            if (gdata.IsScalingDisabled)
            {
                return;
            }

            double timeAlive = Main.time - gdata.SpawnTime;

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

            if (currentHpInterval < gdata.LastHpInterval)
            {
                UpdatePaceModifiers(npc, gdata, timeAlive, currentHpInterval, hpPercent);
                BossGroupTracker.EvaluateWeaponAdaptationOnInterval(npc, gdata);
            }

            ApplyDamageModifiers(gdata, ref modifiers);

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
                float factor = 1f;
                // Use group-adaptation factors
                if (BossGroupTracker.TryGetAdaptationFactorForNPC(npc.whoAmI, comboKey, out float groupFactor))
                {
                    factor = groupFactor;
                }

                if (factor < 1f)
                    modifiers.FinalDamage *= factor;
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
                float factor = 1f;
                if (BossGroupTracker.TryGetAdaptationFactorForNPC(npc.whoAmI, comboKey, out float groupFactor))
                {
                    factor = groupFactor;
                }

                if (factor < 1f)
                    modifiers.FinalDamage *= factor;
            }
        }

        private void UpdatePaceModifiers(NPC npc, BossGroupTracker.BossGroupData gdata, double timeAlive, int currentHpInterval, double hpPercent)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            if (config == null)
                return;

            double hpLost = 1.0 - currentHpInterval / (double)FullHealthPercent;
            double idealTime = idealTotalTicks * hpLost;
            double timeDifferenceMinutes = (timeAlive - idealTime) / 3600.0;
            gdata.LastTimeDifference = timeDifferenceMinutes;

            double absTimeDiff = Math.Abs(timeDifferenceMinutes);

            double oldDefense = gdata.CurrentDefenseModifier;
            double oldOffense = gdata.CurrentOffenseModifier;

            if (absTimeDiff <= deadZoneMinutes)
            {
                gdata.CurrentDefenseModifier = 1.0;
                gdata.CurrentOffenseModifier = 1.0;
            }
            else
            {
                double scaledDifference = absTimeDiff - deadZoneMinutes;
                double modifier = 1.0 + SCALING_CONSTANT * Math.Pow(scaledDifference, 2);
                modifier = Math.Min(modifier, (double)config.MaxDefenseModifier);

                if (timeDifferenceMinutes > 0)
                {
                    gdata.CurrentOffenseModifier = modifier;
                    gdata.CurrentDefenseModifier = 1.0;
                }
                else
                {
                    gdata.CurrentDefenseModifier = modifier;
                    gdata.CurrentOffenseModifier = 1.0;
                }
            }

            // Emit per-NPC debug at the 10% interval boundary
            if (config?.DebugMode == true)
            {
                EmitDebugText(npc, gdata, hpPercent);
            }

            gdata.LastHpInterval = currentHpInterval;
        }

        // Per-instance adaptation checking has been consolidated to BossGroupTracker.

        // Per-instance adaptation proximity output is handled by BossGroupTracker.

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

        private void EmitDebugText(NPC npc, BossGroupTracker.BossGroupData gdata, double hpPercent)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            string paceMessage = "On Pace";
            double displayDefMod = 1.0; // What we show as the boss defense multiplier

            // If we are a multiplayer client, prefer the server-synced modifiers from the cache
            if (Main.netMode == NetmodeID.MultiplayerClient && BossGroupTracker.TryGetClientModifiers(npc.whoAmI, out float cdef, out float coff))
            {
                // Use the client-cached defense modifier from the server for display
                if (coff > 1.0f)
                {
                    paceMessage = $"Pace: +{gdata.LastTimeDifference:F1} min";
                    displayDefMod = 1.0 / coff;
                }
                else if (cdef > 1.0f)
                {
                    paceMessage = $"Pace: {gdata.LastTimeDifference:F1} min";
                    displayDefMod = cdef;
                }
                if (config?.DebugMode == true)
                    Main.NewText($"(SYNC) {(int)(hpPercent * FullHealthPercent)}% HP | {paceMessage} | {displayDefMod:F2}x Def", Color.Gray);
                return;
            }

            // If scaling disabled on the client use cached value for debug display
            if (Main.netMode == NetmodeID.MultiplayerClient && BossGroupTracker.TryGetClientScalingDisabled(npc.whoAmI, out bool clientDisabled) && clientDisabled)
            {
                if (config?.DebugMode == true)
                    Main.NewText($"(SYNC) {(int)(hpPercent * FullHealthPercent)}% HP | Scaling disabled via server", Color.Gray);
                return;
            }

                if (gdata.CurrentOffenseModifier > 1.0)
                {
                    // Player is slow: we increase player damage via currentOffenseModifier.
                    // For debug we want to show the equivalent boss defense multiplier (<1.0).
                    paceMessage = $"Pace: +{gdata.LastTimeDifference:F1} min";
                    displayDefMod = 1.0 / gdata.CurrentOffenseModifier;
                }
                else if (gdata.CurrentDefenseModifier > 1.0)
                {
                    // Player is fast: boss defense is increased (player deals less).
                    paceMessage = $"Pace: {gdata.LastTimeDifference:F1} min";
                    displayDefMod = gdata.CurrentDefenseModifier;
                }

            if (config?.DebugMode == true)
                Main.NewText($"{(int)(hpPercent * FullHealthPercent)}% HP | {paceMessage} | {displayDefMod:F2}x Def", Color.Gray);
        }

        private void ApplyDamageModifiers(BossGroupTracker.BossGroupData gdata, ref NPC.HitModifiers modifiers)
        {
            modifiers.FinalDamage /= (float)gdata.CurrentDefenseModifier;
            modifiers.FinalDamage *= (float)gdata.CurrentOffenseModifier;
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
                BossGroupTracker.ReportComboDamage(npc.whoAmI, playerId, weaponKey, damageDone);
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
                BossGroupTracker.ReportComboDamage(npc.whoAmI, playerId, weaponKey, damageDone);
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
                if (npc.boss)
                {
                    var gdata = BossGroupTracker.GetGroupData(npc);
                    if (gdata != null && gdata.SpawnTime < 0)
                    {
                        // Initialize client-only state to avoid skipping scaling-related UI on clients.
                        gdata.SpawnTime = Main.time;
                        ApplyConfigOverrides();
                        gdata.CurrentDefenseModifier = 1.0;
                        gdata.CurrentOffenseModifier = 1.0;
                        gdata.LastHpInterval = FullHealthPercent;
                        gdata.LastTimeDifference = 0.0;
                        gdata.WeaponDamagePhase.Clear();
                        gdata.WeaponDamageRunning.Clear();
                        gdata.AdaptationFactors.Clear();
                        gdata.AdaptationWarned.Clear();
                        gdata.LastPhaseTime = gdata.SpawnTime;
                        // Apply cached scaling disabled flag if server told us
                        if (BossGroupTracker.TryGetClientScalingDisabled(npc.whoAmI, out bool clientDisabled))
                        {
                            gdata.IsScalingDisabled = clientDisabled;
                        }
                        // Apply cached defense/offense modifiers if available
                        if (BossGroupTracker.TryGetClientModifiers(npc.whoAmI, out float cdef, out float coff))
                        {
                            gdata.CurrentDefenseModifier = cdef;
                            gdata.CurrentOffenseModifier = coff;
                        }
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
