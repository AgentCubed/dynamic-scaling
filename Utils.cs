using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.Localization;
using Terraria.ID;

namespace DynamicScaling
{
    internal static class Utils
    {
        public static void EmitDebug(string message, Color color)
        {
            if (Main.netMode == NetmodeID.Server)
            {
                ChatHelper.BroadcastChatMessage(NetworkText.FromLiteral(message), color);
            }
            else
            {
                Main.NewText(message, color);
            }
        }
    }
}