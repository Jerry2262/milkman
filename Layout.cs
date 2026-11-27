// Milkman — 布局几何：显示器识别、DPI 换算、吸附资格与锚点定位
// 这里的成员全部是纯函数或只读查询，不持有窗口状态，便于独立验证。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Milkman
{
  static class ScreenLayout
  {
    // 版面按 144 DPI 基准表达：几何常量与字号同乘同除，比例才不会失衡。
    public const int BaseDpi = 144;

    // 吸附收起时顶部保留的提示条高度。这是鼠标唯一能点到的目标，固定为实际像素，
    // 不随 DPI 缩放：切成设计单位会在 100% 屏上缩成 3px，直接变得点不中。
    public const int Peek = 5;

    public static int Scaled(int design, float scale)
    {
      return (int)Math.Round(design * scale);
    }

    // 客户端像素坐标 → 设计单位坐标
    public static Point ToDesign(Point client, float scale)
    {
      return new Point((int)Math.Round(client.X / scale), (int)Math.Round(client.Y / scale));
    }

    // 显示器按设备名识别；找不到返回 null 由调用方兜底
    public static Screen FindScreen(string device)
    {
      if (string.IsNullOrEmpty(device)) return null;
      foreach (var screen in Screen.AllScreens)
        if (string.Equals(screen.DeviceName, device, StringComparison.OrdinalIgnoreCase)) return screen;
      return null;
    }

    // 只有顶边外侧没有其它屏幕覆盖时才允许吸附：吸附会把窗口移到屏幕外，
    // 若上方还有屏幕，收起的浮窗就会压到那块屏上。纯函数，便于直接验证。
    public static bool CanDock(Rectangle screen, Rectangle[] others)
    {
      foreach (var other in others)
      {
        if (other.Top + other.Height > screen.Top) continue;                    // 该屏不在上方
        if (other.Left >= screen.Right || other.Right <= screen.Left) continue; // 横向不相交
        return false;
      }
      return true;
    }

    // 某块屏是否允许吸附：顶边外侧不能有其它屏幕
    public static bool DockableOn(Rectangle screen)
    {
      var all = Screen.AllScreens;
      var others = new Rectangle[all.Length];
      for (int i = 0; i < all.Length; i++) others[i] = all[i].Bounds;
      return CanDock(screen, others);
    }

    // 把相对偏移约束在该屏工作区内，保证窗口始终可见可点。
    // 纯函数：输入显示器区域、偏移、窗口尺寸与吸附状态，输出实际坐标。
    public static Point AnchorTo(Rectangle area, Point offset, Size size, bool topEdge)
    {
      int x = Math.Max(area.Left, Math.Min(area.Left + offset.X, area.Right - size.Width));
      int y = topEdge ? area.Top : Math.Max(area.Top, Math.Min(area.Top + offset.Y, area.Bottom - size.Height));
      return new Point(x, y);
    }
  }
}
