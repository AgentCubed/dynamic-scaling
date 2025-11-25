using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.GameContent.UI.BigProgressBar;
using Terraria.Localization;
using Terraria.ModLoader;

namespace DynamicScaling
{
    public class BossGroupTracker : ModSystem
    {
        // Maps npc.whoAmI -> group ID
        private static Dictionary<int, int> npcToGroup = new Dictionary<int, int>();

        // Maps group ID -> boss bar instance
        private static Dictionary<int, IBigProgressBar> groupBars = new Dictionary<int, IBigProgressBar>();

        // Maps group ID -> aggregated data
        private static Dictionary<int, BossGroupData> groupData = new Dictionary<int, BossGroupData>();

        private static int nextGroupId = 0;

        // Reference to the BigProgressBarSystem to look up boss bars
        private static BigProgressBarSystem bossBarSystem;

        public class BossGroupData
        {
            public int GroupId;
            public float TotalLife;
            public float TotalLifeMax;

            // Unified weapon adaptation tracking
            public Dictionary<(int playerId, int weaponKey), double> WeaponDamagePhase = new Dictionary<(int, int), double>();
            public Dictionary<(int playerId, int weaponKey), double> WeaponDamageRunning = new Dictionary<(int, int), double>();
            public Dictionary<(int playerId, int weaponKey), float> AdaptationFactors = new Dictionary<(int, int), float>();
            public System.Collections.Generic.HashSet<(int playerId, int weaponKey)> AdaptationWarned = new System.Collections.Generic.HashSet<(int, int)>();

            // Unified pace tracking
            public double SpawnTime = -1;
            public double CurrentDefenseModifier = 1.0;
            public double CurrentOffenseModifier = 1.0;
            public int LastHpInterval = 100;
            public double LastTimeDifference = 0.0;
            public int PlayerDeathsThisFight = 0;
            public double LastPhaseTime = 0.0;

            // State
            public bool IsScalingDisabled = false;

            // For network syncing: last sent modifiers so the server doesn't spam unchanged values
            public double LastSentDefenseModifier = 1.0;
            public double LastSentOffenseModifier = 1.0;
        }

        public override void OnWorldLoad()
        {
            ClearAll();
        }

        public override void OnWorldUnload()
        {
            ClearAll();
        }

        private static void ClearAll()
        {
            npcToGroup.Clear();
            groupBars.Clear();
            groupData.Clear();
            nextGroupId = 0;
            bossBarSystem = null;
            clientModifiers.Clear();
        }

        // Public API for other classes (ScalingBossBar, GlobalNPC) to force a rebuild / clear cache
        public static void ClearCache()
        {
            ClearAll();
        }

        public override void PostUpdateNPCs()
        {
            if (Main.netMode == Terraria.ID.NetmodeID.MultiplayerClient)
            {
                return;
            }

            // Avoid allocating a List every frame. Copy keys to an array once.
            int count = groupData.Count;
            int[] groupKeys = new int[count];
            groupData.Keys.CopyTo(groupKeys, 0);
            for (int index = 0; index < groupKeys.Length; index++)
            {
                var groupId = groupKeys[index];
                var data = groupData[groupId];
                NPC npc = GetFirstActiveNPCInGroup(groupId);
                
                if (npc == null || !npc.active || !npc.boss)
                    continue;

                if (data.IsScalingDisabled)
                    continue;

                double timeAlive = Main.time - data.SpawnTime;
                if (timeAlive <= 60)
                    continue;
                
                if (!GetBossHealth(npc, out float life, out float lifeMax) || lifeMax <= 0)
                    continue;
                
                double hpPercent = life / lifeMax;
                int currentInterval = (int)Math.Floor(hpPercent * 10) * 10;
                
                if (currentInterval < data.LastHpInterval)
                {
                    UpdateBossScaling(npc, data, timeAlive, currentInterval, hpPercent);
                }
            }
        }

        private static void UpdateBossScaling(NPC npc, BossGroupData groupData, double timeAlive, int currentHpInterval, double hpPercent)
        {
            UpdatePaceModifiers(npc, groupData, timeAlive, currentHpInterval, hpPercent);
            EvaluateWeaponAdaptationOnInterval(npc, groupData);
        }

        // Register a boss NPC and assign it to a group
        public static int RegisterBoss(NPC npc)
        {
            if (!npc.boss)
            {
                return -1;
            }

            // Check if already registered
            if (npcToGroup.TryGetValue(npc.whoAmI, out int existingGroup))
            {
                return existingGroup;
            }

            // Get boss bar system instance
            if (bossBarSystem == null)
            {
                // Access the singleton instance via Main
                bossBarSystem = Main.BigBossProgressBar;
            }

            // Try to get the appropriate boss bar for this NPC
            IBigProgressBar bossBar = GetBossBarForNPC(npc);
            if (bossBar == null)
            {
                return -1;
            }

            // Check if any existing group uses this same boss bar
            int? existingGroupId = FindGroupWithSameBossBar(bossBar, npc);
            if (existingGroupId.HasValue)
            {
                npcToGroup[npc.whoAmI] = existingGroupId.Value;
                return existingGroupId.Value;
            }

            // Create new group
            int newGroupId = nextGroupId++;
            npcToGroup[npc.whoAmI] = newGroupId;
            groupBars[newGroupId] = bossBar;
            groupData[newGroupId] = new BossGroupData
            {
                GroupId = newGroupId,
                SpawnTime = Main.time
            };

            // Apply Ignore If Below Progression option: if threshold > 0 and BossChecklist reports the boss progression < threshold, disable scaling
            try
            {
                var config = ModContent.GetInstance<ServerConfig>();
                if (config?.BossProgressionThreshold > 0)
                {
                    double? prog = GetBossChecklistProgressionForNPC(npc.type);
                    if (prog.HasValue && prog.Value < config.BossProgressionThreshold)
                    {
                        groupData[newGroupId].IsScalingDisabled = true;
                        if (config.DebugMode)
                            DebugUtil.EmitDebug($"[BossGroupTracker] Scaling disabled for {npc.FullName} (progression {prog.Value} < threshold {config.BossProgressionThreshold})", Color.Yellow);
                    }
                }
            }
            catch { }

            // Initialize last-sent values to defaults and sync initial modifiers to clients
            groupData[newGroupId].LastSentDefenseModifier = 1.0;
            groupData[newGroupId].LastSentOffenseModifier = 1.0;
            if (Main.netMode == NetmodeID.Server)
            {
                BossSyncPacket.SendBossModifiersForNPC(npc.whoAmI, 1f, 1f);
            }

            return newGroupId;
        }

        // Get the boss bar instance for an NPC
        private static IBigProgressBar GetBossBarForNPC(NPC npc)
        {
            // Check if NPC has a custom ModBossBar
            if (npc.BossBar != null)
            {
                return npc.BossBar;
            }

            // Check vanilla special boss bars
            if (bossBarSystem != null && bossBarSystem.TryGetSpecialVanillaBossBar(npc.netID, out IBigProgressBar specialBar))
            {
                return specialBar;
            }

            // Fallback to common boss bar if NPC has a boss head
            if (npc.GetBossHeadTextureIndex() != -1)
            {
                return new CommonBossBigProgressBar();
            }

            return null;
        }

        // Find if any existing group uses the same boss bar type and could include this NPC
        private static int? FindGroupWithSameBossBar(IBigProgressBar bossBar, NPC npc)
        {
            foreach (var kvp in groupBars)
            {
                int groupId = kvp.Key;
                IBigProgressBar existingBar = kvp.Value;

                // For vanilla special bars (Twins, Moon Lord, etc.), the same instance is reused
                // So we can check reference equality
                if (ReferenceEquals(existingBar, bossBar))
                {
                    // Verify at least one NPC in this group is still active
                    if (HasActiveNPCInGroup(groupId))
                    {
                        return groupId;
                    }
                }

                // For type-based matching (e.g., multiple CommonBossBigProgressBar instances)
                if (existingBar.GetType() == bossBar.GetType())
                {
                    // Need to check if this NPC would validate with the same bar
                    // For CommonBossBigProgressBar, check if boss heads match
                    if (existingBar is CommonBossBigProgressBar)
                    {
                        // Get an active NPC from this group
                        var groupNpc = GetFirstActiveNPCInGroup(groupId);
                        if (groupNpc != null && groupNpc.GetBossHeadTextureIndex() == npc.GetBossHeadTextureIndex())
                        {
                            return groupId;
                        }
                    }
                }
            }

            return null;
        }

        private static bool HasActiveNPCInGroup(int groupId)
        {
            foreach (var kvp in npcToGroup)
            {
                if (kvp.Value == groupId)
                {
                    NPC npc = Main.npc[kvp.Key];
                    if (npc.active && npc.boss)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static NPC GetFirstActiveNPCInGroup(int groupId)
        {
            foreach (var kvp in npcToGroup)
            {
                if (kvp.Value == groupId)
                {
                    NPC npc = Main.npc[kvp.Key];
                    if (npc.active && npc.boss)
                    {
                        return npc;
                    }
                }
            }
            return null;
        }

        // Get aggregated boss health by calling ValidateAndCollectNecessaryInfo
        public static bool GetBossHealth(NPC npc, out float life, out float lifeMax)
        {
            life = 0f;
            lifeMax = 1f;

            if (!npc.boss || !npcToGroup.TryGetValue(npc.whoAmI, out int groupId))
            {
                return false;
            }

            if (!groupBars.TryGetValue(groupId, out IBigProgressBar bossBar))
            {
                return false;
            }

            // Create BigProgressBarInfo to pass to ValidateAndCollectNecessaryInfo
            BigProgressBarInfo info = new BigProgressBarInfo
            {
                npcIndexToAimAt = npc.whoAmI
            };

            // Call ValidateAndCollectNecessaryInfo to aggregate health
            // This is the same method the boss bar rendering uses
            if (!bossBar.ValidateAndCollectNecessaryInfo(ref info))
            {
                return false;
            }

            // Extract life values from the boss bar's cache
            // ModBossBar and vanilla bars store this in their internal cache
            if (bossBar is ModBossBar modBar)
            {
                life = modBar.Life;
                lifeMax = modBar.LifeMax;
            }
            else
            {
                // For vanilla bars, we need to access their cache
                // Use reflection or recreate the logic
                life = GetCachedLife(bossBar);
                lifeMax = GetCachedLifeMax(bossBar);
            }

            // Update stored data
            if (groupData.TryGetValue(groupId, out var data))
            {
                data.TotalLife = life;
                data.TotalLifeMax = lifeMax;
            }

            return lifeMax > 0;
        }

        private static float GetCachedLife(IBigProgressBar bar)
        {
            // Use reflection to access _cache.LifeCurrent for vanilla bars
            var cacheField = bar.GetType().GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (cacheField != null)
            {
                var cache = (BigProgressBarCache)cacheField.GetValue(bar);
                return cache.LifeCurrent;
            }
            return 0f;
        }

        private static float GetCachedLifeMax(IBigProgressBar bar)
        {
            // Use reflection to access _cache.LifeMax for vanilla bars
            var cacheField = bar.GetType().GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (cacheField != null)
            {
                var cache = (BigProgressBarCache)cacheField.GetValue(bar);
                return cache.LifeMax;
            }
            return 1f;
        }

        // Get group data for an NPC
        public static BossGroupData GetGroupData(NPC npc)
        {
            if (!npc.boss || !npcToGroup.TryGetValue(npc.whoAmI, out int groupId))
            {
                return null;
            }

            // Update health before returning
            GetBossHealth(npc, out _, out _);

            return groupData.TryGetValue(groupId, out var data) ? data : null;
        }

        // Called by the server to record combo damage reported by clients
        public static void ReportComboDamage(int npcWhoAmI, int playerId, int weaponKey, int amount)
        {
            if (Main.netMode != NetmodeID.Server)
            {
                return; // Only record on server
            }

            if (playerId < 0 || playerId >= Main.maxPlayers)
                return;

            NPC npc = Main.npc[npcWhoAmI];
            if (npc == null || !npc.active || !npc.boss)
                return;

            if (!npcToGroup.TryGetValue(npcWhoAmI, out int groupId))
                return;

            if (!groupData.TryGetValue(groupId, out var data))
                return;

            if (data.IsScalingDisabled)
                return;

            var key = (playerId, weaponKey);
            if (data.WeaponDamagePhase.TryGetValue(key, out double curPhase))
            {
                data.WeaponDamagePhase[key] = curPhase + amount;
            }
            else
            {
                data.WeaponDamagePhase[key] = amount;
            }
        }

        // Clean up dead NPCs from tracking
        public static void CleanupDeadNPC(int npcWhoAmI)
        {
            if (npcToGroup.TryGetValue(npcWhoAmI, out int groupId))
            {
                npcToGroup.Remove(npcWhoAmI);

                // If no more NPCs in this group, clean up group data
                if (!HasActiveNPCInGroup(groupId))
                {
                    groupBars.Remove(groupId);
                    groupData.Remove(groupId);
                }
            }

            // Remove client cached modifiers for this NPC (prevent stale entries)
            if (clientModifiers.ContainsKey(npcWhoAmI))
            {
                clientModifiers.Remove(npcWhoAmI);
            }
            if (clientAdaptationFactors.ContainsKey(npcWhoAmI))
            {
                clientAdaptationFactors.Remove(npcWhoAmI);
            }
        }

        // Get all NPCs in the same group
        public static List<int> GetGroupMembers(int groupId)
        {
            List<int> members = new List<int>();
            foreach (var kvp in npcToGroup)
            {
                if (kvp.Value == groupId)
                {
                    members.Add(kvp.Key);
                }
            }
            return members;
        }

        // Client-side cache of modifiers per NPC whoAmI
        private static Dictionary<int, (float defenseModifier, float offenseModifier)> clientModifiers = new Dictionary<int, (float, float)>();
        // Client-side cache of adaptation factors per NPC whoAmI
        private static Dictionary<int, Dictionary<(int playerId, int weaponKey), float>> clientAdaptationFactors = new Dictionary<int, Dictionary<(int, int), float>>();

        public static bool TryGetClientModifiers(int npcWhoAmI, out float defense, out float offense)
        {
            if (clientModifiers.TryGetValue(npcWhoAmI, out var tuple))
            {
                defense = tuple.defenseModifier;
                offense = tuple.offenseModifier;
                return true;
            }
            defense = 1f;
            offense = 1f;
            return false;
        }

        public static void SetClientModifiers(int npcWhoAmI, float defense, float offense)
        {
            clientModifiers[npcWhoAmI] = (defense, offense);
        }

        public static void SetClientAdaptationFactor(int npcWhoAmI, (int playerId, int weaponKey) comboKey, float factor)
        {
            if (!clientAdaptationFactors.TryGetValue(npcWhoAmI, out var dict))
            {
                dict = new Dictionary<(int, int), float>();
                clientAdaptationFactors[npcWhoAmI] = dict;
            }
            dict[comboKey] = factor;
        }

        public static bool TryGetClientAdaptationFactor(int npcWhoAmI, (int playerId, int weaponKey) comboKey, out float factor)
        {
            factor = 1f;
            if (!clientAdaptationFactors.TryGetValue(npcWhoAmI, out var dict))
                return false;
            if (!dict.TryGetValue(comboKey, out factor))
                return false;
            return true;
        }

        private const double DefaultExpectedMinutes = 4.0;
        private const double DeadZoneDivisor = 5.0;
        private const int FullHealthPercent = 100;
        private const double PhaseAvgAlpha = 0.4;

        private static void UpdatePaceModifiers(NPC npc, BossGroupData groupData, double timeAlive, int currentHpInterval, double hpPercent)
        {
            var config = ModContent.GetInstance<ServerConfig>();

            double idealTotalTicks = (config?.ExpectedTotalMinutes ?? DefaultExpectedMinutes) * 3600.0;
            double deadZoneMinutes = (config?.ExpectedTotalMinutes ?? DefaultExpectedMinutes) / DeadZoneDivisor;

            double hpLost = 1.0 - currentHpInterval / (double)FullHealthPercent;
            double idealTime = idealTotalTicks * hpLost;
            double timeDifferenceMinutes = (timeAlive - idealTime) / 3600.0;
            groupData.LastTimeDifference = timeDifferenceMinutes;

            double absTimeDiff = Math.Abs(timeDifferenceMinutes);

            if (absTimeDiff <= deadZoneMinutes)
            {
                groupData.CurrentDefenseModifier = 1.0;
                groupData.CurrentOffenseModifier = 1.0;
            }
            else
            {
                double scaledDifference = absTimeDiff - deadZoneMinutes;
                double modifier = 1.0 + config.ScalingConstant * Math.Pow(scaledDifference, 2);
                modifier = Math.Min(modifier, (double)config.MaxDefenseModifier);

                if (timeDifferenceMinutes > 0)
                {
                    groupData.CurrentOffenseModifier = modifier;
                    groupData.CurrentDefenseModifier = 1.0;
                }
                else
                {
                    groupData.CurrentDefenseModifier = modifier;
                    groupData.CurrentOffenseModifier = 1.0;
                }
            }

            EmitDebugText(npc, groupData, hpPercent);

            groupData.LastHpInterval = currentHpInterval;

            // If this is server, and the modifier changed materially, broadcast to clients
            if (Main.netMode == NetmodeID.Server)
            {
                const double Threshold = 0.01; // 1% change threshold
                bool changed = Math.Abs(groupData.CurrentDefenseModifier - groupData.LastSentDefenseModifier) > Threshold || Math.Abs(groupData.CurrentOffenseModifier - groupData.LastSentOffenseModifier) > Threshold;
                if (changed)
                {
                    var debugConfig = ModContent.GetInstance<ServerConfig>();
                    if (debugConfig?.DebugMode == true)
                    {
                        DebugUtil.EmitDebug($"[BossGroupTracker] Modifier changed: def {groupData.LastSentDefenseModifier:F2}x -> {groupData.CurrentDefenseModifier:F2}x, off {groupData.LastSentOffenseModifier:F2}x -> {groupData.CurrentOffenseModifier:F2}x", Color.Magenta);
                    }

                    groupData.LastSentDefenseModifier = groupData.CurrentDefenseModifier;
                    groupData.LastSentOffenseModifier = groupData.CurrentOffenseModifier;
                    // Send modifiers for each active member in the group
                    var members = GetGroupMembers(groupData.GroupId);
                    foreach (var npcIndex in members)
                    {
                        if (npcIndex < 0 || npcIndex >= Main.maxNPCs) continue;
                        NPC m = Main.npc[npcIndex];
                        if (!m.active || !m.boss) continue;
                        BossSyncPacket.SendBossModifiersForNPC(m.whoAmI, (float)groupData.CurrentDefenseModifier, (float)groupData.CurrentOffenseModifier);
                    }
                }
            }
        }

        private static void EvaluateWeaponAdaptationOnInterval(NPC npc, BossGroupData groupData)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.WeaponAdaptationEnabled != true) return;

            var keys = groupData.WeaponDamagePhase.Keys.ToArray();
            if (keys.Length <= 0)
            {
                groupData.LastPhaseTime = Main.time;
                return;
            }

            bool phaseTooFast = groupData.CurrentDefenseModifier > 1.0;

            foreach (var key in keys)
            {
                double curPhaseDamage = groupData.WeaponDamagePhase.TryGetValue(key, out double val) ? val : 0.0;
                double prevRunning = groupData.WeaponDamageRunning.TryGetValue(key, out double prev) ? prev : 0.0;
                double newRunning = (1.0 - PhaseAvgAlpha) * prevRunning + PhaseAvgAlpha * curPhaseDamage;
                groupData.WeaponDamageRunning[key] = newRunning;
            }

            var allRunningValues = groupData.WeaponDamageRunning.Values.ToList();
            if (allRunningValues.Count == 0) return;

            double maxDamage = allRunningValues.Max();
            var relevantValues = allRunningValues.Where(v => v > maxDamage * 0.1).OrderBy(v => v).ToList();

            double median = 0.0;
            int count = relevantValues.Count;
            if (count > 0)
            {
                if (count % 2 == 0)
                    median = (relevantValues[count / 2 - 1] + relevantValues[count / 2]) / 2.0;
                else
                    median = relevantValues[count / 2];
            }

            if (median <= 1.0) median = maxDamage * 0.5;

            double startMultiplier = config.WeaponAdaptationStartMultiplier;
            double completeMultiplier = config.WeaponAdaptationCompleteMultiplier;
            double maxReduction = config.WeaponAdaptationMaxReduction;
            double minDamage = config.WeaponAdaptationMinDamage;

            var runningKeys = groupData.WeaponDamageRunning.Keys.ToArray();

            foreach (var key in runningKeys)
            {
                double comboRunning = groupData.WeaponDamageRunning[key];
                
                if (comboRunning < minDamage) continue;

                double ratio = median > 0 ? comboRunning / median : 0;

                if (groupData.WeaponDamageRunning.Count == 1 && config.WeaponAdaptationAdaptToSoloPlayers)
                {
                   if (phaseTooFast && groupData.CurrentDefenseModifier >= (double)config.MaxDefenseModifier)
                   {
                       ratio = completeMultiplier + 1.0; 
                   }
                }

                if (ratio >= startMultiplier && !groupData.AdaptationWarned.Contains(key) && !groupData.AdaptationFactors.ContainsKey(key))
                {
                    groupData.AdaptationWarned.Add(key);
                    Main.NewText($"{npc.GivenOrTypeName} is beginning to adapt to {GetPlayerName(key.playerId)}'s {GetWeaponNameFromKey(key.weaponKey)}.", Color.Orange);
                    if (config?.DebugMode == true)
                    {
                        DebugUtil.EmitDebug($"{npc.GivenOrTypeName} is analyzing {GetPlayerName(key.playerId)}'s {GetWeaponNameFromKey(key.weaponKey)} (Ratio: {ratio:F2})", Microsoft.Xna.Framework.Color.Orange);
                    }
                }

                if (ratio >= completeMultiplier && phaseTooFast)
                {
                    float factor = 1.0f - (float)maxReduction * (1.0f - (float)(median / comboRunning));
                    factor = Math.Max(factor, 0.1f);

                    if (groupData.AdaptationFactors.TryGetValue(key, out float existingFactor))
                    {
                        if (factor < existingFactor - 0.001f)
                        {
                            UpdateAdaptationFactor(npc, groupData, key, factor);
                        }
                    }
                    else
                    {
                        UpdateAdaptationFactor(npc, groupData, key, factor);
                    }
                }
            }

            if (config?.DebugMode == true)
            {
                EmitComboAdaptationProximity(npc, groupData);
            }

            groupData.WeaponDamagePhase.Clear();
            groupData.LastPhaseTime = Main.time;
        }

        private static void UpdateAdaptationFactor(NPC npc, BossGroupData groupData, (int playerId, int weaponKey) key, float factor)
        {
            groupData.AdaptationFactors[key] = factor;
            if (Main.netMode == NetmodeID.Server)
            {
                BossSyncPacket.SendAdaptationForNPC(npc.whoAmI, key.playerId, key.weaponKey, factor);
            }
            
            Main.NewText($"{npc.GivenOrTypeName} has adapted to {GetPlayerName(key.playerId)}'s {GetWeaponNameFromKey(key.weaponKey)}.", Color.Yellow);
            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.DebugMode == true)
            {
                string playerName = GetPlayerName(key.playerId);
                string weaponName = GetWeaponNameFromKey(key.weaponKey);
                DebugUtil.EmitDebug($"{npc.GivenOrTypeName} adapted to {playerName}'s {weaponName} (Factor: {factor:F2})", Microsoft.Xna.Framework.Color.Yellow);
            }
        }

        private static void CheckAdaptationForComboRunning(NPC npc, BossGroupData groupData, (int playerId, int weaponKey) comboKey, bool phaseTooFast)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            if (config == null)
            {
                return;
            }

            if (!groupData.WeaponDamageRunning.TryGetValue(comboKey, out double comboRunning))
            {
                return;
            }

            int countRunning = groupData.WeaponDamageRunning.Count;
            bool allowSingleComboAdaptation = (countRunning == 1 && groupData.CurrentDefenseModifier >= (double)config.MaxDefenseModifier && config.WeaponAdaptationAdaptToSoloPlayers);
            if (allowSingleComboAdaptation && config?.DebugMode == true)
            {
                var onlyKey = groupData.WeaponDamageRunning.Keys.First();
                string playerName = GetPlayerName(onlyKey.playerId);
                string weaponName = GetWeaponNameFromKey(onlyKey.weaponKey);
                DebugUtil.EmitDebug($"{npc.GivenOrTypeName} has adapted to {playerName}'s {weaponName}.", Microsoft.Xna.Framework.Color.Orange);
            }
            if (countRunning <= 1 && !allowSingleComboAdaptation)
            {
                return;
            }

            double totalRunning = 0.0;
            foreach (var v in groupData.WeaponDamageRunning.Values) totalRunning += v;
            double meanOthers;
            if (countRunning == 1)
            {
                meanOthers = 1e-6;
            }
            else
            {
                meanOthers = (totalRunning - comboRunning) / Math.Max(1, countRunning - 1);
                if (meanOthers <= 0.0)
                {
                    return;
                }
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

            if (ratio >= startMultiplier && !groupData.AdaptationWarned.Contains(comboKey) && !groupData.AdaptationFactors.ContainsKey(comboKey))
            {
                groupData.AdaptationWarned.Add(comboKey);
                string playerName = GetPlayerName(comboKey.playerId);
                string weaponName = GetWeaponNameFromKey(comboKey.weaponKey);
                DebugUtil.EmitDebug($"{npc.GivenOrTypeName} is beginning to adapt to {playerName}'s {weaponName}...", Microsoft.Xna.Framework.Color.Orange);
            }

            if (ratio >= completeMultiplier && phaseTooFast)
            {
                float factor = countRunning == 1 ? 0f : 1.0f - (float)maxReduction * (1.0f - (float)meanOthers / (float)comboRunning);
                    if (groupData.AdaptationFactors.TryGetValue(comboKey, out float existingFactor))
                {
                    if (factor < existingFactor - 0.001f)
                    {
                        groupData.AdaptationFactors[comboKey] = factor;
                            if (Main.netMode == NetmodeID.Server)
                            {
                                BossSyncPacket.SendAdaptationForNPC(npc.whoAmI, comboKey.playerId, comboKey.weaponKey, factor);
                            }
                        string playerName = GetPlayerName(comboKey.playerId);
                        string weaponName = GetWeaponNameFromKey(comboKey.weaponKey);
                        DebugUtil.EmitDebug($"{npc.GivenOrTypeName} has adapted to {playerName}'s {weaponName}.", Microsoft.Xna.Framework.Color.Yellow);
                    }
                }
                else
                {
                    groupData.AdaptationFactors[comboKey] = factor;
                    if (Main.netMode == NetmodeID.Server)
                    {
                        BossSyncPacket.SendAdaptationForNPC(npc.whoAmI, comboKey.playerId, comboKey.weaponKey, factor);
                    }
                    string playerName = GetPlayerName(comboKey.playerId);
                    string weaponName = GetWeaponNameFromKey(comboKey.weaponKey);
                    DebugUtil.EmitDebug($"{npc.GivenOrTypeName} has adapted to {playerName}'s {weaponName}.", Microsoft.Xna.Framework.Color.Yellow);
                }
            }
            else
            {
                if (config?.DebugMode == true)
                {
                    string playerName = GetPlayerName(comboKey.playerId);
                    string weaponName = GetWeaponNameFromKey(comboKey.weaponKey);
                    string reason = ratio < completeMultiplier ? $"ratio {ratio:F2} < {completeMultiplier:F2}" : "phase not too fast";
                    DebugUtil.EmitDebug($"{npc.GivenOrTypeName} did not adapt to {playerName}'s {weaponName}: {reason}", Microsoft.Xna.Framework.Color.Gray);
                }
            }
        }

        private static void EmitComboAdaptationProximity(NPC npc, BossGroupData groupData)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            if (config == null || groupData.WeaponDamagePhase.Count <= 0)
            {
                return;
            }
            int comboCount = groupData.WeaponDamagePhase.Count;
            if (comboCount <= 1)
            {
                if (config?.DebugMode == true)
                    DebugUtil.EmitDebug($"{npc.GivenOrTypeName} adaptation: not enough combos to evaluate proximity (count={comboCount})", Microsoft.Xna.Framework.Color.Gray);
                return;
            }

            double startMultiplier = config.WeaponAdaptationStartMultiplier;
            double completeMultiplier = config.WeaponAdaptationCompleteMultiplier;

            double phaseDurationTicks = Math.Max(1.0, Main.time - groupData.LastPhaseTime);
            double phaseDurationMinutes = phaseDurationTicks / 3600.0;
            double totalPhase = 0.0;
            foreach (var v in groupData.WeaponDamagePhase.Values) totalPhase += v;
            foreach (var kvp in groupData.WeaponDamagePhase)
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
                    DebugUtil.EmitDebug($"{npc.GivenOrTypeName} adaptation check: {proximityMsg}", Microsoft.Xna.Framework.Color.Gray);
            }
        }

        private static void EmitDebugText(NPC npc, BossGroupData groupData, double hpPercent)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            string paceMessage = "On Pace";
            double displayDefMod = 1.0;

            if (groupData.CurrentOffenseModifier > 1.0)
            {
                paceMessage = $"Pace: +{groupData.LastTimeDifference:F1} min";
                displayDefMod = 1.0 / groupData.CurrentOffenseModifier;
            }
            else if (groupData.CurrentDefenseModifier > 1.0)
            {
                paceMessage = $"Pace: {groupData.LastTimeDifference:F1} min";
                displayDefMod = groupData.CurrentDefenseModifier;
            }

            if (config?.DebugMode == true)
                DebugUtil.EmitDebug($"{(int)(hpPercent * FullHealthPercent)}% HP | {paceMessage} | {displayDefMod:F2}x Def", Microsoft.Xna.Framework.Color.Gray);
        }

        private static string GetPlayerName(int playerId)
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

        private static string GetWeaponNameFromKey(int key)
        {
            if (key > 0)
            {
                int itemType = key - 1;
                return Lang.GetItemNameValue(itemType);
            }
            int projType = -key - 1;
            return Lang.GetProjectileName(projType).Value;
        }

        // Query BossChecklist for the progression value for a particular NPC type
        public static double? GetBossChecklistProgressionForNPC(int npcType)
        {
            try
            {
                var bossChecklistMod = ModLoader.GetMod("BossChecklist");
                if (bossChecklistMod == null)
                    return null;

                object result = bossChecklistMod.Call("GetBossInfoDictionary", ModLoader.GetMod("DynamicScaling"), "2.0");
                if (result is string || result == null)
                {
                    // Fallback to older API version for compatibility
                    result = bossChecklistMod.Call("GetBossInfoDictionary", ModLoader.GetMod("DynamicScaling"), "1.6");
                }
                if (result == null)
                    return null;
                var normalized = BossChecklistUtils.NormalizeBossChecklistReturn(result);
                if (normalized != null)
                {
                    foreach (var kv in normalized)
                    {
                        if (kv.Value is IDictionary<string, object> info)
                        {
                            // Extract npcIDs
                            if (info.TryGetValue("npcIDs", out var idsObj))
                            {
                                List<int> ids = new List<int>();
                                if (idsObj is System.Collections.IEnumerable ie && !(idsObj is string))
                                {
                                    foreach (var o in ie)
                                    {
                                        if (o == null) continue;
                                        if (o is int i)
                                            ids.Add(i);
                                        else
                                        {
                                            if (int.TryParse(o.ToString(), out int parsed))
                                                ids.Add(parsed);
                                        }
                                    }
                                }
                                else
                                {
                                    if (int.TryParse(idsObj.ToString(), out int single))
                                        ids.Add(single);
                                }

                                if (ids.Contains(npcType))
                                {
                                    if (info.TryGetValue("progression", out var pVal))
                                    {
                                        try
                                        {
                                            double p = Convert.ToDouble(pVal);
                                            return p;
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

                return null;
            }
        }

        // Use BossChecklistUtils.NormalizeBossChecklistReturn instead (shared helper)
    }
