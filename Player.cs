using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

#nullable enable

namespace DynamicScaling
{
    public class ScalingPlayer : ModPlayer
    {
        // --- Static Tuning Variables ---
        private const double TICK_INTENSITY = 0.0003;
        private const double DIFFERENCE_FROM_AVG_INTENSITY = 0.15;
        private const double NUMER_ALIVE_INTENSITY = 0.5;
        private const int TICKS_PER_SECOND = 60;
        // Removed Expected Players functionality: BossPlayerCountRange no longer used

        private static long cumulativeShortHandedTickCounter = 0;
        private static int cachedAlivePlayers = 0;
        private static int cachedOnlinePlayers = 1;
        private static uint lastStateUpdateFrame = uint.MaxValue;

        private int deathsThisBossFight = 0;

        public int DeathsThisBossFight => deathsThisBossFight;

        private ServerConfig.PlayerTuning? ResolvePlayerTuning(ServerConfig config)
        {
            if (config.PlayerOverrides == null)
            {
                return null;
            }

            if (config.PlayerOverrides.TryGetValue(Player.name, out var specific))
            {
                return specific;
            }

            if (config.PlayerOverrides.TryGetValue("playername", out var fallback))
            {
                return fallback;
            }

            return null;
        }



        private int GetDealModification(ServerConfig config, ServerConfig.PlayerTuning? tuning)
        {
            int offset = tuning?.DealDamageModifierDifference ?? 0;
            return config.DealDamage + deathsThisBossFight + offset;
        }

        private int GetTakeModification(ServerConfig config, ServerConfig.PlayerTuning? tuning)
        {
            int offset = tuning?.TakeDamageModifierDifference ?? 0;
            return config.TakeDamage + deathsThisBossFight * 2 + offset;
        }

        private float GetDealDamageMultiplier(int totalModification)
        {
            return (float)Math.Pow(1.2, totalModification);
        }

        private float GetTakeDamageMultiplier(int totalModification)
        {
            return (float)Math.Pow(1.2, totalModification);
        }

        // Expected players incoming damage multiplier functionality has been removed

        public override void Kill(double damage, int hitDirection, bool pvp, Terraria.DataStructures.PlayerDeathReason damageSource)
        {
            if (Main.CurrentFrameFlags.AnyActiveBossNPC)
            {
                deathsThisBossFight++;
                ScalingGlobalNPC.RecordBossDeath();
                int totalDeaths = ScalingGlobalNPC.TotalBossDeaths;

                var config = ModContent.GetInstance<ServerConfig>();
                if (config?.DebugMode == true)
                {
                    Main.NewText($"Player death during boss fight: {deathsThisBossFight} deaths this fight, {totalDeaths} total boss deaths", Color.Gray);
                }
            }
        }

        public override void ModifyHitNPC(NPC target, ref NPC.HitModifiers modifiers)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            var tuning = ResolvePlayerTuning(config);
            int totalModification = GetDealModification(config, tuning);
            if (totalModification != 0)
            {
                float multiplier = GetDealDamageMultiplier(totalModification);
                modifiers.FinalDamage *= multiplier;
            }
        }

        public override void ModifyHitByNPC(NPC npc, ref Player.HurtModifiers modifiers)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            var tuning = ResolvePlayerTuning(config);
            bool equalizeDeathsMode = tuning?.EqualizeDeathsMode ?? config.EqualizeDeathsMode;
            int totalModification = GetTakeModification(config, tuning);
            if (totalModification != 0)
            {
                float multiplier = GetTakeDamageMultiplier(totalModification);
                modifiers.IncomingDamageMultiplier *= multiplier;
            }
            
            ApplyExpectedPlayersScaling(ref modifiers, config);

            if (Main.CurrentFrameFlags.AnyActiveBossNPC && equalizeDeathsMode)
            {
                // Mirror original DamageEditor behavior: always update shared fight state each hit.
                UpdateSharedFightState();
                int totalDeaths = ScalingGlobalNPC.GetCurrentFightDeathCount();
                double equalizedMultiplier = GetCombinedDamageMultiplier(
                    cumulativeShortHandedTickCounter,
                    cachedAlivePlayers,
                    cachedOnlinePlayers,
                    totalDeaths,
                    deathsThisBossFight);

                modifiers.IncomingDamageMultiplier *= (float)equalizedMultiplier;
            }
        }

        public override void ModifyHitByProjectile(Projectile proj, ref Player.HurtModifiers modifiers)
        {
            var config = ModContent.GetInstance<ServerConfig>();
            var tuning = ResolvePlayerTuning(config);
            bool equalizeDeathsMode = tuning?.EqualizeDeathsMode ?? config.EqualizeDeathsMode;
            int totalModification = GetTakeModification(config, tuning);
            if (totalModification != 0)
            {
                float multiplier = GetTakeDamageMultiplier(totalModification);
                modifiers.IncomingDamageMultiplier *= multiplier;
            }

            ApplyExpectedPlayersScaling(ref modifiers, config);

            if (Main.CurrentFrameFlags.AnyActiveBossNPC && equalizeDeathsMode)
            {
                // Mirror original DamageEditor behavior: always update shared fight state each hit.
                UpdateSharedFightState();
                int totalDeaths = ScalingGlobalNPC.GetCurrentFightDeathCount();
                double equalizedMultiplier = GetCombinedDamageMultiplier(
                    cumulativeShortHandedTickCounter,
                    cachedAlivePlayers,
                    cachedOnlinePlayers,
                    totalDeaths,
                    deathsThisBossFight);

                modifiers.IncomingDamageMultiplier *= (float)equalizedMultiplier;
            }
        }

        private void ApplyExpectedPlayersScaling(ref Player.HurtModifiers modifiers, ServerConfig config)
        {
            if (config.ExpectedPlayers <= 1 || !Main.CurrentFrameFlags.AnyActiveBossNPC)
            {
                return;
            }

            // Check Expected Players Progression Threshold
            if (config.ExpectedPlayersBossProgressionThresholdValue > 0)
            {
                // If any active boss has progression below threshold, disable scaling
                bool hasLowProgressionBoss = false;
                for (int i = 0; i < Main.maxNPCs; i++)
                {
                    NPC npc = Main.npc[i];
                    if (npc.active && npc.boss)
                    {
                        double? prog = BossChecklistUtils.GetBossChecklistProgressionForNPC(npc.type);
                        if (prog.HasValue && prog.Value < config.ExpectedPlayersBossProgressionThresholdValue)
                        {
                            hasLowProgressionBoss = true;
                            break;
                        }
                    }
                }
                if (hasLowProgressionBoss)
                {
                    return;
                }
            }

            // Find closest boss
            NPC? closestBoss = null;
            float closestDistSq = float.MaxValue;
            float range = 300f * 16f; // Same as GlobalNPC.Range
            float rangeSq = range * range;

            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc.active && npc.boss)
                {
                    float distSq = Vector2.DistanceSquared(Player.Center, npc.Center);
                    if (distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        closestBoss = npc;
                    }
                }
            }

            if (closestBoss != null && closestDistSq < rangeSq)
            {
                int nearbyPlayers = ScalingGlobalNPC.GetPlayersNearby(closestBoss.Center, range);
                if (nearbyPlayers < config.ExpectedPlayers)
                {
                    float diff = config.ExpectedPlayers - nearbyPlayers;
                    float factor = config.ScalingMultiplier * (diff * diff) + 1f;
                    modifiers.IncomingDamageMultiplier *= factor;
                }
            }
        }

        /// <summary>
        /// Run once per tick after player update to safely reset stacks when no boss is active.
        /// </summary>
        public override void PostUpdate()
        {
            if (ShouldProcessSharedState(Player))
            {
                UpdateSharedFightState();
            }

            // If stacks exist but no active boss NPCs are present this frame, reset.
            if (deathsThisBossFight > 0 && !Main.CurrentFrameFlags.AnyActiveBossNPC)
            {
                deathsThisBossFight = 0;

                var config = ModContent.GetInstance<ServerConfig>();
                if (config?.DebugMode == true)
                {
                    Main.NewText("Boss fight ended, resetting death counter", Color.Gray);
                }
            }

            // Expected players recompute removed
        }

        private static bool ShouldProcessSharedState(Player player)
        {
            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                return player.whoAmI == Main.myPlayer;
            }

            if (Main.netMode == NetmodeID.Server)
            {
                return player.whoAmI == 0;
            }

            return player.whoAmI == Main.myPlayer;
        }

        private static void UpdateSharedFightState()
        {
            uint frame = Main.GameUpdateCount;
            if (lastStateUpdateFrame == frame)
            {
                return;
            }

            lastStateUpdateFrame = frame;

            int online = 0;
            int alive = 0;

            for (int i = 0; i < Main.maxPlayers; i++)
            {
                Player player = Main.player[i];
                if (player == null || !player.active)
                {
                    continue;
                }

                online++;
                if (!player.dead && player.statLife > 0 && !player.ghost)
                {
                    alive++;
                }
            }

            cachedOnlinePlayers = Math.Max(1, online);
            cachedAlivePlayers = Math.Min(alive, cachedOnlinePlayers);

            if (Main.CurrentFrameFlags.AnyActiveBossNPC && cachedAlivePlayers < cachedOnlinePlayers)
            {
                cumulativeShortHandedTickCounter++;
            }
            else
            {
                cumulativeShortHandedTickCounter = 0;
            }
        }

        /// <summary>
        /// Calculates the damage multiplier based on three combined scaling factors.
        /// </summary>
        public static double GetCombinedDamageMultiplier(
            long cumulativeTickCounter,
            int alivePlayers,
            int onlinePlayers,
            int totalDeaths,
            int yourDeaths)
        {
            if (alivePlayers >= onlinePlayers)
            {
                return 1.0;
            }

            double aliveFactor = (double)(onlinePlayers - alivePlayers) / onlinePlayers;
            double nam = 1.0 + aliveFactor * NUMER_ALIVE_INTENSITY;

            double averageDeaths = onlinePlayers > 0 ? (double)totalDeaths / onlinePlayers : 0.0;
            double individualDifference = averageDeaths - yourDeaths;
            double dim = Math.Max(1.0, 1.0 + individualDifference * DIFFERENCE_FROM_AVG_INTENSITY);

            double timeInSeconds = (double)cumulativeTickCounter / TICKS_PER_SECOND;
            double tm = 1.0 + Math.Pow(timeInSeconds, 2) * TICK_INTENSITY;

            double finalMultiplier = nam * dim * tm;

            return finalMultiplier;
        }
    }
}