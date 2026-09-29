using System;

namespace PierCam.Imaging;

/// <summary>
/// How the frame is turned before anything else sees it: mirror first, then quarter turns
/// clockwise.
///
/// A pier camera is mounted wherever it fits, which is not always the right way up, and turning
/// it physically may not be an option once it is weatherproofed. Everything downstream — the live
/// view, the recording, the star calibration, the censor mask — works in the turned frame, so the
/// picture and the geometry can never disagree about which way is up.
///
/// Mirror-then-rotate is the canonical form of the eight ways a rectangle can be placed, so any
/// combination of flips and turns the user asks for reduces to one of these.
/// </summary>
internal readonly record struct FrameOrientation(bool Mirror, int Quarters)
{
    public static FrameOrientation None => new(false, 0);

    /// <summary>
    /// The user's three switches reduced to canonical form. A vertical flip is a horizontal one
    /// followed by a half turn, which is how it folds into the same representation.
    /// </summary>
    public static FrameOrientation From(int rotateDegrees, bool flipHorizontal, bool flipVertical)
    {
        var quarters = ((rotateDegrees / 90) % 4 + 4) % 4;
        var mirror = flipHorizontal ^ flipVertical;
        if (flipVertical) quarters = (quarters + 2) % 4;
        return new FrameOrientation(mirror, quarters);
    }

    public bool IsIdentity => !Mirror && Quarters == 0;

    /// <summary>Whether width and height swap.</summary>
    public bool Swaps => (Quarters & 1) == 1;

    public (int Width, int Height) Apply(int width, int height) => Swaps ? (height, width) : (width, height);

    /// <summary>
    /// Where a source pixel lands. Used to build the frame and to place anything measured on the
    /// sensor — a censor polygon, say — into the turned frame.
    /// </summary>
    public (int X, int Y) Map(int x, int y, int width, int height)
    {
        if (Mirror) x = width - 1 - x;
        return Quarters switch
        {
            1 => (height - 1 - y, x),
            2 => (width - 1 - x, height - 1 - y),
            3 => (y, width - 1 - x),
            _ => (x, y),
        };
    }

    /// <summary>The same in normalised coordinates, for points stored independently of frame size.</summary>
    public (double X, double Y) MapUnit(double x, double y)
    {
        if (Mirror) x = 1 - x;
        return Quarters switch
        {
            1 => (1 - y, x),
            2 => (1 - x, 1 - y),
            3 => (y, 1 - x),
            _ => (x, y),
        };
    }

    /// <summary>The turn that undoes this one.</summary>
    public FrameOrientation Inverse() =>
        // A mirror is its own inverse, but it does not commute with the turns: undoing
        // "mirror then rotate" is "rotate back, then mirror", which is a mirror followed by the
        // same number of quarter turns when the mirror is folded back through them.
        Mirror ? new FrameOrientation(true, Quarters) : new FrameOrientation(false, (4 - Quarters) % 4);

    public override string ToString() =>
        IsIdentity ? "as it comes" : $"{(Mirror ? "mirrored, " : "")}{Quarters * 90}°";
}
