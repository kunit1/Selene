// console-style Left/Right for the "SeleneGameRow" list on the old theme's Welcome Hub.
// the old theme plugin handles every Left/Right on the hub itself (nearest focusable element, else next page),
// which skips tiles and jumps pages. A Window class handler runs before the old theme plugin's instance handler,
// so for focus inside SeleneGameRow we move exactly one visible tile, keep it at the left edge, and mark
// the key handled. Everything else is left to the old theme plugin.
// Loaded by the ClaudeLibraryTools extension via Add-Type (C# 5: no ?. or string interpolation).
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Selene
{
    public static class HomeNav
    {
        // existing code unchanged...

        private static void OnGlideFrame(object sender, EventArgs e)
        {
            if (!Ui.IsMainWindowVisible()) { StopGlide(); return; }
            var shift = glideShift;
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == glideLast) return;           // Rendering can fire more than once per frame
            double dt = glideLast == TimeSpan.Zero ? 1.0 / 144 : Math.Min(0.05, (now - glideLast).TotalSeconds);
            glideLast = now;
            if (shift == null) { StopGlide(); return; }
            double rest = glideRest;
            double x0 = shift.X - rest, v0 = glideV, w = GlideOmega, k = Math.Exp(-w * dt), b = v0 + w * x0;
            double x = (x0 + b * dt) * k;
            glideV = (v0 - w * b * dt) * k;
            if (Math.Abs(x) < 0.3 && Math.Abs(glideV) < 6) { shift.X = rest; StopGlide(); return; }
            shift.X = x + rest;
        }

        private static void StopGlide()
        {
            glideV = 0;
            if (!glideHooked) return;
            glideHooked = false;
            CompositionTarget.Rendering -= OnGlideFrame;
            glideClock.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }
}
