using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Terraria;
using Terraria.Localization;
using Terraria.ID;
using Terraria.ModLoader.Config;

namespace DynamicScaling
{
    public class ServerConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ServerSide;

        [Header("DamageEditor")]
        [DefaultValue(0)]
        [Range(-10, 10)]
        [Increment(1)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.DealDamage.Tooltip")]
        public int DealDamage { get; set; } = 0;

        [DefaultValue(0)]
        [Range(-10, 10)]
        [Increment(1)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.TakeDamage.Tooltip")]
        public int TakeDamage { get; set; } = 0;

        [DefaultValue(false)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.EqualizeDeathsMode.Tooltip")]
        public bool EqualizeDeathsMode { get; set; } = false;

        public Dictionary<string, PlayerTuning> PlayerOverrides { get; set; } = new Dictionary<string, PlayerTuning>
        {
            ["playername"] = new PlayerTuning()
        };

        [Header("BossTimeScaling")]
        [DefaultValue(4)]
        [Range(0, 240)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.ExpectedTotalMinutes.Tooltip")]
        public int ExpectedTotalMinutes { get; set; } = 4;

        [DefaultValue(10)]
        [Range(1, 100)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.MaxDefenseModifier.Tooltip")]
        public int MaxDefenseModifier { get; set; } = 10;

        [DefaultValue(2f)]
        [Range(0.1f, 10f)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.ScalingConstant.Tooltip")]
        public float ScalingConstant { get; set; } = 2f;

        [DefaultValue("0")]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.BossProgressionThreshold.Tooltip")]
        public string BossProgressionThreshold { get; set; } = "0";

        [Header("BossTargetingSettings")]
        [DefaultValue(false)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.TargetHighestHealth.Tooltip")]
        public bool TargetHighestHealth { get; set; } = false;

        [DefaultValue(true)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.StopTargetingIfLowest.Tooltip")]
        public bool StopTargetingIfLowest { get; set; } = true;

        [Header("BossGroupSettings")]
        [DefaultValue(1)]
        [Range(1, 255)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.ExpectedPlayers.Tooltip")]
        public int ExpectedPlayers { get; set; } = 1;

        [DefaultValue(0.3f)]
        [Range(0f, 10f)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.ScalingMultiplier.Tooltip")]
        public float ScalingMultiplier { get; set; } = 0.3f;

        [DefaultValue("0")]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.ExpectedPlayersBossProgressionThreshold.Tooltip")]
        public string ExpectedPlayersBossProgressionThreshold { get; set; } = "0";

        [JsonIgnore]
        public float BossProgressionThresholdValue => TryParseThreshold(BossProgressionThreshold, out float v) ? v : 0f;

        [JsonIgnore]
        public float ExpectedPlayersBossProgressionThresholdValue => TryParseThreshold(ExpectedPlayersBossProgressionThreshold, out float v) ? v : 0f;

        private static bool TryParseThreshold(string input, out float val)
        {
            if (float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out val) && val >= 0f && val < 30f)
            {
                return true;
            }
            val = 0f;
            return false;
        }

        public override bool AcceptClientChanges(ModConfig pendingConfig, int whoAmI, ref NetworkText message)
        {
            var pending = pendingConfig as ServerConfig;
            if (pending == null)
                return true;

            bool changed = false;
            if (!TryParseThreshold(pending.BossProgressionThreshold, out _))
            {
                pending.BossProgressionThreshold = "0";
                changed = true;
            }
            if (!TryParseThreshold(pending.ExpectedPlayersBossProgressionThreshold, out _))
            {
                pending.ExpectedPlayersBossProgressionThreshold = "0";
                changed = true;
            }
            if (changed)
            {
                string reason = "Invalid progression value(s) were reset to 0.";
                message = NetworkText.FromLiteral(reason);
                if (Main.netMode == NetmodeID.Server || Main.netMode == NetmodeID.SinglePlayer)
                {
                    Main.NewText(reason, Color.Yellow);
                }
            }
            return true;
        }

        public override void OnChanged()
        {
            bool changed = false;
            if (!TryParseThreshold(BossProgressionThreshold, out _))
            {
                BossProgressionThreshold = "0";
                changed = true;
            }
            if (!TryParseThreshold(ExpectedPlayersBossProgressionThreshold, out _))
            {
                ExpectedPlayersBossProgressionThreshold = "0";
                changed = true;
            }
            if (changed)
            {
                // Persist corrected values and notify players
                SaveChanges(this, (s, c) => { }, silent: true, broadcast: true);
                string reason = "Invalid progression value(s) were reset to 0.";
                if (Main.netMode == NetmodeID.Server || Main.netMode == NetmodeID.SinglePlayer)
                {
                    Main.NewText(reason, Color.Yellow);
                }
            }
        }

        [Header("BossDynamicDamageScaling")]
        [DefaultValue(0.7f)]
        [Range(0f, 1f)]
        public float HighHealthThreshold { get; set; } = 0.7f;

        [DefaultValue(60)]
        [Range(0, 600)]
        public int HighHealthDelaySeconds { get; set; } = 60;

        [DefaultValue(0.05f)]
        [Range(0f, 1f)]
        public float HighHealthDamageIncreasePerSecond { get; set; } = 0.05f;

        [Header("WeaponAdaptation")]
        [DefaultValue(false)]
        public bool WeaponAdaptationEnabled { get; set; } = false;

        [DefaultValue(2f)]
        [Range(1f, 10f)]
        public float WeaponAdaptationStartMultiplier { get; set; } = 2f;

        [DefaultValue(4f)]
        [Range(1f, 20f)]
        public float WeaponAdaptationCompleteMultiplier { get; set; } = 4f;

        [DefaultValue(200f)]
        [Range(0f, 10000f)]
        public float WeaponAdaptationMinDamage { get; set; } = 200f;

        [DefaultValue(0.2f)]
        [Range(0f, 1f)]
        public float WeaponAdaptationMaxReduction { get; set; } = 0.2f;

        [DefaultValue(false)]
        [TooltipKey("$Mods.DynamicScaling.Configs.ServerConfig.WeaponAdaptationAdaptToSoloPlayers.Tooltip")]
        public bool WeaponAdaptationAdaptToSoloPlayers { get; set; } = false;

        [Header("Admin")]
        [DefaultValue(false)]
        public bool DebugMode { get; set; } = false;

        public class PlayerTuning
        {
            [DefaultValue(0)]
            public int DealDamageModifierDifference { get; set; } = 0;

            [DefaultValue(0)]
            public int TakeDamageModifierDifference { get; set; } = 0;

            [DefaultValue(false)]
            public bool EqualizeDeathsMode { get; set; } = false;
        }
    }
}