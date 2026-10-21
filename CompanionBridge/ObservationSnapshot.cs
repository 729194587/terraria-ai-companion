using System;
using System.Collections.Generic;
using System.Linq;

namespace CompanionBridge;

internal sealed record WorldRect(float X, float Y, float Width, float Height);
internal sealed record TileBounds(int X, int Y, int Width, int Height);
// Row-vector affine matrix: x'=x*M11+y*M21+M41, y'=x*M12+y*M22+M42.
internal sealed record ViewTransform(float M11, float M12, float M21, float M22, float M41, float M42);
internal sealed record CameraSnapshot(long RenderSequence, ulong SourceGameUpdate, string CapturedAt,
    long CapturedMonotonic, StateVector ScreenPosition, WorldRect Viewport, StateVector Zoom,
    ViewTransform Transform, StateVector[] WorldCorners, WorldRect WorldBounds, TileBounds TileBounds);
internal sealed record CameraObservation(CameraSnapshot Frame, double AgeMilliseconds, string Status);
internal sealed record TileCell(string Knowledge, bool HasTile = false, int? TileType = null,
    int? WallType = null, int FrameX = 0, int FrameY = 0, bool HalfBlock = false, int Slope = 0,
    bool Solid = false, bool SolidTop = false, bool Actuated = false, bool Actuator = false,
    int WireMask = 0, int LiquidType = 0, int LiquidAmount = 0, int TilePaint = 0, int WallPaint = 0,
    bool TileInvisible = false, bool WallInvisible = false, bool TileFullbright = false,
    bool WallFullbright = false, string CollisionKnowledge = "known", int TouchDamage = 0,
    bool Hot = false, bool Bleeding = false, bool DestroyOnTouch = false, bool Boulder = false);
internal sealed record ContentType(string Kind, int TypeId, string ContentKey, bool Modded);
internal sealed record TileRow(string Encoding, int[] Data);
internal sealed record ObservationTerrain(int EncodingVersion, int TileSize, TileBounds Bounds,
    ContentType[] Types, TileCell[] Palette, TileRow[] Rows, int UnknownCount, int UninterpretedCount);
internal sealed record ObservedEntity(string Kind, string InstanceKey, int Slot, int? TypeId,
    string ContentKey, string Name, WorldRect BoundsWorldPixels, StateVector VelocityPixelsPerTick,
    int? Life, int? LifeMax, string Classification, string ClassificationBasis,
    bool InViewport, ulong SampleGameUpdate, object Details)
{
    public string Source => "client_known";
    public string Visibility => "geometric_intersection_only; lighting, occlusion, invisibility and mod drawing not evaluated";
}
internal sealed record EntityCoverage(int MatchedCount, int ReturnedCount, int OmittedCount, string Reason);
internal sealed record ObservedEntities(ObservedEntity[] Items, EntityCoverage Coverage);
internal sealed record ObservationCompleteness(bool CompleteWithinScope, string[] Reasons, string[] Limitations);
internal sealed record ObservationSnapshot(int SchemaVersion, string WorldSessionId, string ObservationId,
    ulong GameUpdate, string SampledAt, string Phase, string Status, CameraObservation Camera,
    object Player, ObservationTerrain Terrain, ObservedEntities Entities, object Environment,
    object Interactables, ObservationCompleteness Completeness, double SamplingMilliseconds)
{
    public string Scope => "client_known viewport geometry, not whole world or verified visual visibility";
    public string Coordinates => "world pixels; tile=16px; positions/bounds top-left; +X right,+Y down; velocity pixels/game update";
}

internal static class ObservationGeometry
{
    internal const int MaxTiles = 131072;
    internal static CameraSnapshot Camera(long sequence, ulong tick, string utc, long clock,
        StateVector screen, WorldRect viewport, StateVector zoom, ViewTransform m)
    {
        float det = m.M11 * m.M22 - m.M12 * m.M21;
        if (!float.IsFinite(det) || Math.Abs(det) < 1e-8f || viewport.Width <= 0 || viewport.Height <= 0)
            throw new ArgumentException("Invalid camera transform/viewport");
        // SpriteBatch coordinates are local to the viewport; viewport X/Y are device offsets.
        StateVector Invert(float x, float y)
        {
            x -= m.M41; y -= m.M42;
            return new(screen.X + (x * m.M22 - y * m.M21) / det,
                screen.Y + (y * m.M11 - x * m.M12) / det);
        }
        var corners = new[] { Invert(0, 0), Invert(viewport.Width, 0),
            Invert(viewport.Width, viewport.Height), Invert(0, viewport.Height) };
        if (corners.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || Math.Abs(p.X) > 10000000 || Math.Abs(p.Y) > 10000000))
            throw new ArgumentException("Invalid camera extent");
        float left = corners.Min(p => p.X), top = corners.Min(p => p.Y);
        float right = corners.Max(p => p.X), bottom = corners.Max(p => p.Y);
        int tx = (int)Math.Floor(left / 16), ty = (int)Math.Floor(top / 16);
        var bounds = new TileBounds(tx, ty, (int)Math.Ceiling(right / 16) - tx, (int)Math.Ceiling(bottom / 16) - ty);
        return new(sequence, tick, utc, clock, screen, viewport, zoom, m, corners,
            new(left, top, right - left, bottom - top), bounds);
    }

    internal static bool Intersects(WorldRect rect, StateVector[] polygon)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || polygon.Length != 4) return false;
        var box = new[] { new StateVector(rect.X, rect.Y), new(rect.X + rect.Width, rect.Y),
            new(rect.X + rect.Width, rect.Y + rect.Height), new(rect.X, rect.Y + rect.Height) };
        var axes = new List<StateVector> { new(1, 0), new(0, 1) };
        for (int i = 0; i < 4; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % 4];
            axes.Add(new(-(b.Y - a.Y), b.X - a.X));
        }
        foreach (var axis in axes)
        {
            if (axis.X == 0 && axis.Y == 0) continue;
            float Dot(StateVector p) => p.X * axis.X + p.Y * axis.Y;
            if (box.Max(Dot) <= polygon.Min(Dot) || polygon.Max(Dot) <= box.Min(Dot)) return false;
        }
        return true;
    }

    internal static string CameraStatus(double ageMs, ulong sampleTick, ulong cameraTick) =>
        ageMs > 250 || cameraTick > sampleTick || sampleTick - cameraTick > 2 ? "stale" : "recent_render";
}

internal static class ObservationEncoding
{
    internal static ObservationTerrain Encode(TileBounds bounds, Func<int, int, TileCell> read,
        Func<string, int, ContentType> describe)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > ObservationGeometry.MaxTiles)
            throw new ArgumentException("Viewport tile budget exceeded");
        var palette = new List<TileCell>(); var indices = new Dictionary<TileCell, int>();
        var types = new Dictionary<(string, int), ContentType>(); var rows = new TileRow[bounds.Height];
        int unknown = 0, uninterpreted = 0;
        for (int y = 0; y < bounds.Height; y++)
        {
            var raw = new int[bounds.Width];
            for (int x = 0; x < raw.Length; x++)
            {
                var cell = read(bounds.X + x, bounds.Y + y);
                if (cell.Knowledge != "read") unknown++;
                if (cell.CollisionKnowledge != "known") uninterpreted++;
                if (!indices.TryGetValue(cell, out int index))
                {
                    index = palette.Count; indices.Add(cell, index); palette.Add(cell);
                    foreach (var pair in new[] { ("tile", cell.TileType), ("wall", cell.WallType) })
                        if (pair.Item2 is int id && !types.ContainsKey((pair.Item1, id)))
                            types.Add((pair.Item1, id), describe(pair.Item1, id));
                }
                raw[x] = index;
            }
            var runs = new List<int>();
            for (int x = 0; x < raw.Length;)
            {
                int end = x + 1;
                while (end < raw.Length && raw[end] == raw[x]) end++;
                runs.Add(end - x); runs.Add(raw[x]); x = end;
            }
            rows[y] = runs.Count < raw.Length ? new("rle", runs.ToArray()) : new("indices", raw);
        }
        return new(1, 16, bounds, types.Values.ToArray(), palette.ToArray(), rows, unknown, uninterpreted);
    }

    internal static TileCell[] Decode(TileRow row, TileCell[] palette, int width)
    {
        var result = new List<TileCell>();
        void Add(int index, int count)
        {
            if (index < 0 || index >= palette.Length || count <= 0 || count > width - result.Count)
                throw new FormatException("Invalid tile row index/count");
            for (int i = 0; i < count; i++) result.Add(palette[index]);
        }
        if (row.Encoding == "indices") foreach (int index in row.Data) Add(index, 1);
        else if (row.Encoding == "rle" && row.Data.Length % 2 == 0)
            for (int i = 0; i < row.Data.Length; i += 2) Add(row.Data[i + 1], row.Data[i]);
        else throw new FormatException("Unsupported tile row encoding");
        if (result.Count != width) throw new FormatException("Invalid decoded tile row length");
        return result.ToArray();
    }
}
