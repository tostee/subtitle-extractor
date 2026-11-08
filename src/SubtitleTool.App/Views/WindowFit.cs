using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SubtitleTool.App.Views;

/// <summary>
/// Que la ventana quepa en pantalla: con escalado (p. ej. 1920×1080 al 150 % = 1280×720 DIP) el tamaño por defecto
/// puede superar el área de trabajo y dejar los botones de abajo fuera de la vista.
/// </summary>
public static class WindowFit
{
    /// <summary>Llamar en el constructor de la ventana, tras <c>InitializeComponent</c>.</summary>
    public static void Attach(Window window)
    {
        // Antes de mostrarla: limita el tamaño al área de trabajo principal (así CenterOwner/CenterScreen ya la centran bien).
        Clamp(window, SystemParameters.WorkArea);
        // Ya mostrada: ajusta tamaño y posición al monitor en el que quedó (puede no ser el principal).
        window.Loaded += (_, _) =>
        {
            if (MonitorWorkArea(window) is { } area)
                Clamp(window, area, position: true);
        };
    }

    private static void Clamp(Window w, Rect area, bool position = false)
    {
        if (w.WindowState != WindowState.Normal || area.IsEmpty || area.Width <= 0 || area.Height <= 0)
            return;
        w.MinWidth = Math.Min(w.MinWidth, area.Width);
        w.MinHeight = Math.Min(w.MinHeight, area.Height);
        var width = double.IsNaN(w.Width) ? w.ActualWidth : w.Width;
        var height = double.IsNaN(w.Height) ? w.ActualHeight : w.Height;
        if (width > area.Width)
            w.Width = width = area.Width;
        if (height > area.Height)
            w.Height = height = area.Height;
        if (!position)
            return;
        if (w.Left + width > area.Right)
            w.Left = area.Right - width;
        if (w.Top + height > area.Bottom)
            w.Top = area.Bottom - height;
        if (w.Left < area.Left)
            w.Left = area.Left;
        if (w.Top < area.Top)
            w.Top = area.Top;
    }

    /// <summary>Área de trabajo (sin barra de tareas) del monitor de la ventana, en unidades WPF (DIP).</summary>
    private static Rect? MonitorWorkArea(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
            return null;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return null;
        var dpi = VisualTreeHelper.GetDpi(w);
        var r = info.Work;
        return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY,
            (r.Right - r.Left) / dpi.DpiScaleX, (r.Bottom - r.Top) / dpi.DpiScaleY);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
