using System;

namespace VsDbgMcp
{
    /// <summary>
    /// Which stack frame a read happens in when the innermost one cannot be read at all.
    ///
    /// After a pause Visual Studio puts a row of its own on top of the stack -
    /// "[Application execution paused, double-click to view all thread stacks]" - and that
    /// row has no expression context. Every eval in it failed with three words that named
    /// no way out, and every vars came back empty, which reads as "no locals here" and is
    /// a different statement from "this frame could not be read".
    /// </summary>
    public static class FrameChoice
    {
        /// <summary>
        /// The bare fact, for the places that have only a frame in hand. Anywhere the
        /// stack is reachable, say which frame would have worked instead.
        /// </summary>
        public const string NoContext = "this frame has no expression context";

        /// <summary>
        /// The frame nearest <paramref name="from"/> that can be evaluated in, or null when
        /// none of the first <paramref name="count"/> can. Equal distances go to the inner
        /// frame, which is the one closer to what the caller was looking at.
        /// </summary>
        public static int? Nearest(int from, int count, Func<int, bool> canEvaluate)
        {
            int? best = null;
            for (var i = 0; i < count; i++)
            {
                if (!canEvaluate(i)) continue;
                if (best == null || Math.Abs(i - from) < Math.Abs(best.Value - from)) best = i;
            }
            return best;
        }

        /// <summary>
        /// The same, preferring a frame that has source of its own.
        ///
        /// Under the pause row sit the frames of the thread's way into the kernel -
        /// ntdll, KernelBase - and an expression context exists in every one of them.
        /// Landing on the first of those answers 1+1 and nothing else: it has no locals,
        /// so vars comes back empty for a reason that has nothing to do with the program,
        /// which is the answer this whole thing exists to stop giving. The frames with
        /// code of their own are looked at first, and the rest only if there are none.
        /// </summary>
        public static int? Nearest(int from, int count, Func<int, bool> canEvaluate, Func<int, bool> hasSource) =>
            Nearest(from, count, i => canEvaluate(i) && hasSource(i)) ?? Nearest(from, count, canEvaluate);

        /// <summary>
        /// What to tell a caller whose own choice of frame cannot be evaluated in. The
        /// choice stands, so this names the frame that would have worked rather than
        /// leaving it to be found by trying frames one at a time.
        /// </summary>
        public static string CannotEvaluate(int? nearest, string nearestName, int looked)
        {
            if (nearest == null)
            {
                return NoContext + ", and neither does any of the first " + looked +
                       " frames on this thread";
            }

            return NoContext + ". Frame " + nearest.Value + Named(nearestName) +
                   " does: pass frame " + nearest.Value + ", or select(frame: " + nearest.Value + ")";
        }

        /// <summary>
        /// What to say when a read moved off a frame nobody chose. Saying nothing would
        /// report a value as belonging to a frame it did not come from.
        /// </summary>
        public static string Moved(int from, int to, string toName) =>
            "frame " + from + " has no expression context, so this was read in frame " + to +
            Named(toName) + ", the nearest one with code of its own. After a pause the innermost " +
            "frame is Visual Studio's own and holds nothing, and the frames under it belong to " +
            "the system libraries the thread is parked in. select(frame: N) pins a frame of your own.";

        /// <summary>The stack ran out before the frame the caller named.</summary>
        public static string NoSuchFrame(int index, int count) =>
            "there is no frame " + index + " on this thread; it has " + count +
            (count == 1 ? " frame" : " frames") + ". 'stack' lists them.";

        static string Named(string name) => string.IsNullOrEmpty(name) ? "" : " (" + name + ")";
    }
}
