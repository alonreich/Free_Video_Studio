using System;
using System.Collections.Generic;

namespace FreeVideoStudio.App.Infrastructure;

public static class MemeMusicPreview
{
    public static bool ActiveMemeStopsMusic(IEnumerable<FreeVideoStudio.Core.Media.MemePlacement>? memes, string? activeMemeId)
    {
        if (memes == null || string.IsNullOrEmpty(activeMemeId)) return false;
        foreach (var m in memes)
            if (m != null && string.Equals(m.Id, activeMemeId, StringComparison.Ordinal))
                return !m.IsCornerOverlay && !m.MusicKeepsPlaying;
        return false;
    }
}
