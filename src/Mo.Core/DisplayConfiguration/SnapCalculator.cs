namespace Mo.Core.DisplayConfiguration;

// Edge-snapping + overlap resolution for the profile editor.
// Coordinates are desktop pixels; callers convert canvas ↔ desktop via DisplayTopology.
public static class SnapCalculator
{
    public readonly record struct AlignmentLine(bool IsVertical, int DesktopPos, int StartPerp, int EndPerp);

    public sealed record SnapResult(int X, int Y, IReadOnlyList<AlignmentLine> Guides);

    // Snap `dragging` (positioned at requestedX/Y) to the nearest edge of any rect in `others`.
    // toleranceDesktopPx is the maximum gap that will trigger a snap.
    public static SnapResult ComputeSnap(
        DisplayTopology.MonitorRect dragging,
        int requestedX,
        int requestedY,
        IReadOnlyList<DisplayTopology.MonitorRect> others,
        int toleranceDesktopPx)
    {
        int snappedX = requestedX;
        int snappedY = requestedY;
        int bestDxDist = int.MaxValue;
        int bestDyDist = int.MaxValue;
        var guides = new List<AlignmentLine>();

        int draggingRight = requestedX + dragging.Width;
        int draggingBottom = requestedY + dragging.Height;

        foreach (var o in others)
        {
            int oRight = o.X + o.Width;
            int oBottom = o.Y + o.Height;

            // Horizontal edge candidates: my.left==o.right, my.right==o.left, my.left==o.left, my.right==o.right
            TryX(requestedX, o.X, o.X, ref snappedX, ref bestDxDist);
            TryX(requestedX, oRight, oRight, ref snappedX, ref bestDxDist);
            TryX(draggingRight, o.X, o.X - dragging.Width, ref snappedX, ref bestDxDist);
            TryX(draggingRight, oRight, oRight - dragging.Width, ref snappedX, ref bestDxDist);

            // Vertical edge candidates
            TryY(requestedY, o.Y, o.Y, ref snappedY, ref bestDyDist);
            TryY(requestedY, oBottom, oBottom, ref snappedY, ref bestDyDist);
            TryY(draggingBottom, o.Y, o.Y - dragging.Height, ref snappedY, ref bestDyDist);
            TryY(draggingBottom, oBottom, oBottom - dragging.Height, ref snappedY, ref bestDyDist);
        }

        // Collect alignment guides for whatever snap actually took
        if (bestDxDist <= toleranceDesktopPx)
        {
            foreach (var o in others)
            {
                int oRight = o.X + o.Width;
                if (snappedX == o.X || snappedX + dragging.Width == o.X)
                    guides.Add(VerticalGuide(o.X, snappedY, dragging, o));
                if (snappedX == oRight || snappedX + dragging.Width == oRight)
                    guides.Add(VerticalGuide(oRight, snappedY, dragging, o));
            }
        }
        else
        {
            snappedX = requestedX;
        }

        if (bestDyDist <= toleranceDesktopPx)
        {
            foreach (var o in others)
            {
                int oBottom = o.Y + o.Height;
                if (snappedY == o.Y || snappedY + dragging.Height == o.Y)
                    guides.Add(HorizontalGuide(o.Y, snappedX, dragging, o));
                if (snappedY == oBottom || snappedY + dragging.Height == oBottom)
                    guides.Add(HorizontalGuide(oBottom, snappedX, dragging, o));
            }
        }
        else
        {
            snappedY = requestedY;
        }

        return new SnapResult(snappedX, snappedY, guides);

        static void TryX(int draggingEdge, int targetEdge, int candidateX, ref int snapped, ref int bestDist)
        {
            int dist = Math.Abs(draggingEdge - targetEdge);
            if (dist < bestDist)
            {
                bestDist = dist;
                snapped = candidateX;
            }
        }

        static void TryY(int draggingEdge, int targetEdge, int candidateY, ref int snapped, ref int bestDist)
        {
            int dist = Math.Abs(draggingEdge - targetEdge);
            if (dist < bestDist)
            {
                bestDist = dist;
                snapped = candidateY;
            }
        }

        static AlignmentLine VerticalGuide(int x, int draggingY, DisplayTopology.MonitorRect dragging, DisplayTopology.MonitorRect other)
        {
            int top = Math.Min(draggingY, other.Y);
            int bottom = Math.Max(draggingY + dragging.Height, other.Y + other.Height);
            return new AlignmentLine(IsVertical: true, x, top, bottom);
        }

        static AlignmentLine HorizontalGuide(int y, int draggingX, DisplayTopology.MonitorRect dragging, DisplayTopology.MonitorRect other)
        {
            int left = Math.Min(draggingX, other.X);
            int right = Math.Max(draggingX + dragging.Width, other.X + other.Width);
            return new AlignmentLine(IsVertical: false, y, left, right);
        }
    }

    // True when `target` shares at least one pixel of an edge with any rect in `others`.
    // Matches Windows behavior: a single-pixel corner touch counts as adjacent.
    public static bool HasAdjacentEdge(DisplayTopology.MonitorRect target, IReadOnlyList<DisplayTopology.MonitorRect> others)
    {
        int tR = target.X + target.Width;
        int tB = target.Y + target.Height;
        foreach (var o in others)
        {
            int oR = o.X + o.Width;
            int oB = o.Y + o.Height;

            bool verticalEdgeShare =
                (target.X == oR || tR == o.X) &&
                target.Y < oB && tB > o.Y;
            bool horizontalEdgeShare =
                (target.Y == oB || tB == o.Y) &&
                target.X < oR && tR > o.X;

            if (verticalEdgeShare || horizontalEdgeShare) return true;
        }
        return false;
    }

    // If `target` has no shared edge with any rect in `others`, pull it to the closest
    // adjacent-and-non-overlapping position. The sliding dimension is clamped so at least
    // one pixel of overlap exists on the shared axis.
    public static (int X, int Y) EnforceAdjacency(
        DisplayTopology.MonitorRect target,
        IReadOnlyList<DisplayTopology.MonitorRect> others)
    {
        if (others.Count == 0) return (target.X, target.Y);
        if (HasAdjacentEdge(target, others)) return (target.X, target.Y);

        int bestX = target.X;
        int bestY = target.Y;
        long bestDistSq = long.MaxValue;

        foreach (var o in others)
        {
            foreach (var (cx, cy) in AdjacentCandidates(target, o))
            {
                var candidateRect = new DisplayTopology.MonitorRect(cx, cy, target.Width, target.Height);
                if (WouldOverlap(candidateRect, others)) continue;

                long dx = cx - target.X;
                long dy = cy - target.Y;
                long distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestX = cx;
                    bestY = cy;
                }
            }
        }
        return (bestX, bestY);
    }

    private static IEnumerable<(int X, int Y)> AdjacentCandidates(DisplayTopology.MonitorRect target, DisplayTopology.MonitorRect other)
    {
        int oR = other.X + other.Width;
        int oB = other.Y + other.Height;

        // Clamp so at least one pixel of overlap exists on the shared axis.
        int yForHoriz = Math.Clamp(target.Y, other.Y - target.Height + 1, oB - 1);
        int xForVert = Math.Clamp(target.X, other.X - target.Width + 1, oR - 1);

        yield return (oR, yForHoriz);                  // attach to other's right edge
        yield return (other.X - target.Width, yForHoriz); // attach to other's left edge
        yield return (xForVert, oB);                   // attach to other's bottom edge
        yield return (xForVert, other.Y - target.Height); // attach to other's top edge
    }

    public static bool WouldOverlap(DisplayTopology.MonitorRect target, IReadOnlyList<DisplayTopology.MonitorRect> others)
    {
        int targetRight = target.X + target.Width;
        int targetBottom = target.Y + target.Height;

        foreach (var o in others)
        {
            int oRight = o.X + o.Width;
            int oBottom = o.Y + o.Height;
            // Shared edge is OK; strictly intersecting area is not.
            if (target.X < oRight && targetRight > o.X &&
                target.Y < oBottom && targetBottom > o.Y)
                return true;
        }
        return false;
    }

    // If `target` overlaps any rect in `others`, push it to the closest non-overlapping
    // position along the axis that needs the least movement. Returns original when clear.
    public static (int X, int Y) ResolveOverlap(
        DisplayTopology.MonitorRect target,
        IReadOnlyList<DisplayTopology.MonitorRect> others)
    {
        if (!WouldOverlap(target, others)) return (target.X, target.Y);

        int bestX = target.X;
        int bestY = target.Y;
        int bestMove = int.MaxValue;

        foreach (var o in others)
        {
            int oRight = o.X + o.Width;
            int oBottom = o.Y + o.Height;

            // Four candidate positions: push left/right/up/down to just touch o's edge.
            TryCandidate(oRight, target.Y, target, others, ref bestX, ref bestY, ref bestMove, target.X, target.Y);
            TryCandidate(o.X - target.Width, target.Y, target, others, ref bestX, ref bestY, ref bestMove, target.X, target.Y);
            TryCandidate(target.X, oBottom, target, others, ref bestX, ref bestY, ref bestMove, target.X, target.Y);
            TryCandidate(target.X, o.Y - target.Height, target, others, ref bestX, ref bestY, ref bestMove, target.X, target.Y);
        }

        return (bestX, bestY);

        static void TryCandidate(
            int nx, int ny,
            DisplayTopology.MonitorRect target,
            IReadOnlyList<DisplayTopology.MonitorRect> others,
            ref int bestX, ref int bestY, ref int bestMove,
            int originX, int originY)
        {
            var candidate = new DisplayTopology.MonitorRect(nx, ny, target.Width, target.Height);
            if (WouldOverlap(candidate, others)) return;
            int move = Math.Abs(nx - originX) + Math.Abs(ny - originY);
            if (move < bestMove)
            {
                bestMove = move;
                bestX = nx;
                bestY = ny;
            }
        }
    }
}
