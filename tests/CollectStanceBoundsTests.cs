// Pure stance-search bounds for the ACTUAL DropStanceSearch.Bounds.
// No Terraria world is involved, so this runs in any harness that can see the type.
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using CompanionBridge.ServerIpc;

public static class CollectStanceBoundsTests
{
    private static int checks;
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception(text);
        checks++;
        Console.WriteLine("PASS collect stance bounds: " + text);
    }

    private const int WorldTilesX = 8400, WorldTilesY = 2400;

    public static void Run()
    {
        // A 20x42 actor and a 16x16 item resting at the origin of a normal world.
        var item = new Rectangle(1600, 1600, 16, 16);

        // 1. A large native grab range must widen the search to that range. The old
        //    implementation clamped the range to 96 pixels, so this bound would have
        //    been identical to the 96 case.
        var wide = DropStanceSearch.Bounds(item, 20, 42, 400, WorldTilesX, WorldTilesY);
        var narrow = DropStanceSearch.Bounds(item, 20, 42, 96, WorldTilesX, WorldTilesY);
        Check(wide.Right - wide.Left > narrow.Right - narrow.Left, "a 400px grab range searches wider than a 96px range");
        Check(wide.Bottom - wide.Top > narrow.Bottom - narrow.Top, "a 400px grab range searches taller than a 96px range");
        Check(narrow.Right - narrow.Left == item.Width + 20 + 2 * 96 - 2, "the 96px case still matches the native rectangle");

        // 2. The search must reach at least the full native range on each side, so a
        //    stance at the very edge of the pull rectangle is still considered.
        Check(wide.Left <= item.Left - 400 - 20 + 1, "the search reaches the native left edge");
        Check(wide.Right >= item.Right + 400 - 1, "the search reaches the native right edge");
        Check(wide.Top <= item.Top - 400 + 1, "the search reaches the native top edge");
        Check(wide.Bottom >= item.Bottom + 400 + 42 - 1, "the search reaches the native bottom edge");

        // 3. No range may be reduced below the native value to save work.
        foreach (int range in new[] { 0, 1, 96, 120, 240, 400, 960 })
        {
            var bounds = DropStanceSearch.Bounds(item, 20, 42, range, WorldTilesX, WorldTilesY);
            Check(bounds.Left <= item.Left - range - 20 + 1 || bounds.Left == 32,
                "range " + range + " keeps the native left reach unless clipped by the world");
            Check(bounds.Right >= item.Right + range - 1 || bounds.Right == (WorldTilesX - 2) * 16 - 20,
                "range " + range + " keeps the native right reach unless clipped by the world");
        }

        // 4. World clipping stays in place, so the search never leaves the map.
        var atEdge = DropStanceSearch.Bounds(new Rectangle(32, 32, 16, 16), 20, 42, 400, WorldTilesX, WorldTilesY);
        Check(atEdge.Left >= 32 && atEdge.Top >= 32, "the search stays inside the world origin");
        var farEdge = DropStanceSearch.Bounds(
            new Rectangle((WorldTilesX - 2) * 16 - 16, (WorldTilesY - 2) * 16 - 16, 16, 16),
            20, 42, 400, WorldTilesX, WorldTilesY);
        Check(farEdge.Right <= (WorldTilesX - 2) * 16 - 20, "the search stays inside the world width");
        Check(farEdge.Bottom <= (WorldTilesY - 2) * 16 - 1, "the search stays inside the world height");

        // 5. An out-of-range or empty rectangle reports no searchable area rather
        //    than a negative one.
        var empty = DropStanceSearch.Bounds(item, 20, 42, 0, 2, 2);
        Check(empty.Left > empty.Right || empty.Top > empty.Bottom, "an unusable world region yields no stance area");

        var bands = new List<DropStanceSearch.Band>();
        bool complete = DropStanceSearch.Scan(new(1, 20, 80, 80), 10, 80, 20,
            (x, _) => x != 9, bands.Add);
        Check(complete && bands.Count == 2 && bands[0] == new DropStanceSearch.Band(1, 8, 80)
            && bands[1] == new DropStanceSearch.Band(10, 20, 80), "adjacent safe pixels form bands separated by a single unsafe pixel");

        bands.Clear();
        complete = DropStanceSearch.Scan(wide, item.Left, 1600, 6144,
            (x, feet) => x == item.Left + 250 && feet == 1600, bands.Add);
        Check(!complete && bands.Contains(new(item.Left + 250, item.Left + 250, 1600)),
            "a stance beyond 96px can be probed at its actual coordinate");

        bands.Clear();
        int probes = 0;
        complete = DropStanceSearch.Scan(new(1, 20, 80, 88), 20, 80, 5,
            (x, feet) => { probes++; return feet == 88; }, bands.Add);
        Check(!complete && bands.Count == 0 && probes == 5, "exhaustion with unsearched safe ground is not an exhaustive miss");
        complete = DropStanceSearch.Scan(new(1, 1, 80, 80), 1, 80, 1, (_, _) => true, bands.Add);
        Check(complete && bands.Count == 1 && bands[0] == new DropStanceSearch.Band(1, 1, 80),
            "one inclusive pixel and one foot level can produce a stance");

        Console.WriteLine($"Collect stance bounds: {checks} checks; native range preserved, world clipping intact.");
    }
}
