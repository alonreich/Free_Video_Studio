using System;
using System.Collections.Generic;

namespace FreeVideoStudio.Core.Media;

public static class TimelineReorder
{
    public static int TargetIndex(IReadOnlyList<(double X0, double X1)> blocks, int from, double pointerX)
    {
        int target = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i == from) continue;
            if ((blocks[i].X0 + blocks[i].X1) / 2.0 < pointerX) target++;
        }
        return Math.Clamp(target, 0, Math.Max(0, blocks.Count - 1));
    }

    public static double InsertionX(IReadOnlyList<(double X0, double X1)> blocks, int from, int target)
    {
        var others = new List<(double X0, double X1)>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++) if (i != from) others.Add(blocks[i]);
        if (others.Count == 0) return blocks.Count > 0 ? blocks[0].X0 : 0;
        if (target <= 0) return others[0].X0;
        return others[Math.Min(target, others.Count) - 1].X1;
    }
}
