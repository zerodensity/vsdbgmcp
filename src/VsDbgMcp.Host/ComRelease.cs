using System.Runtime.InteropServices;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Lets go of a debug engine object as soon as a read is done with it.
    ///
    /// Every frame, context and property the engine hands out is held by a COM wrapper
    /// until the garbage collector gets to it, and Visual Studio's collector gets to
    /// them seldom. A thread listing creates thousands, and the engine's memory behind
    /// them stayed allocated call after call.
    ///
    /// Each release undoes one reference this code received, so an object that is also
    /// held elsewhere - the same frame reached twice - keeps the references it still
    /// has. Release only what this code was handed and does not keep.
    /// </summary>
    static class ComRelease
    {
        public static void Release(object received)
        {
            if (received != null && Marshal.IsComObject(received)) Marshal.ReleaseComObject(received);
        }
    }
}
