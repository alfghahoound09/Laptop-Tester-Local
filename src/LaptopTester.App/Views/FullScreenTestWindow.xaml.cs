using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LaptopTester.Core.Models;

namespace LaptopTester.App.Views;

public partial class FullScreenTestWindow : Window
{
    private readonly string _mode;
    private readonly bool _expressConfirmMode;
    private int _stepIndex;

    public TestStatus? ExpressVerdict { get; private set; }

    private static readonly (string Name, Color Color)[] SolidColors =
    [
        ("Black (#000000)", Colors.Black),
        ("White (#FFFFFF)", Colors.White),
        ("Red (#FF0000)", Color.FromRgb(255, 0, 0)),
        ("Green (#00FF00)", Color.FromRgb(0, 255, 0)),
        ("Blue (#0000FF)", Color.FromRgb(0, 0, 255)),
        ("50% Gray (#808080)", Color.FromRgb(128, 128, 128))
    ];

    public FullScreenTestWindow(string mode, bool expressConfirmMode = false)
    {
        InitializeComponent();
        _mode = mode;
        _expressConfirmMode = expressConfirmMode;
        ApplySlide();
    }

    private void ApplySlide()
    {
        if (_mode == "Gradient")
        {
            RootGrid.Background = new LinearGradientBrush(Colors.Black, Colors.White, 0);
            HintText.Text = "Gradient Ramp Test · Press Esc to close";
            return;
        }
        if (_mode == "Grid")
        {
            var dg = new DrawingGroup();
            using (var dc = dg.Open())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 40, 40));
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(80, 80, 80)), 1);
                dc.DrawLine(pen, new Point(0, 0), new Point(40, 0));
                dc.DrawLine(pen, new Point(0, 0), new Point(0, 40));
            }
            RootGrid.Background = new DrawingBrush(dg)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 40, 40),
                ViewportUnits = BrushMappingMode.Absolute
            };
            HintText.Text = "Pixel Alignment Grid · Press Esc to close";
            return;
        }

        var (name, color) = SolidColors[_stepIndex % SolidColors.Length];
        RootGrid.Background = new SolidColorBrush(color);
        HintText.Text = _expressConfirmMode
            ? $"{name}  ·  Space / Click = Next  ·  P = Pass  ·  F = Fail  ·  Esc = Exit"
            : $"{name}  ·  Click / Space = Next  ·  Right-Click = Prev  ·  Esc = Exit";
    }

    private void NextSlide()
    {
        _stepIndex = (_stepIndex + 1) % SolidColors.Length;
        ApplySlide();
    }

    private void PrevSlide()
    {
        _stepIndex = (_stepIndex - 1 + SolidColors.Length) % SolidColors.Length;
        ApplySlide();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => NextSlide();
    private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e) => PrevSlide();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (_expressConfirmMode && e.Key == Key.P)
        {
            ExpressVerdict = TestStatus.Passed;
            Close();
        }
        else if (_expressConfirmMode && e.Key == Key.F)
        {
            ExpressVerdict = TestStatus.Failed;
            Close();
        }
        else if (!_expressConfirmMode && e.Key == Key.Enter)
        {
            NextSlide();
        }
        else if (_expressConfirmMode && e.Key == Key.Enter)
        {
            ExpressVerdict = TestStatus.Passed;
            Close();
        }
        else if (e.Key is Key.Space or Key.Right)
        {
            NextSlide();
        }
        else if (e.Key == Key.Left)
        {
            PrevSlide();
        }
    }
}
