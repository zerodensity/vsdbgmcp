using System.Threading.Tasks;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// A tool that failed says so by throwing, which is how this SDK marks a result as
    /// an error. A test about the words of a refusal has to catch it to read them.
    /// </summary>
    static class Failure
    {
        public static async Task<string> Text(Task<string> call) =>
            (await Assert.ThrowsAsync<ToolFailure>(() => call)).Message;
    }
}
