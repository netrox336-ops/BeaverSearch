namespace BeaverSearch.Models;

public sealed record Cs2InspectData(
    uint DefIndex,
    uint PaintIndex,
    uint PaintSeed,
    float PaintWear,
    IReadOnlyList<uint> StickerIds);
