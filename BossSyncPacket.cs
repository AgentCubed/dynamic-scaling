using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace DynamicScaling
{
    internal static class BossSyncPacket
    {
        private const byte PacketTypeSyncBossModifiers = 0;
        private const byte PacketTypeReportComboDamage = 1;
        private const byte PacketTypeSyncAdaptation = 2;

        public static void SendBossModifiersForNPC(int npcWhoAmI, float defense, float offense)
        {
            if (Main.netMode == NetmodeID.SinglePlayer)
                return;

            ModPacket packet = ModContent.GetInstance<DynamicScaling>().GetPacket();
            packet.Write(PacketTypeSyncBossModifiers);
            packet.Write(npcWhoAmI);
            packet.Write(defense);
            packet.Write(offense);

            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.DebugMode == true)
            {
                DebugUtil.EmitDebug($"[BossSyncPacket] SENDING modifier packet: npcIdx={npcWhoAmI}, def={defense:F2}x, off={offense:F2}x", Microsoft.Xna.Framework.Color.Yellow);
            }

            if (Main.netMode == NetmodeID.Server)
                packet.Send();
            else
                packet.Send(-1, Main.myPlayer);
        }

        public static void SendDamageReportToServer(int npcWhoAmI, int weaponKey, int damage)
        {
            // Only send reports from clients
            if (Main.netMode != NetmodeID.MultiplayerClient)
                return;

            ModPacket packet = ModContent.GetInstance<DynamicScaling>().GetPacket();
            packet.Write(PacketTypeReportComboDamage);
            packet.Write(npcWhoAmI);
            packet.Write(weaponKey);
            packet.Write(damage);

            packet.Send();
        }

        public static void SendAdaptationForNPC(int npcWhoAmI, int playerId, int weaponKey, float factor)
        {
            if (Main.netMode == NetmodeID.SinglePlayer)
                return;

            ModPacket packet = ModContent.GetInstance<DynamicScaling>().GetPacket();
            packet.Write(PacketTypeSyncAdaptation);
            packet.Write(npcWhoAmI);
            packet.Write(playerId);
            packet.Write(weaponKey);
            packet.Write(factor);

            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.DebugMode == true)
            {
                DebugUtil.EmitDebug($"[BossSyncPacket] SENDING adaptation packet: npcIdx={npcWhoAmI}, player={playerId}, weaponKey={weaponKey}, factor={factor:F2}", Microsoft.Xna.Framework.Color.Yellow);
            }

            if (Main.netMode == NetmodeID.Server)
                packet.Send();
            else
                packet.Send(-1, Main.myPlayer);
        }

        public static void HandlePacket(BinaryReader reader, int whoAmI)
        {
            byte packetType = reader.ReadByte();
            switch (packetType)
            {
                case PacketTypeSyncBossModifiers:
                    ReceiveSyncBossModifiers(reader);
                    break;
                case PacketTypeReportComboDamage:
                    ReceiveReportComboDamage(reader, whoAmI);
                    break;
                case PacketTypeSyncAdaptation:
                    ReceiveSyncAdaptation(reader);
                    break;
                default:
                    break;
            }
        }

        private static void ReceiveSyncBossModifiers(BinaryReader reader)
        {
            int npcIndex = reader.ReadInt32();
            float def = reader.ReadSingle();
            float off = reader.ReadSingle();

            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.DebugMode == true)
            {
                DebugUtil.EmitDebug($"[BossSyncPacket] RECEIVED modifier packet: npcIdx={npcIndex}, def={def:F2}x, off={off:F2}x", Microsoft.Xna.Framework.Color.Yellow);
            }

            // Apply to client-side cache
            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                ScalingGlobalNPC.SetClientModifiers(npcIndex, def, off);
                if (config?.DebugMode == true)
                {
                    DebugUtil.EmitDebug($"[BossSyncPacket] Cache updated: npcIdx={npcIndex}, def={def:F2}x, off={off:F2}x", Microsoft.Xna.Framework.Color.Green);
                }
            }
        }

        private static void ReceiveReportComboDamage(BinaryReader reader, int whoAmI)
        {
            int npcIndex = reader.ReadInt32();
            int weaponKey = reader.ReadInt32();
            int damage = reader.ReadInt32();

            if (Main.netMode == NetmodeID.Server)
            {
                NPC npc = Main.npc[npcIndex];
                if (npc == null || !npc.active || !npc.boss) return;

                ScalingGlobalNPC.ReportComboDamage(npcIndex, whoAmI, weaponKey, damage);
            }
        }

        private static void ReceiveSyncAdaptation(BinaryReader reader)
        {
            int npcIndex = reader.ReadInt32();
            int playerId = reader.ReadInt32();
            int weaponKey = reader.ReadInt32();
            float factor = reader.ReadSingle();

            var config = ModContent.GetInstance<ServerConfig>();
            if (config?.DebugMode == true)
            {
                DebugUtil.EmitDebug($"[BossSyncPacket] RECEIVED adaptation packet: npcIdx={npcIndex}, player={playerId}, weaponKey={weaponKey}, factor={factor:F2}", Microsoft.Xna.Framework.Color.Yellow);
            }

            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                ScalingGlobalNPC.SetClientAdaptationFactor(npcIndex, (playerId, weaponKey), factor);
                if (config?.DebugMode == true)
                {
                    DebugUtil.EmitDebug($"[BossSyncPacket] Client adaptation cache updated: npcIdx={npcIndex}, player={playerId}, weaponKey={weaponKey}, factor={factor:F2}", Microsoft.Xna.Framework.Color.Green);
                }
            }
        }
    }
}
