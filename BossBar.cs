using System;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace DynamicScaling
{
    /// <summary>
    /// GlobalBossBar for displaying boss health. This is CLIENT-SIDE ONLY and used for rendering.
    /// For game logic calculations, use BossBar.TryGetBossHealth which works server-side for aggregation and falls back to npc.life for single-part bosses.
    /// </summary>
    public class BossBar : GlobalBossBar
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
            // No-op when BossGroupTracker is removed.
            // Keep the method to preserve external API compatibility.
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

            // If a ModBossBar or other IBigProgressBar instance is available on the NPC, use it to aggregate health.
            var bossBar = npc.BossBar;
            if (bossBar == null)
                return false; // fallback to npc.life/npc.lifeMax at the caller

            // Create BigProgressBarInfo and validate
            var info = new Terraria.GameContent.UI.BigProgressBar.BigProgressBarInfo
            {
                npcIndexToAimAt = npc.whoAmI
            };

            if (!bossBar.ValidateAndCollectNecessaryInfo(ref info))
                return false;

            // If it's a ModBossBar (modded boss bar), it may expose Life and LifeMax
            if (bossBar is ModBossBar modBar)
            {
                life = modBar.Life;
                lifeMax = modBar.LifeMax;
                return lifeMax > 0;
            }

            // Otherwise, attempt to read _cache.LifeCurrent / LifeMax via reflection for vanilla bars
            var cacheField = bossBar.GetType().GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (cacheField != null)
            {
                var cache = cacheField.GetValue(bossBar);
                if (cache != null)
                {
                    var lifeCurrentF = cache.GetType().GetField("LifeCurrent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var lifeMaxF = cache.GetType().GetField("LifeMax", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (lifeCurrentF != null && lifeMaxF != null)
                    {
                        try
                        {
                            life = Convert.ToSingle(lifeCurrentF.GetValue(cache));
                            lifeMax = Convert.ToSingle(lifeMaxF.GetValue(cache));
                            return lifeMax > 0;
                        }
                        catch { }
                    }
                }
            }

            return false;
        }
    }
}
