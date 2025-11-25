using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace DynamicScaling
{
    /// <summary>
    /// GlobalBossBar for displaying boss health. This is CLIENT-SIDE ONLY and used for rendering.
    /// For game logic calculations, use BossGroupTracker.GetBossHealth() which works server-side.
    /// </summary>
    public class ScalingBossBar : GlobalBossBar
    {
        public override bool PreDraw(SpriteBatch spriteBatch, NPC npc, ref BossBarDrawParams drawParams)
        {
            // This hook is client-side only and called during rendering
            // The drawParams already contains the aggregated health from the boss bar's
            // ValidateAndCollectNecessaryInfo method (which handles multipart bosses correctly)
            
            // No modifications needed - just let vanilla/modded boss bars handle the aggregation
            return true;
        }

        // External API helpers used by server-side logic to access aggregated boss health.
        public static void ClearCache()
        {
            BossGroupTracker.ClearCache();
        }

        public static bool TryGetBossHealth(int whoAmI, out float life, out float lifeMax)
        {
            life = 0f;
            lifeMax = 1f;
            NPC npc = Main.npc[whoAmI];
            if (npc == null || !npc.active || !npc.boss)
            {
                return false;
            }
            return BossGroupTracker.GetBossHealth(npc, out life, out lifeMax);
        }
    }
}
