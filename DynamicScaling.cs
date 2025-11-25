using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria.ModLoader;
using System.IO;

namespace DynamicScaling
{
	// Please read https://github.com/tModLoader/tModLoader/wiki/Basic-tModLoader-Modding-Guide#mod-skeleton-contents for more information about the various files in a mod.
	public class DynamicScaling : Mod
	{
		public override void HandlePacket(BinaryReader reader, int whoAmI)
		{
			// Delegate to subpacket handlers - handlers will read the packet type byte
			BossSyncPacket.HandlePacket(reader, whoAmI);
		}

	}
}
