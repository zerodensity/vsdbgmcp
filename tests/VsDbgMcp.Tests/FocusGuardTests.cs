using VsDbgMcp.Contracts;
using VsDbgMcp.Host;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Handing the foreground to a window whose threads the debugger has suspended
    /// froze Visual Studio for good. A debugged process's window gets it back only
    /// once the program runs again.
    /// </summary>
    public class FocusGuardTests
    {
        [Fact]
        public void A_stopped_debuggee_window_does_not_get_the_foreground_back() =>
            Assert.False(FocusGuard.ShouldHandBack(DebugModes.Break, ownerIsDebugged: true));

        [Fact]
        public void A_running_debuggee_window_gets_the_foreground_back() =>
            Assert.True(FocusGuard.ShouldHandBack(DebugModes.Run, ownerIsDebugged: true));

        [Fact]
        public void Any_other_window_gets_the_foreground_back_at_a_stop() =>
            Assert.True(FocusGuard.ShouldHandBack(DebugModes.Break, ownerIsDebugged: false));

        [Fact]
        public void A_debuggee_window_does_not_get_the_foreground_back_when_debugging_ends() =>
            Assert.False(FocusGuard.ShouldHandBack(DebugModes.Design, ownerIsDebugged: true));
    }
}
