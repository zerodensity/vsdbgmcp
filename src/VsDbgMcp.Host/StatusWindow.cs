using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// The panel. Shows that the server is listening, who is attached, and every call an
    /// agent has made - with what it asked and what it got back - and offers a way to
    /// stop it.
    /// </summary>
    [Guid("350ab7fd-e060-4321-ae93-7c4492e67cb3")]
    public sealed class StatusWindow : ToolWindowPane
    {
        public StatusWindow() : base(null)
        {
            Caption = "VS Debugger MCP";
            Content = new StatusControl();
        }
    }

    sealed class StatusControl : UserControl
    {
        const int MaxRows = 200;

        readonly TextBlock _headline = new TextBlock();
        readonly TextBlock _detail = new TextBlock();
        readonly TextBlock _setup = new TextBlock();
        readonly Button _pause = new Button();
        readonly CheckBox _guard = new CheckBox();
        readonly StackPanel _rows = new StackPanel();

        static readonly FontFamily Mono = new FontFamily("Consolas, Cascadia Mono, Courier New");
        static readonly SolidColorBrush FailedBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));

        /// <summary>The newest entry already drawn, so a redraw adds only what is new.</summary>
        long _drawn;

        /// <summary>Set once the shim is on disk, so a redraw stops going to the disk.</summary>
        bool _shimFound;

        public StatusControl()
        {
            SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

            _headline.FontWeight = FontWeights.SemiBold;
            _headline.Margin = new Thickness(0, 0, 0, 2);
            _headline.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            _detail.Opacity = 0.75;
            _detail.Margin = new Thickness(0, 0, 0, 8);
            _detail.TextWrapping = TextWrapping.Wrap;
            _detail.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            _setup.Opacity = 0.75;
            _setup.Margin = new Thickness(0, 0, 0, 8);
            _setup.TextWrapping = TextWrapping.Wrap;
            _setup.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            _pause.MinWidth = 76;
            _pause.Margin = new Thickness(0, 0, 6, 0);
            _pause.Click += (s, e) => { Activity.SetPaused(!Activity.Paused); RefreshHeader(); };

            _guard.Content = "Don't steal focus";
            _guard.ToolTip =
                "When an agent starts, resumes or steps the program, put the window you were " +
                "using back in front instead of letting Visual Studio come forward.\n\n" +
                "Stops you cause yourself are never affected.";
            _guard.VerticalAlignment = VerticalAlignment.Center;
            _guard.Margin = new Thickness(0, 0, 12, 0);
            _guard.IsChecked = Activity.GuardFocus;
            _guard.Checked += (s, e) => Activity.SetGuardFocus(true);
            _guard.Unchecked += (s, e) => Activity.SetGuardFocus(false);
            _guard.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

            var clear = new Button { Content = "Clear", MinWidth = 60 };
            clear.Click += (s, e) => Activity.Clear();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            buttons.Children.Add(_pause);
            buttons.Children.Add(_guard);
            buttons.Children.Add(clear);

            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _rows
            };
            Grid.SetRow(scroller, 4);

            var grid = new Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(_headline, 0);
            Grid.SetRow(_detail, 1);
            Grid.SetRow(_setup, 2);
            Grid.SetRow(buttons, 3);
            grid.Children.Add(_headline);
            grid.Children.Add(_detail);
            grid.Children.Add(_setup);
            grid.Children.Add(buttons);
            grid.Children.Add(scroller);

            Content = grid;

            // Subscribe on load rather than once in the constructor: WPF raises Unloaded
            // whenever this tab stops being the visible one, so unsubscribing there
            // without re-subscribing would leave the panel frozen after the first time
            // someone clicked another tab.
            Loaded += (s, e) => { Activity.Changed += OnChanged; Redraw(); };
            Unloaded += (s, e) => Activity.Changed -= OnChanged;
            Redraw();
        }

        void OnChanged()
        {
            if (Dispatcher.CheckAccess())
            {
                Redraw();
                return;
            }

            // Activity is recorded from whichever thread served the call, so hop to the
            // one that owns these controls.
            //
            // VSTHRD001 asks for the joinable task factory instead. That advice is for
            // shell code that has to co-operate with Visual Studio's own blocking waits.
            // This is a WPF control marshalling to its own dispatcher, which cannot
            // deadlock against anything and is what Dispatcher exists for.
#pragma warning disable VSTHRD001, VSTHRD110
            Dispatcher.BeginInvoke(new Action(Redraw));
#pragma warning restore VSTHRD001, VSTHRD110
        }

        void RefreshHeader()
        {
            _headline.Text = Activity.Paused
                ? "Paused - the agent cannot touch the debugger"
                : Activity.Clients > 0
                    ? "Listening, " + Activity.Clients + (Activity.Clients == 1 ? " client attached" : " clients attached")
                    : "Listening, no client attached";

            _detail.Text = (Activity.InstanceId ?? "(no solution)") +
                           "  ·  " + (Activity.Mode ?? DebugModes.Design) +
                           "\n" + (Activity.PipeName ?? "");

            _pause.Content = Activity.Paused ? "Resume" : "Pause";
            _guard.IsChecked = Activity.GuardFocus;
            RefreshSetup();
        }

        /// <summary>
        /// What to put in the agent's configuration. It is one absolute path, set once
        /// and globally, so the only thing the reader needs from this panel is a way to
        /// get that path onto the clipboard without retyping it.
        /// </summary>
        void RefreshSetup()
        {
            if (_shimFound) return;

            _setup.Inlines.Clear();
            _shimFound = File.Exists(Names.ShimExe);

            if (!_shimFound)
            {
                _setup.Inlines.Add(new Run("The shim is not on disk yet - restart Visual Studio.")
                {
                    Foreground = FailedBrush
                });
                return;
            }

            _setup.Inlines.Add(new Run("Agent setup:  "));
            _setup.Inlines.Add(CopyLink("copy command",
                "claude mcp add -s user vsdbg -- \"" + Names.ShimExe + "\""));
            _setup.Inlines.Add(new Run("   ·   "));
            _setup.Inlines.Add(CopyLink("copy path", Names.ShimExe));
        }

        static Hyperlink CopyLink(string text, string value)
        {
            var link = new Hyperlink(new Run(text)) { ToolTip = value };
            link.Click += (s, e) =>
            {
                // Another process can hold the clipboard open. Losing a copy is not
                // worth taking Visual Studio's UI down over.
                try { Clipboard.SetText(value); }
                catch (ExternalException) { }
            };
            return link;
        }

        /// <summary>
        /// Adds the rows that appeared since last time, newest at the top.
        ///
        /// Rebuilding the list wholesale would be simpler and would also collapse every
        /// row the reader had unfolded, every time an agent made another call.
        /// </summary>
        void Redraw()
        {
            RefreshHeader();

            var fresh = Activity.Since(_drawn);
            if (fresh.Count == 0)
            {
                // A Clear resets the log, so the panel has to notice it went backwards.
                if (_rows.Children.Count > 0 && Activity.Recent(1).Count == 0)
                {
                    _rows.Children.Clear();
                    _drawn = 0;
                    ShowEmpty();
                }
                return;
            }

            RemoveEmpty();

            foreach (var entry in fresh)
            {
                _rows.Children.Insert(0, Row(entry));
                _drawn = entry.Id;
            }

            while (_rows.Children.Count > MaxRows) _rows.Children.RemoveAt(_rows.Children.Count - 1);
        }

        void ShowEmpty()
        {
            var block = new TextBlock
            {
                Text = "Nothing yet. Calls an agent makes will appear here.",
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Tag = "empty"
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            _rows.Children.Add(block);
        }

        void RemoveEmpty()
        {
            for (var i = _rows.Children.Count - 1; i >= 0; i--)
            {
                if (_rows.Children[i] is FrameworkElement e && (string)e.Tag == "empty")
                    _rows.Children.RemoveAt(i);
            }
        }

        /// <summary>
        /// A call with something to show folds open; one without stays a plain line, so
        /// there is no chevron promising a reply that does not exist.
        /// </summary>
        UIElement Row(ActivityEntry entry)
        {
            var header = Header(entry);
            if (!entry.HasResult) return header;

            var expander = new Expander
            {
                Header = header,
                Margin = new Thickness(0, 1, 0, 1),
                Content = Body(entry.Result)
            };
            expander.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            return expander;
        }

        StackPanel Header(ActivityEntry entry)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

            line.Children.Add(Cell(entry.When.ToString("HH:mm:ss", CultureInfo.InvariantCulture), 62, 0.55));
            line.Children.Add(Cell(entry.Tool, 118, 1.0, entry.Failed));
            line.Children.Add(Cell(Duration(entry.Milliseconds), 62, 0.55));
            if (!string.IsNullOrEmpty(entry.Detail)) line.Children.Add(Cell(Shorten(entry.Detail), 0, 0.8));

            return line;
        }

        UIElement Body(string result)
        {
            // A read-only text box rather than a label, because the first thing anyone
            // wants from a reply on screen is to copy part of it.
            var text = new TextBox
            {
                Text = result,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontFamily = Mono,
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(18, 2, 0, 6)
            };

            text.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
            return text;
        }

        static string Shorten(string detail)
        {
            const int limit = 60;
            if (detail.Length <= limit) return detail;
            return detail.Substring(0, limit) + "...";
        }

        static string Duration(int ms)
        {
            if (ms <= 0) return "";
            if (ms < 1000) return ms + " ms";
            return (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        static TextBlock Cell(string text, double width, double opacity, bool failed = false)
        {
            var block = new TextBlock
            {
                Text = text,
                Opacity = opacity,
                FontFamily = Mono,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (width > 0) block.Width = width;
            if (failed) block.Foreground = FailedBrush;
            else block.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            return block;
        }
    }
}
