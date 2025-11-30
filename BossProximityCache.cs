using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

#nullable enable

namespace DynamicScaling
{
    public class BossProximityCache : ModSystem
    {
        private static float _bossRange = 500f * 16f;
        public static float BossRange => _bossRange;
        public static float BossRangeSq => BossRange * BossRange;
        private const int UpdateIntervalTicks = 6;

        private static readonly Dictionary<int, int> playerClosestBoss = new();
        private static readonly Dictionary<int, HashSet<int>> bossNearbyPlayers = new();
        private static uint lastUpdateFrame = uint.MaxValue;

        public override void PostUpdateNPCs()
        {
            uint frame = Main.GameUpdateCount;
            if (frame - lastUpdateFrame < UpdateIntervalTicks && lastUpdateFrame != uint.MaxValue)
                return;

            lastUpdateFrame = frame;
            if (Main.netMode == NetmodeID.Server)
            {
                _bossRange = ModContent.GetInstance<ServerConfig>().Range * 16f;
            }
            RefreshCache();
        }

        private static void RefreshCache()
        {
            playerClosestBoss.Clear();
            bossNearbyPlayers.Clear();

            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc.active && npc.boss)
                {
                    bossNearbyPlayers[i] = new HashSet<int>();
                }
            }

            for (int p = 0; p < Main.maxPlayers; p++)
            {
                Player player = Main.player[p];
                if (player == null || !player.active || player.dead || player.ghost)
                    continue;

                int closestBossIndex = -1;
                float closestDistSq = float.MaxValue;

                foreach (int bossIndex in bossNearbyPlayers.Keys)
                {
                    NPC boss = Main.npc[bossIndex];
                    float distSq = Vector2.DistanceSquared(player.Center, boss.Center);

                    if (distSq < BossRangeSq)
                    {
                        bossNearbyPlayers[bossIndex].Add(p);
                    }

                    if (distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        closestBossIndex = bossIndex;
                    }
                }

                if (closestBossIndex >= 0 && closestDistSq < BossRangeSq)
                {
                    playerClosestBoss[p] = closestBossIndex;
                }
            }
        }

        public static bool IsPlayerNearAnyBoss(int playerIndex)
        {
            return playerClosestBoss.ContainsKey(playerIndex);
        }

        public static bool IsPlayerNearAnyBoss(Player player)
        {
            return playerClosestBoss.ContainsKey(player.whoAmI);
        }

        public static bool TryGetClosestBoss(int playerIndex, out NPC? boss)
        {
            if (playerClosestBoss.TryGetValue(playerIndex, out int bossIndex))
            {
                boss = Main.npc[bossIndex];
                return boss.active && boss.boss;
            }
            boss = null;
            return false;
        }

        public static bool TryGetClosestBoss(Player player, out NPC? boss)
        {
            return TryGetClosestBoss(player.whoAmI, out boss);
        }

        public static int GetNearbyPlayerCount(int bossIndex)
        {
            if (bossNearbyPlayers.TryGetValue(bossIndex, out var set))
                return set.Count;
            return 0;
        }

        public static int GetNearbyPlayerCount(NPC boss)
        {
            return GetNearbyPlayerCount(boss.whoAmI);
        }

        public static HashSet<int> GetNearbyPlayers(int bossIndex)
        {
            if (bossNearbyPlayers.TryGetValue(bossIndex, out var set))
                return set;
            return new HashSet<int>();
        }

        public static HashSet<int> GetNearbyPlayers(NPC boss)
        {
            return GetNearbyPlayers(boss.whoAmI);
        }

        public override void OnWorldUnload()
        {
            playerClosestBoss.Clear();
            bossNearbyPlayers.Clear();
            lastUpdateFrame = uint.MaxValue;
        }
    }
}
