using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace CompanionBridge.ServerIpc;

// Pure geometry for the drop-collection stance search. Kept free of Terraria types
// so the native grab rectangle can be checked without a world or a running game.
internal static class DropStanceSearch
{
    // World-pixel bounds of the stance search for one drop: the item hitbox grown by
    // the full Player.GetItemGrabRange on every side, then clipped to the world.
    internal readonly record struct Limits(int Left, int Right, int Top, int Bottom);
    internal readonly record struct Band(int Left, int Right, int Feet);
    internal readonly record struct CoveredBand(int Left, int Right, int Count);

    internal static IEnumerable<CoveredBand> CoverageBands(int left,int right,int feet,Limits[] coverage)
    {
        if(left>right)yield break;
        var changes=new SortedDictionary<int,int> { [left]=0,[right+1]=0 };
        foreach(var bounds in coverage)
        {
            if(feet<bounds.Top||feet>bounds.Bottom)continue;
            int start=Math.Max(left,bounds.Left), end=Math.Min(right,bounds.Right);
            if(start>end)continue;
            changes[start]=changes.GetValueOrDefault(start)+1;
            changes[end+1]=changes.GetValueOrDefault(end+1)-1;
        }
        int previous=left,count=0;
        foreach(var change in changes)
        {
            if(change.Value!=0||change.Key==right+1)
            {
                if(change.Key>previous)yield return new(previous,change.Key-1,count);
                previous=change.Key;
            }
            count+=change.Value;
        }
    }

    internal static Limits Bounds(Rectangle itemHitbox, int playerWidth, int playerHeight,
        int grabRange, int maxTilesX, int maxTilesY)
    {
        int range = Math.Max(0, grabRange);
        int left = (int)Math.Max(32, (long)itemHitbox.Left - playerWidth - range + 1);
        int right = (int)Math.Min((maxTilesX - 2) * 16 - playerWidth, (long)itemHitbox.Right + range - 1);
        int top = (int)Math.Max(32 + playerHeight, (long)itemHitbox.Top - range + 1);
        int bottom = (int)Math.Min((maxTilesY - 2) * 16 - 1, (long)itemHitbox.Bottom + range + playerHeight - 1);
        return new(left, right, top, bottom);
    }

    internal static bool Scan(Limits bounds, int actorX, int actorFeet, int budget,
        Func<int, int, bool> safe, Action<Band> accept)
    {
        int firstRow = (bounds.Top + 7) / 8, lastRow = bounds.Bottom / 8;
        if (bounds.Left > bounds.Right || firstRow > lastRow) return true;
        int width = bounds.Right - bounds.Left + 1;
        foreach (int row in Enumerable.Range(firstRow, lastRow - firstRow + 1)
                     .OrderBy(row => Math.Abs(row * 8 - actorFeet)))
        {
            if (budget == 0) return false;
            int count = Math.Min(width, budget);
            // A partial row probes the interval nearest the Actor. Only adjacent
            // successful pixel probes can form a continuous navigation goal.
            int left = Math.Clamp(actorX - count / 2, bounds.Left, bounds.Right - count + 1);
            int end = left + count;
            int start = left;
            for (int x = left; x < end; x++)
            {
                if (safe(x, row * 8)) continue;
                if (start < x) accept(new(start, x - 1, row * 8));
                start = x + 1;
            }
            if (start < end) accept(new(start, end - 1, row * 8));
            budget -= count;
            if (count < width) return false;
        }
        return true;
    }
}
