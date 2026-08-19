using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// The panel. Shows that the server is listening, who is attached, and every call an
    /// agent has made - and offers a way to stop it.
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
        readonly TextBlock _headline = new TextBlock();
        readonly TextBlock _detail = new TextBlock();
        readonly Button _pause = new Button();
        readonly CheckBox _guard = new CheckBox();
        readonly ItemsControl _activity = new ItemsControl();

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

            _pause.MinWidth = 76;
            _pause.Margin = new Thickness(0, 0, 6, 0);
            _pause.Click += (s, e) => { Activity.SetPaused(!Activity.Paused); Refresh(); };

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
                Content = _activity
            };
            Grid.SetRow(scroller, 3);

            var grid = new Grid { Margin = new Thickness(10) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(_headline, 0);
            Grid.SetRow(_detail, 1);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(_headline);
            grid.Children.Add(_detail);
            grid.Children.Add(buttons);
            grid.Children.Add(scroller);

            Content = grid;

            // Subscribe on load rather than once in the constructor: WPF raises Unloaded
            // whenever this tab stops being the visible one, so unsubscribing there
            // without re-subscribing would leave the panel frozen after the first time
            // someone clicked another tab.
            Loaded += (s, e) => { Activity.Changed += OnChanged; Refresh(); };
            Unloaded += (s, e) => Activity.Changed -= OnChanged;
            Refresh();
        }

        void OnChanged()
        {
            if (Dispatcher.CheckAccess())
            {
                Refresh();
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
            Dispatcher.BeginInvoke(new Action(Refresh));
#pragma warning restore VSTHRD001, VSTHRD110
        }

        void Refresh()
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

            _activity.Items.Clear();
            foreach (var entry in Activity.Recent(200)) _activity.Items.Add(Row(entry));

            if (_activity.Items.Count == 0)
                _activity.Items.Add(Dim("Nothing yet. Calls an agent makes will appear here."));
        }

        static UIElement Row(ActivityEntry entry)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

            line.Children.Add(Cell(entry.When.ToString("HH:mm:ss", CultureInfo.InvariantCulture), 62, 0.55));
            line.Children.Add(Cell(entry.Tool, 150, 1.0, entry.Failed));
            line.Children.Add(Cell(Duration(entry.Milliseconds), 70, 0.55));
            if (!string.IsNullOrEmpty(entry.Detail)) line.Children.Add(Cell(entry.Detail, 0, 0.75));

            return line;
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
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 12
            };

            if (width > 0) block.Width = width;
            if (failed) block.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
            else block.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            return block;
        }

        static UIElement Dim(string text)
        {
            var block = new TextBlock { Text = text, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            return block;
        }
    }
}
