using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    public partial class ShimIntegrationTests
    {
        const string CapturePng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=";

        [Theory]
        [InlineData(null)]
        [InlineData("10,20,1,1")]
        public async Task Capture_returns_visual_content_and_reports_only_a_summary(string region)
        {
            _host.NextCapture = new CaptureResult { Width = 1, Height = 1, Base64 = CapturePng };
            var result = await new EvidenceTools(_sessions).Capture(region);
            Assert.False(result.IsError);
            Assert.Equal(2, result.Content.Count);
            var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
            Assert.Contains("1x1 PNG", text);
            Assert.DoesNotContain(CapturePng, text);
            var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(CapturePng, Encoding.UTF8.GetString(image.Data.Span));
            Assert.Equal(Convert.FromBase64String(CapturePng), image.DecodedData.ToArray());
            if (region == null)
            {
                Assert.Null(_host.LastCaptureRegion);
                Assert.Contains("whole window", text);
            }
            else
            {
                Assert.Equal(new[] { 10, 20, 1, 1 }, _host.LastCaptureRegion);
                Assert.Contains("requested region: " + region, text);
            }
            await Task.Delay(250);
            var report = _host.ReportFor("capture");
            Assert.Equal(text, report.Result);
            Assert.False(report.Failed);
        }

        [Theory]
        [InlineData("1,2", "region must be x,y,width,height.")]
        [InlineData("a,2,3,4", "region must be four numbers.")]
        public async Task Capture_rejects_malformed_regions_without_capturing(string region, string message)
        {
            var result = await new EvidenceTools(_sessions).Capture(region);
            Assert.True(result.IsError);
            Assert.Equal(message, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.DoesNotContain("capture", _host.Calls);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("empty")]
        [InlineData("host-error")]
        public async Task Capture_failures_have_no_image(string kind)
        {
            _host.NextCapture = kind == "missing" ? null : kind == "empty" ? new CaptureResult() :
                new CaptureResult { Error = "That process has no visible top-level window.", Base64 = CapturePng };
            var result = await new EvidenceTools(_sessions).Capture();
            Assert.True(result.IsError);
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            await Task.Delay(250);
            Assert.True(_host.ReportFor("capture").Failed);
        }

        [Fact]
        public async Task Capture_routing_failures_are_text_errors()
        {
            var result = await new EvidenceTools(_sessions).Capture(instance: "Missing#999999");
            Assert.True(result.IsError);
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            Assert.DoesNotContain("capture", _host.Calls);
        }

        [Fact]
        public async Task Capture_is_native_image_content_over_stdio_with_a_compact_schema()
        {
            _host.NextCapture = new CaptureResult { Width = 1, Height = 1, Base64 = CapturePng };
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            start.ArgumentList.Add(typeof(EvidenceTools).Assembly.Location);
            start.ArgumentList.Add("--cwd");
            start.ArgumentList.Add(@"D:\repo\Engine\src");
            start.Environment["VSDBGMCP_DATA_DIR"] = _dir;
            using var process = Process.Start(start);
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                var id = 0;
                async Task<JsonElement> Request(string method, object arguments)
                {
                    var requestId = ++id;
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0", id = requestId, method, @params = arguments
                    }));
                    await process.StandardInput.FlushAsync();
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (true)
                    {
                        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(deadline - DateTime.UtcNow);
                        Assert.NotNull(line);
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != requestId) continue;
                        Assert.False(root.TryGetProperty("error", out _), line);
                        return root.GetProperty("result").Clone();
                    }
                }

                await Request("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "capture-test", version = "1" } });
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                var catalog = await Request("tools/list", new { });
                foreach (var name in new[] { "profile_status", "debug_state" })
                {
                    var schema = catalog.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name).GetRawText();
                    Assert.DoesNotContain("collectorSessionId", schema, StringComparison.OrdinalIgnoreCase);
                }
                var tool = catalog.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "capture");
                Assert.Equal(new[] { "instance", "region" }, tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n));
                Assert.False(tool.TryGetProperty("outputSchema", out _));
                Assert.True(tool.GetRawText().Length < 2000);

                var result = await Request("tools/call", new { name = "capture", arguments = new { } });
                Assert.False(result.GetProperty("isError").GetBoolean());
                var content = result.GetProperty("content");
                Assert.Equal(2, content.GetArrayLength());
                Assert.Equal("text", content[0].GetProperty("type").GetString());
                Assert.DoesNotContain(CapturePng, content[0].GetProperty("text").GetString());
                Assert.Equal("image", content[1].GetProperty("type").GetString());
                Assert.Equal("image/png", content[1].GetProperty("mimeType").GetString());
                Assert.Equal(CapturePng, content[1].GetProperty("data").GetString());
                Assert.False(result.TryGetProperty("structuredContent", out _));

                _host.NextCapture = new CaptureResult { Error = "Nothing is being debugged." };
                result = await Request("tools/call", new { name = "capture", arguments = new { } });
                Assert.True(result.GetProperty("isError").GetBoolean());
                Assert.Equal(1, result.GetProperty("content").GetArrayLength());
                Assert.Contains("Nothing is being debugged", result.GetProperty("content")[0].GetProperty("text").GetString());
            }
            finally
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5000)) process.Kill(true);
                await process.WaitForExitAsync();
                await stderr;
            }
        }
    }
}
